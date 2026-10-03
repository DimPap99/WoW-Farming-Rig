using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>Forever's visual-only acquisition, local pursuit, combat and recovery loop.</summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Initial, Acquire, Pursue, Fight, Loot, Recover, Complete, Failed }

    // Give the flags a moment to follow a clear-target press before another:
    // the fallback is Escape, which opens the game menu with nothing selected.
    private const int ClearTargetRetryMs = 1000;

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly IForeverLootRoutine loot;
    private readonly ForeverRunOptions runOptions;
    private readonly HashSet<VisualTargetIdentity> rejectedTargets = [];
    private int emptyTargetPresses;
    private readonly ClassConfiguration config;
    private readonly CancellationToken token;
    private readonly ForeverStateMachine<Phase> machine;
    private volatile bool stopped;
    private volatile int completedKills;
    private long lastTargetPress;
    private long lastClearTarget;
    private long lastActionFrame;
    private long invalidFrameSince;
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
                Phase.Initial, Phase.Failed, Phase.Complete)
            .On(Phase.Initial, Initial, exits: [Phase.Acquire])
            .On(Phase.Acquire, Acquire, exits: [Phase.Pursue, Phase.Fight, Phase.Complete],
                timeoutMs: 30000, onTimeout: "no safe target found within acquisition time",
                enter: () =>
                {
                    rejectedTargets.Clear();
                    emptyTargetPresses = 0;
                })
            .On(Phase.Pursue, Pursue, exits: [Phase.Fight, Phase.Acquire, Phase.Loot, Phase.Recover],
                timeoutMs: 20000, onTimeout: "target did not enter spell range within local pursuit bound",
                exit: navigator.StopPursuit)
            .On(Phase.Fight, Fight, exits: [Phase.Pursue, Phase.Loot, Phase.Recover],
                timeoutMs: 75000, onTimeout: "fight did not end within the combat bound",
                // Both entries, from pursuit and from aggro, start with no
                // pending restore or unreadable-identity window from earlier.
                enter: () =>
                {
                    targetRestoreAt = 0;
                    targetUnreadableAt = 0;
                })
            .On(Phase.Loot, Loot, exits: [Phase.Recover])
            .On(Phase.Recover, Recover, exits: [Phase.Acquire, Phase.Complete])
            .On(Phase.Failed, enter: navigator.StopPursuit);
    }

    public bool TryReadOnlyPreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) || bars.TargetFlags is not { } flags ||
            flags.PlayerCombat || !ForeverFrame.CanOpenFight(bars, inCombat: false) ||
            !ForeverBindings.TargetNearestEnemy.IsBoundIn(bars) ||
            bars.LastDeathEvent is null ||
            config.Loot && (bars.Loot is null ||
                !ForeverBindings.InteractTarget.IsBoundIn(bars)))
        {
            reason = "current frame, resources, or target and loot bindings are incomplete";
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
            if (phase is Phase.Pursue or Phase.Fight && target is { } killed &&
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
            if ((phase is Phase.Acquire or Phase.Pursue or Phase.Fight or Phase.Recover ||
                    phase == Phase.Loot && flags.PlayerCombat) &&
                !(flags.Present && flags.Dead && !flags.PlayerCombat) &&
                !combat.RunAdhoc(bars, navigator.StopPursuit, out string adhocReason))
            {
                navigator.StopPursuit();
                if (!string.IsNullOrEmpty(adhocReason))
                    machine.Fail(adhocReason);
                return;
            }
            // Once acquired, keep the same opponent through pursuit and combat.
            // Aggro can begin before our first cast; that is not a reason to Tab.
            if (flags.PlayerCombat && phase == Phase.Acquire && target is null)
            {
                navigator.StopPursuit();
                if (ForeverTargetSelector.TryEligibleTarget(bars,
                        config.NPCMaxLevels_Above, out var nearby) &&
                    combat.InRange(bars, nearby))
                {
                    target = nearby;
                    deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
                    machine.Go(Phase.Fight);
                }
                else if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500 &&
                    selector.SelectNearbyOpponent(bars))
                {
                    lastTargetPress = GetTimestamp();
                    return;
                }
                else
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

    private void Initial(VisualBarSample bars)
    {
        if (!TryReadOnlyPreflight(out string reason))
        {
            if (machine.ElapsedMs > 5000)
                machine.Fail(reason);
            return;
        }
        machine.Go(Phase.Acquire);
    }

    private void Acquire(VisualBarSample bars)
    {
        // Roaming holds the movement keys; any frame that does not roam lets
        // go, as a person stops to look at a candidate.
        if (!AcquireOrRoam(bars, bars.TargetFlags!.Value))
            navigator.StopPursuit();
    }

    // True when it roamed this frame.
    private bool AcquireOrRoam(VisualBarSample bars, VisualTargetFlags flags)
    {
        if (flags.PlayerCombat)
        {
            machine.Fail("unexpected combat during target acquisition");
            return false;
        }
        if (!ForeverFrame.HasMana(bars, ForeverFrame.ReadyMana))
            return false;
        if (flags.Present)
        {
            // Target flags need a moment to follow a Tab press.
            if (GetElapsedTime(lastTargetPress).TotalMilliseconds < 650)
                return false;
            // A target too close for every opener is skipped while another
            // can be pulled; aggro still brings a close one into the fight.
            if (ForeverTargetSelector.TrySafeTarget(bars,
                    config.NPCMaxLevels_Above, out VisualTargetIdentity selected) &&
                !combat.TooCloseToOpen(bars))
            {
                target = selected;
                targetSelectedAt = GetTimestamp();
                deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
                machine.Go(Phase.Pursue);
            }
            else if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500)
            {
                if (runOptions.StopWhenNoTargets && bars.TargetIdentity is { } rejected &&
                    !rejectedTargets.Add(rejected))
                {
                    selector.ClearTarget(bars);
                    machine.Go(Phase.Complete);
                    return false;
                }
                if (selector.CycleCandidate(bars))
                    lastTargetPress = GetTimestamp();
            }
            return false;
        }
        // Nothing selected: press Tab now and then, and keep walking in between.
        if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500)
        {
            if (runOptions.StopWhenNoTargets && emptyTargetPresses >= 2)
            {
                machine.Go(Phase.Complete);
                return false;
            }
            if (selector.SelectNearest(bars))
            {
                emptyTargetPresses++;
                lastTargetPress = GetTimestamp();
            }
        }
        if (runOptions.StopWhenNoTargets || machine.ElapsedMs <= 6000)
            return false;
        if (navigator.Roam(bars, out string reason) == ForeverNavigationResult.Failed)
        {
            machine.Fail(reason);
            return false;
        }
        return true;
    }

    private void Pursue(VisualBarSample bars)
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
                machine.Go(Phase.Acquire);
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
                target = null;
                machine.Go(Phase.Acquire);
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
                machine.Go(Phase.Pursue);
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
                machine.Go(Phase.Recover);
                break;
            case ForeverLootResult.Empty:
                machine.Go(Phase.Recover);
                break;
            case ForeverLootResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    private void Recover(VisualBarSample bars)
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
            ? Phase.Complete : Phase.Acquire);
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
            machine.Go(Phase.Recover);
    }
}
