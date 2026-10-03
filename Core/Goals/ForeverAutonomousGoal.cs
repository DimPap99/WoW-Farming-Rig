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
    private enum Phase { Start, Search, Select, Approach, Fight, Loot, Rest, Complete, Failed }

    // Give the flags a moment to follow a clear-target press before another:
    // the fallback is Escape, which opens the game menu with nothing selected.
    private const int ClearTargetRetryMs = 1000;
    // Looking around is a calm scan: this share of full steering speed.
    private const double LookAroundRate = 0.25;
    // After a full look-around with nothing to take, walk on this long first.
    private const int RoamMs = 8000;
    // A unit that could not be selected or reached is passed over this long.
    private static readonly TimeSpan SetAside = TimeSpan.FromMinutes(1);
    // Selections that may fail in a row before the run stops.
    private const int MaxSelectFailures = 4;

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly IForeverLootRoutine loot;
    private readonly ForeverRunOptions runOptions;
    private readonly ClassConfiguration config;
    private readonly CancellationToken token;
    private readonly ForeverStateMachine<Phase> machine;
    private readonly ForeverExclusions exclusions = new();
    private volatile bool stopped;
    private volatile int completedKills;
    private long lastClearTarget;
    private long lastActionFrame;
    private long invalidFrameSince;
    private VisualNameplate? candidate;
    private int selectFailures;
    private double lookAroundTurned;
    private double? lookAroundFacing;
    private long roamSince;
    private VisualTargetIdentity? target;
    private bool observedDeadTarget;
    private long targetSelectedAt;
    private long targetUnreadableAt;
    private long targetRestoreAt;
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
        IForeverCombatRoutine combat, IForeverLootRoutine loot, ForeverRunOptions runOptions,
        ClassConfiguration config,
        CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverAutonomousGoal))
    {
        this.logger = logger;
        this.tracker = tracker;
        this.selector = selector;
        this.navigator = navigator;
        this.combat = combat;
        this.loot = loot;
        this.runOptions = runOptions;
        this.config = config;
        token = cts.Token;

        // The whole run: each phase, where it may go next, its hard bound and
        // what it releases. Bounds that depend on what the phase sees stay in
        // the phase itself.
        machine = new ForeverStateMachine<Phase>(logger, "Forever controller",
                Phase.Start, Phase.Failed, Phase.Complete)
            .On(Phase.Start, Start, exits: [Phase.Search])
            .On(Phase.Search, Search, exits: [Phase.Select, Phase.Fight, Phase.Complete],
                timeoutMs: 45000, onTimeout: "no safe target found within acquisition time",
                enter: ResetLookAround, exit: navigator.StopPursuit)
            .On(Phase.Select, Select, exits: [Phase.Approach, Phase.Search],
                timeoutMs: 8000, onTimeout: "selecting the chosen unit did not finish",
                enter: navigator.StopPursuit)
            .On(Phase.Approach, Approach, exits: [Phase.Fight, Phase.Search, Phase.Loot, Phase.Rest],
                timeoutMs: 20000, onTimeout: "target did not enter spell range within local pursuit bound",
                exit: navigator.StopPursuit)
            .On(Phase.Fight, Fight, exits: [Phase.Approach, Phase.Loot, Phase.Rest],
                timeoutMs: 75000, onTimeout: "fight did not end within the combat bound",
                // Both entries, from pursuit and from aggro, start with no
                // pending restore or unreadable-identity window from earlier.
                enter: () =>
                {
                    targetRestoreAt = 0;
                    targetUnreadableAt = 0;
                })
            .On(Phase.Loot, Loot, exits: [Phase.Rest])
            .On(Phase.Rest, Rest, exits: [Phase.Search, Phase.Complete])
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
            Phase phase = machine.State;
            if (phase is Phase.Approach or Phase.Fight && target is { } killed &&
                (bars.LastDeathEvent is { } death && death.Confirms(killed, deathSequenceBefore) ||
                 bars.TargetIdentity == killed && flags.Present && flags.Dead))
            {
                TargetDied(bars, killed);
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

        if (!LookedAround(bars))
        {
            if (navigator.TurnInPlace(LookAroundRate, out string turnReason) == ForeverNavigationResult.Failed)
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
        if (navigator.Roam(bars, out string roamReason) == ForeverNavigationResult.Failed)
            machine.Fail(roamReason);
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
        // Out of combat the unit is passed over for a while; in combat an
        // attacker stays a candidate. Either way the failures in a row are bounded.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever selection gave up on slot {Slot}: {Reason}", chosen.Slot, reason);
        candidate = null;
        if (++selectFailures >= MaxSelectFailures)
        {
            machine.Fail($"{selectFailures} selections in a row failed; last: {reason}");
            return;
        }
        if (!bars.TargetFlags!.Value.PlayerCombat)
            exclusions.Exclude(chosen, SetAside);
        machine.Go(Phase.Search);
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
        if (result == ForeverNavigationResult.Blocked && !flags.PlayerCombat)
        {
            machine.Fail($"selected target's local approach is blocked: {reason}");
            return;
        }
        if (result == ForeverNavigationResult.Failed)
        {
            if (!flags.PlayerCombat &&
                reason == "selected nameplate marker stayed unavailable")
            {
                // Passed over, or the search would choose the selected unit again.
                exclusions.Exclude(selected, SetAside);
                target = null;
                machine.Go(Phase.Search);
            }
            else
                machine.Fail(reason);
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
        switch (result)
        {
            case ForeverCombatResult.NeedRange:
                machine.Go(Phase.Approach);
                return;
            case ForeverCombatResult.TargetDead:
                TargetDied(bars, selected);
                return;
            case ForeverCombatResult.Failed:
                machine.Fail(reason);
                return;
        }
    }

    private void Loot(VisualBarSample bars)
    {
        switch (loot.Update(bars, out string reason))
        {
            case ForeverLootResult.Collected:
                completedLoots++;
                machine.Go(Phase.Rest);
                break;
            case ForeverLootResult.Empty:
                machine.Go(Phase.Rest);
                break;
            case ForeverLootResult.Failed:
                machine.Fail(reason);
                break;
        }
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
        if (!observedDeadTarget)
        {
            machine.Fail("target death was not verified");
            return;
        }
        completedKills++;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever autonomous kill {Count} verified", CompletedKills);
        target = null;
        observedDeadTarget = false;
        machine.Go(runOptions.MaxCompletedCycles is { } maximum && completedKills >= maximum
            ? Phase.Complete : Phase.Search);
    }

    private void TargetDied(VisualBarSample bars, VisualTargetIdentity killed)
    {
        navigator.StopPursuit();
        observedDeadTarget = true;
        observedDeaths++;
        if (config.Loot)
        {
            if (bars.Loot is not { } state)
            {
                machine.Fail("target death confirmed, but loot event telemetry is unavailable");
                return;
            }
            loot.Begin(killed, state);
            machine.Go(Phase.Loot);
        }
        else
            machine.Go(Phase.Rest);
    }

    // Keeps the same opponent through pursuit and combat.
    private void Lock(VisualBarSample bars, VisualTargetIdentity selected)
    {
        target = selected;
        targetSelectedAt = GetTimestamp();
        deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
        selectFailures = 0;
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
