using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>
/// Forever's visual-only loop: find a unit among the enemy nameplates in view,
/// click it, close in, fight, loot and rest.
/// </summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Start, Search, Select, Approach, Fight, Loot, Rest, Recover, Complete, Failed }

    // Give the flags a moment to follow a clear-target press before another:
    // the fallback is Escape, which opens the game menu with nothing selected.
    private const int ClearTargetRetryMs = 1000;
    // Looking around is a calm scan: this share of full steering speed.
    private const double LookAroundRate = 0.25;
    // After a full look-around with nothing to take, walk on this long first.
    private const int RoamMs = 8000;
    // Reach flickers at its edges: the action rows refresh every 0.5 s and
    // the target distance every 0.1 s, and a mob closing in crosses a ranged
    // weapon's minimum range between the two. The fight leaves for pursuit
    // only once reach stayed lost this long.
    private const int ReachLostMs = 1000;
    // Combat lingers a moment after the last kill; with no attacker in view
    // the character stands this long before looking for one out of view.
    private const int CombatLingersMs = 3000;

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly IForeverLootRoutine loot;
    private readonly ForeverCorpses corpses;
    private readonly ForeverRunOptions runOptions;
    private readonly ClassConfiguration config;
    private readonly CancellationToken token;
    private readonly ForeverStateMachine<Phase> machine;
    private readonly ForeverExclusions exclusions = new();
    private readonly ForeverRecovery recovery = new();
    private volatile bool stopped;
    private volatile int completedKills;
    private long lastClearTarget;
    private long lastActionFrame;
    private long invalidFrameSince;
    private VisualNameplate? candidate;
    private string setback = string.Empty;
    // The combat flag of the frame being handled, for setbacks and time bounds.
    private bool frameInCombat;
    private double lookAroundTurned;
    private double? lookAroundFacing;
    private long roamSince;
    private VisualTargetIdentity? target;
    private long targetSelectedAt;
    private long targetUnreadableAt;
    private long targetRestoreAt;
    private long reachLostAt;
    private long lastKillAt;
    private int deathSequenceBefore;
    private volatile int observedDeaths;
    private volatile int completedLoots;

    public override float Cost => 1;
    public override bool CanRun() => machine.State != Phase.Failed && !stopped;
    public string Status => machine.State.ToString();
    public string LastFailure => machine.LastFailure;
    public int CompletedKills => completedKills;
    public int ObservedDeaths => observedDeaths;
    public int CompletedLoots => completedLoots;

    public ForeverAutonomousGoal(ILogger<ForeverAutonomousGoal> logger,
        ForeverVisualBarTracker tracker,
        ForeverTargetSelector selector, ForeverNavigator navigator,
        IForeverCombatRoutine combat, IForeverLootRoutine loot, ForeverCorpses corpses,
        ForeverRunOptions runOptions, ClassConfiguration config,
        CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverAutonomousGoal))
    {
        this.logger = logger;
        this.tracker = tracker;
        this.selector = selector;
        this.navigator = navigator;
        this.combat = combat;
        this.loot = loot;
        this.corpses = corpses;
        this.runOptions = runOptions;
        this.config = config;
        token = cts.Token;

        // The whole run: each phase, where it may go next, its hard bound and
        // what it releases. Bounds that depend on what the phase sees stay in
        // the phase itself.
        machine = new ForeverStateMachine<Phase>(logger, "Forever controller",
                Phase.Start, Phase.Failed, Phase.Complete)
            .On(Phase.Start, Start, exits: [Phase.Search])
            .On(Phase.Search, Search, exits: [Phase.Select, Phase.Fight, Phase.Loot, Phase.Complete],
                timeoutMs: 45000, onTimeout: "no safe target found within acquisition time",
                enter: ResetLookAround, exit: navigator.StopPursuit)
            .On(Phase.Select, Select, exits: [Phase.Approach, Phase.Recover],
                timeoutMs: 8000, onTimeout: "selecting the chosen unit did not finish",
                timedOut: reason => SetBack(ForeverSetback.SelectFailed, reason),
                enter: navigator.StopPursuit)
            .On(Phase.Approach, Approach, exits: [Phase.Fight, Phase.Search, Phase.Loot, Phase.Rest, Phase.Recover],
                timeoutMs: 20000, onTimeout: "target did not enter spell range within local pursuit bound",
                timedOut: reason => SetBack(ForeverSetback.OutOfReach, reason),
                exit: navigator.StopPursuit)
            .On(Phase.Fight, Fight, exits: [Phase.Approach, Phase.Search, Phase.Loot, Phase.Rest],
                timeoutMs: 75000, onTimeout: "fight did not end within the combat bound",
                // Both entries, from pursuit and from aggro, start with no
                // pending restore, unreadable-identity or lost-reach window from earlier.
                enter: () =>
                {
                    targetRestoreAt = 0;
                    targetUnreadableAt = 0;
                    reachLostAt = 0;
                })
            .On(Phase.Loot, Loot, exits: [Phase.Rest, Phase.Search],
                timeoutMs: 120000, onTimeout: "the loot round did not finish",
                timedOut: LootTimedOut, exit: navigator.StopPursuit)
            .On(Phase.Rest, Rest, exits: [Phase.Search, Phase.Complete])
            .On(Phase.Recover, Recover, exits: [Phase.Search], enter: navigator.StopPursuit)
            .On(Phase.Failed, enter: navigator.StopPursuit);
    }

    public bool TryReadOnlyPreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) || bars.TargetFlags is not { } flags)
        {
            reason = "no complete visual frame from a verified class on a verified map";
            return false;
        }
        if (flags.PlayerCombat)
        {
            reason = "the player is in combat";
            return false;
        }
        if (!ForeverFrame.CanOpenFight(bars, inCombat: false))
        {
            reason = $"health {bars.PlayerHealth:P0} or mana {bars.PlayerPower:P0} is below what opening a fight needs";
            return false;
        }
        if (bars.LastDeathEvent is null ||
            config.Loot && (bars.Loot is null || !ForeverBindings.InteractTarget.IsBoundIn(bars)))
        {
            reason = "the death or loot telemetry, or the interact binding, is incomplete";
            return false;
        }
        if (bars.Nameplates is not { } roster)
        {
            reason = "the nameplate roster is unreadable; reload the ForeverProbe add-on that ships with this build";
            return false;
        }
        if (!roster.PlatesShown)
        {
            reason = "enemy nameplates are not always shown; turn on enemy nameplates and Always Show Nameplates in the client";
            return false;
        }
        if (bars.Attack is null)
        {
            reason = "the attack state is unreadable; reload the ForeverProbe add-on that ships with this build";
            return false;
        }
        // Bodies are found with the client's Interact Key and emptied by its Auto Loot.
        if (config.Loot)
        {
            if (bars.SoftInteract is not { } soft)
            {
                reason = "the soft-interact state is unreadable; reload the ForeverProbe add-on that ships with this build";
                return false;
            }
            if (!soft.InteractKeyEnabled)
            {
                reason = "the Interact Key is off; turn on Enable Interact Key in the client's settings";
                return false;
            }
            if (soft.AutoLoot != true)
            {
                reason = "Auto Loot is off; turn on Auto Loot in the client's settings";
                return false;
            }
        }
        return combat.TryPreflight(bars, out reason) &&
            navigator.TryReadOnlyPreflight(bars, out reason);
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is AbortEvent)
        {
            // This runs on the thread that stopped the controller, possibly
            // while Update is still running. Halting the navigator stops that
            // update from pressing movement again, so a stopped controller
            // never resumes; load the profile again for a new run.
            stopped = true;
            navigator.Halt();
            if (CompletedKills == 0)
                machine.Fail("Controller stopped");
        }
    }

    public override void Update()
    {
        if (!CanRun() || machine.IsTerminal || token.IsCancellationRequested)
            return;
        try
        {
            if (!tracker.TryGetLatest(out VisualBarSample bars) ||
                !ForeverFrame.IsVerifiedSetup(bars))
            {
                navigator.StopPursuit();
                if (invalidFrameSince == 0)
                    invalidFrameSince = GetTimestamp();
                if (GetElapsedTime(invalidFrameSince).TotalMilliseconds > 5000)
                    machine.Fail("visual controller frame unavailable for five continuous seconds");
                return;
            }
            invalidFrameSince = 0;
            if (bars.FrameId == lastActionFrame)
                return;
            lastActionFrame = bars.FrameId;
            VisualTargetFlags flags = bars.TargetFlags!.Value;
            if (flags.PlayerDead)
            {
                machine.Fail("player died; corpse recovery has not been verified");
                return;
            }
            frameInCombat = flags.PlayerCombat;
            // Where every unit in view is, so a body can be found once the fight is over.
            corpses.Observe(bars);
            Phase phase = machine.State;
            if (phase is Phase.Approach or Phase.Fight && target is { } killed &&
                (bars.LastDeathEvent is { } death && death.Confirms(killed, deathSequenceBefore) ||
                 bars.TargetIdentity == killed && flags.Present && flags.Dead))
            {
                TargetDied(bars);
                return;
            }
            // The profile's Adhoc self heals and buffs, in every phase that acts
            // and also while looting in combat. A corpse selected out of combat
            // is looted or cleared first. A failed cast latches the caster
            // closed; say so here rather than at the next action it refuses.
            if ((phase is Phase.Search or Phase.Select or Phase.Approach or Phase.Fight or Phase.Rest ||
                    phase == Phase.Loot && flags.PlayerCombat) &&
                !(flags.Present && flags.Dead && !flags.PlayerCombat) &&
                !combat.RunAdhoc(bars, navigator.StopPursuit, out string adhocReason))
            {
                navigator.StopPursuit();
                if (!string.IsNullOrEmpty(adhocReason))
                    machine.Fail(adhocReason);
                return;
            }

            machine.Tick(bars);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forever autonomous controller stopped");
            machine.Fail("controller update threw an exception");
        }
    }

    private void Start(VisualBarSample bars)
    {
        if (!TryReadOnlyPreflight(out string reason))
        {
            if (machine.ElapsedMs > 5000)
                machine.Fail(reason);
            return;
        }
        machine.Go(Phase.Search);
    }

    // The nameplates show only what the camera sees, so finding a unit means
    // looking around: take one in view, else turn a full circle standing, then
    // walk on a stretch and look again.
    private void Search(VisualBarSample bars)
    {
        bool inCombat = bars.TargetFlags!.Value.PlayerCombat;
        // The fight is over: its bodies are looted before the next one.
        if (!inCombat && config.Loot && corpses.Any)
        {
            BeginLoot(bars);
            return;
        }
        // Aggro can begin before our first cast: a selected unit in reach is
        // fought as it is, without a click.
        if (inCombat && ForeverTargetPolicy.TryEligibleTarget(bars, config, out VisualTargetIdentity selected) &&
            combat.InRange(bars, selected))
        {
            Lock(bars, selected);
            machine.Go(Phase.Fight);
            return;
        }
        if (!inCombat && !ForeverFrame.CanOpenFight(bars, inCombat: false))
        {
            navigator.StopPursuit();
            return;
        }
        if (ForeverTargetPolicy.TryChoose(bars, config, combat, exclusions, out VisualNameplate chosen))
        {
            candidate = chosen;
            selector.BeginSelect(chosen);
            machine.Go(Phase.Select);
            return;
        }
        if (inCombat && lastKillAt != 0 && GetElapsedTime(lastKillAt).TotalMilliseconds < CombatLingersMs)
        {
            navigator.StopPursuit();
            return;
        }

        if (!LookedAround(bars))
        {
            if (navigator.TurnInPlace(bars, LookAroundRate, out string turnReason) == ForeverNavigationResult.Failed)
                machine.Fail(turnReason);
            return;
        }
        if (inCombat)
        {
            // Something fights the player out of view: keep looking for it.
            ResetLookAround();
            return;
        }
        if (runOptions.StopWhenNoTargets)
        {
            machine.Go(Phase.Complete);
            return;
        }
        if (roamSince == 0)
            roamSince = GetTimestamp();
        if (GetElapsedTime(roamSince).TotalMilliseconds > RoamMs)
        {
            navigator.StopPursuit();
            ResetLookAround();
            return;
        }
        switch (navigator.Roam(bars, out string roamReason))
        {
            case ForeverNavigationResult.Stuck or ForeverNavigationResult.Blocked:
                // Look around again; the walk goes on in a direction that is free.
                navigator.StopPursuit();
                ResetLookAround();
                break;
            case ForeverNavigationResult.Failed:
                machine.Fail(roamReason);
                break;
        }
    }

    private void Select(VisualBarSample bars)
    {
        if (candidate is not { } chosen)
        {
            machine.Go(Phase.Search);
            return;
        }
        string reason;
        switch (selector.Select(bars, out reason))
        {
            case ForeverClickResult.Pointing:
                return;
            case ForeverClickResult.Done:
                if (ForeverTargetPolicy.TryEligibleTarget(bars, config, out VisualTargetIdentity selected) &&
                    (!chosen.HasIdentity || chosen.Is(selected)))
                {
                    candidate = null;
                    Lock(bars, selected);
                    machine.Go(Phase.Approach);
                    return;
                }
                reason = "the unit selected is not the eligible candidate";
                break;
        }
        SetBack(ForeverSetback.SelectFailed, reason);
    }

    private void Approach(VisualBarSample bars)
    {
        // Range pixels refresh on their own 500ms cadence. Let them catch up
        // to a newly selected identity before interpreting the range bits.
        if (GetElapsedTime(targetSelectedAt).TotalMilliseconds < 650)
            return;
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present ||
            !flags.Attackable || flags.Dead)
        {
            if (flags.PlayerCombat)
                machine.Fail("selected target changed during combat pursuit");
            else
            {
                target = null;
                machine.Go(Phase.Search);
            }
            return;
        }
        if (combat.InRange(bars, selected))
        {
            machine.Go(Phase.Fight);
            return;
        }
        ForeverNavigationResult result = navigator.Approach(bars,
            selected, out string reason);
        if (result == ForeverNavigationResult.NoTargetPosition)
            result = navigator.SearchForTarget(bars, selected, out reason);
        switch (result)
        {
            // In combat a blocked way is tried again, within the approach's bound.
            case ForeverNavigationResult.Blocked when flags.PlayerCombat:
                return;
            case ForeverNavigationResult.Blocked:
                SetBack(ForeverSetback.Blocked, $"local approach is blocked: {reason}");
                return;
            case ForeverNavigationResult.Stuck:
                SetBack(ForeverSetback.Stuck, reason);
                return;
            case ForeverNavigationResult.TargetLost:
                SetBack(ForeverSetback.TargetLost, reason);
                return;
            case ForeverNavigationResult.OutOfReach:
                SetBack(ForeverSetback.OutOfReach, reason);
                return;
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                return;
        }
    }

    private void Fight(VisualBarSample bars)
    {
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (!flags.Present)
        {
            navigator.StopPursuit();
            if (targetRestoreAt == 0)
            {
                targetRestoreAt = GetTimestamp();
                selector.RestoreLastTarget();
            }
            else if (GetElapsedTime(targetRestoreAt).TotalMilliseconds > 3000)
                machine.Fail("selected target disappeared and could not be restored for the death check");
            return;
        }
        // The target is back; the next disappearance gets a fresh window.
        targetRestoreAt = 0;
        if (bars.TargetIdentity is null)
        {
            targetUnreadableAt = targetUnreadableAt == 0 ? GetTimestamp() : targetUnreadableAt;
            if (GetElapsedTime(targetUnreadableAt).TotalMilliseconds > 2000)
                machine.Fail($"combat target identity remained unreadable; flags={flags}, health={bars.TargetHealth:P0}");
            return;
        }
        targetUnreadableAt = 0;
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present)
        {
            machine.Fail($"target identity was lost during combat; expected={target}, actual={bars.TargetIdentity}, flags={flags}, health={bars.TargetHealth:P0}");
            return;
        }
        ForeverCombatResult result = combat.Update(bars, selected,
            out string reason);
        if (result != ForeverCombatResult.NeedRange)
            reachLostAt = 0;
        switch (result)
        {
            case ForeverCombatResult.NeedRange:
                if (reachLostAt == 0)
                    reachLostAt = GetTimestamp();
                if (GetElapsedTime(reachLostAt).TotalMilliseconds >= ReachLostMs)
                    machine.Go(Phase.Approach);
                return;
            case ForeverCombatResult.TargetDead:
                TargetDied(bars);
                return;
            case ForeverCombatResult.Failed:
                machine.Fail(reason);
                return;
        }
    }

    // The round goes from body to body until none is left; an attack sends
    // the run back to fighting, and the bodies wait for the next round.
    private void Loot(VisualBarSample bars)
    {
        switch (loot.Update(bars, out string reason))
        {
            case ForeverLootResult.Collected:
                completedLoots++;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever looted a body; {Count} left", corpses.Pending.Count);
                break;
            case ForeverLootResult.Done:
                machine.Go(Phase.Rest);
                break;
            case ForeverLootResult.InCombat:
                machine.Go(Phase.Search);
                break;
            case ForeverLootResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    private void LootTimedOut(string reason)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever {Reason}; leaving {Count} bodies", reason, corpses.Pending.Count);
        corpses.Clear();
        machine.Go(Phase.Rest);
    }

    // Looting waits until the fight is over; without it the run rests.
    private void BeginLoot(VisualBarSample bars)
    {
        if (!config.Loot)
        {
            machine.Go(Phase.Rest);
            return;
        }
        if (bars.Loot is null)
        {
            machine.Fail("a kill to loot, but loot event telemetry is unavailable");
            return;
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever loot round: {Count} bodies where they fell", corpses.Pending.Count);
        loot.Begin();
        machine.Go(Phase.Loot);
    }

    private void Rest(VisualBarSample bars)
    {
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (flags.Present)
        {
            if (!flags.Dead)
            {
                machine.Fail("new live target appeared during recovery");
                return;
            }
            // Any corpse is safe to deselect, including one whose identity did
            // not read back; repeat the command only after the flags had time.
            if (GetElapsedTime(lastClearTarget).TotalMilliseconds >= ClearTargetRetryMs)
            {
                selector.ClearTarget(bars);
                lastClearTarget = GetTimestamp();
            }
            if (machine.ElapsedMs > 30000)
                machine.Fail("dead target stayed selected during recovery");
            return;
        }
        if (flags.PlayerCombat)
        {
            if (machine.ElapsedMs > 30000)
                machine.Fail("combat did not clear after target death");
            return;
        }
        // Heals between fights are the profile's out-of-combat Adhoc actions,
        // which run before this phase; here only wait for them and regeneration.
        if (!ForeverFrame.IsRested(bars))
        {
            if (machine.ElapsedMs > 45000)
                machine.Fail(bars.PlayerHealth < ForeverFrame.RestedHealth
                    ? "health did not recover in time"
                    : "mana did not recover in time");
            return;
        }
        machine.Go(runOptions.MaxCompletedCycles is { } maximum && completedKills >= maximum
            ? Phase.Complete : Phase.Search);
    }

    // A verified kill. While something else still fights the player, the run
    // goes back to fighting and the body waits; once combat is over the
    // round loots every body of the fight.
    private void TargetDied(VisualBarSample bars)
    {
        navigator.StopPursuit();
        observedDeaths++;
        completedKills++;
        lastKillAt = GetTimestamp();
        recovery.Killed();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever autonomous kill {Count} verified", CompletedKills);
        target = null;
        if (bars.TargetFlags!.Value.PlayerCombat)
        {
            machine.Go(Phase.Search);
            return;
        }
        BeginLoot(bars);
    }

    // Keeps the same opponent through pursuit and combat.
    private void Lock(VisualBarSample bars, VisualTargetIdentity selected)
    {
        target = selected;
        targetSelectedAt = GetTimestamp();
        deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
    }

    // A setback with the chosen or selected unit: set it aside and search
    // again, or stop when recovery refuses (in combat, or past its budget).
    private void SetBack(ForeverSetback kind, string reason)
    {
        if (!recovery.TryRecover(kind, frameInCombat, out string refusal))
        {
            machine.Fail($"{reason} ({refusal})");
            return;
        }
        TimeSpan aside = ForeverRecovery.SetAsideFor(kind, frameInCombat);
        if (aside > TimeSpan.Zero)
        {
            if (candidate is { } plate)
                exclusions.Exclude(plate, aside);
            if (target is { } unit)
                exclusions.Exclude(unit, aside);
        }
        setback = $"{kind}: {reason}";
        candidate = null;
        target = null;
        machine.Go(Phase.Recover);
    }

    private void Recover(VisualBarSample bars)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever recovering from {Setback}; {Count} setbacks since the last kill",
                setback, recovery.SinceKill);
        machine.Go(Phase.Search);
    }

    // True once the facing has turned through a full circle since the last reset.
    private bool LookedAround(VisualBarSample bars)
    {
        double facing = bars.Position!.Value.Direction;
        if (lookAroundFacing is { } previous)
            lookAroundTurned += Math.Abs(Math.Atan2(Math.Sin(facing - previous), Math.Cos(facing - previous)));
        lookAroundFacing = facing;
        return lookAroundTurned >= 2 * Math.PI;
    }

    private void ResetLookAround()
    {
        lookAroundTurned = 0;
        lookAroundFacing = null;
        roamSince = 0;
    }
}
