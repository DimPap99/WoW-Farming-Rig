using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>Forever's visual-only acquisition, local pursuit, combat and recovery loop.</summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Initial, Acquire, Pursue, Fight, Loot, Recover, Complete, Failed }

    // /cleartarget is typed through chat; give the flags a moment to follow.
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
    private readonly ForeverCaster caster;
    private readonly ClassConfiguration config;
    private readonly CancellationToken token;
    private volatile Phase phase = Phase.Initial;
    private volatile bool stopped;
    private volatile int completedKills;
    private volatile string lastFailure = string.Empty;
    private long phaseStarted = GetTimestamp();
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
    public override bool CanRun() => phase != Phase.Failed && !stopped;
    public string Status => phase.ToString();
    public string LastFailure => lastFailure;
    public int CompletedKills => completedKills;
    public int ObservedDeaths => observedDeaths;
    public int CompletedLoots => completedLoots;

    public ForeverAutonomousGoal(ILogger<ForeverAutonomousGoal> logger,
        ForeverVisualBarTracker tracker,
        ForeverTargetSelector selector, ForeverNavigator navigator,
        IForeverCombatRoutine combat, IForeverLootRoutine loot, ForeverRunOptions runOptions,
        ForeverCaster caster, ClassConfiguration config,
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
        this.caster = caster;
        this.config = config;
        token = cts.Token;
    }

    public bool TryReadOnlyPreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) || bars.TargetFlags is not { } flags ||
            flags.PlayerCombat || bars.PlayerHealth < 0.70 ||
            bars.PlayerPower < 0.60 ||
            !ForeverBindings.TargetNearestEnemy.IsBoundIn(bars) ||
            !ForeverBindings.StartAttack.IsBoundIn(bars) ||
            !ForeverBindings.ActionButton(ForeverPaladin.SealSlot, ConsoleKey.D5).IsBoundIn(bars) ||
            bars.ActionBar?.GetSlot(ForeverPaladin.SealSlot) is not
                { Kind: VisualActionKind.Spell, Id: ForeverPaladin.SealOfRighteousness } ||
            bars.SealOfRighteousnessActive is null || bars.LastDeathEvent is null ||
            config.Loot && (bars.Loot is null ||
                !ForeverBindings.InteractTarget.IsBoundIn(bars)))
        {
            reason = "current frame, resources, or target and attack bindings are incomplete";
            return false;
        }
        return navigator.TryReadOnlyPreflight(bars, out reason);
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
            if (phase != Phase.Complete && CompletedKills == 0)
                Fail("Controller stopped");
        }
    }

    public override void Update()
    {
        if (!CanRun() || phase == Phase.Complete || token.IsCancellationRequested)
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
                    Fail("visual controller frame unavailable for five continuous seconds");
                return;
            }
            invalidFrameSince = 0;
            if (bars.FrameId == lastActionFrame)
                return;
            lastActionFrame = bars.FrameId;
            VisualTargetFlags flags = bars.TargetFlags!.Value;
            if (flags.PlayerDead)
            {
                Fail("player died; corpse recovery has not been verified");
                return;
            }
            if (phase is Phase.Pursue or Phase.Fight && target is { } killed &&
                (bars.LastDeathEvent is { } death && death.Confirms(killed, deathSequenceBefore) ||
                 bars.TargetIdentity == killed && flags.Present && flags.Dead))
            {
                TargetDied(bars, killed);
                return;
            }
            if (flags.PlayerCombat && bars.PlayerHealth < 0.70)
            {
                navigator.StopPursuit();
                if (HolyLight() is { } heal)
                {
                    ForeverCastResult healed = caster.TryCast(heal,
                        () => token.IsCancellationRequested);
                    if (healed == ForeverCastResult.Succeeded)
                        return;
                    // A failed heal latches the caster closed; say so here
                    // rather than at the next attack it refuses.
                    if (healed == ForeverCastResult.Failed)
                    {
                        Fail("in-combat heal success was not confirmed");
                        return;
                    }
                }
            }
            // Once acquired, keep the same opponent through pursuit and combat.
            // Aggro can begin before our first cast; that is not a reason to Tab.
            if (flags.PlayerCombat && phase == Phase.Acquire && target is null)
            {
                navigator.StopPursuit();
                if (ForeverTargetSelector.TryEligibleTarget(bars, out var nearby) &&
                    ForeverTargetSelector.InStrikeRange(bars))
                {
                    target = nearby;
                    deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
                    Enter(Phase.Fight);
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

            if (phase is Phase.Acquire or Phase.Pursue or Phase.Fight ||
                phase == Phase.Recover && bars.PlayerHealth >= 0.90)
            {
                if (!(flags.Present && flags.Dead) &&
                    !combat.EnsureSeal(bars, navigator.StopPursuit, out string sealReason))
                {
                    navigator.StopPursuit();
                    if (!string.IsNullOrEmpty(sealReason))
                        Fail(sealReason);
                    return;
                }
            }

            switch (phase)
            {
                case Phase.Initial:
                    if (!TryReadOnlyPreflight(out string initialReason))
                    {
                        if (Elapsed() > 5000)
                            Fail(initialReason);
                        return;
                    }
                    Enter(Phase.Acquire);
                    break;

                case Phase.Acquire:
                    Acquire(bars, flags);
                    break;

                case Phase.Pursue:
                    Pursue(bars, flags);
                    break;

                case Phase.Fight:
                    Fight(bars, flags);
                    break;

                case Phase.Recover:
                    Recover(bars, flags);
                    break;

                case Phase.Loot:
                    switch (loot.Update(bars, out string lootReason))
                    {
                        case ForeverLootResult.Collected:
                            completedLoots++;
                            Enter(Phase.Recover);
                            break;
                        case ForeverLootResult.Empty:
                            Enter(Phase.Recover);
                            break;
                        case ForeverLootResult.Failed:
                            Fail(lootReason);
                            break;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forever autonomous controller stopped");
            Fail("controller update threw an exception");
        }
    }

    private void Acquire(VisualBarSample bars, VisualTargetFlags flags)
    {
        if (Elapsed() > 30000)
        {
            Fail("no safe target found within acquisition time");
            return;
        }
        if (flags.PlayerCombat)
        {
            Fail("unexpected combat during target acquisition");
            return;
        }
        if (bars.PlayerPower < 0.60)
            return;
        if (GetElapsedTime(lastTargetPress).TotalMilliseconds < 650)
            return;
        if (flags.Present)
        {
            if (ForeverTargetSelector.TrySafeTarget(bars,
                    out VisualTargetIdentity selected))
            {
                target = selected;
                targetSelectedAt = GetTimestamp();
                deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
                Enter(Phase.Pursue);
            }
            else if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500)
            {
                if (runOptions.StopWhenNoTargets && bars.TargetIdentity is { } rejected &&
                    !rejectedTargets.Add(rejected))
                {
                    selector.ClearTarget();
                    Enter(Phase.Complete);
                    return;
                }
                if (selector.CycleCandidate(bars))
                    lastTargetPress = GetTimestamp();
            }
            return;
        }
        if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500)
        {
            if (runOptions.StopWhenNoTargets && emptyTargetPresses >= 2)
            {
                Enter(Phase.Complete);
                return;
            }
            if (selector.SelectNearest(bars))
            {
                emptyTargetPresses++;
                lastTargetPress = GetTimestamp();
                return;
            }
        }
        if (!runOptions.StopWhenNoTargets && Elapsed() > 6000)
        {
            if (navigator.Roam(bars, out string reason) ==
                ForeverNavigationResult.Failed)
                Fail(reason);
        }
    }

    private void Pursue(VisualBarSample bars, VisualTargetFlags flags)
    {
        // Range pixels refresh on their own 500ms cadence. Let them catch up
        // to a newly selected identity before interpreting the range bits.
        if (GetElapsedTime(targetSelectedAt).TotalMilliseconds < 650)
            return;
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present ||
            !flags.Attackable || flags.Dead)
        {
            if (flags.PlayerCombat)
                Fail("selected target changed during combat pursuit");
            else
            {
                target = null;
                Enter(Phase.Acquire);
            }
            return;
        }
        if (combat.InRange(bars, selected))
        {
            Enter(Phase.Fight);
            return;
        }
        if (Elapsed() > 20000)
        {
            Fail("target did not enter spell range within local pursuit bound");
            return;
        }
        ForeverNavigationResult result = navigator.Approach(bars,
            selected, out string reason);
        if (result == ForeverNavigationResult.NoTargetPosition)
            result = navigator.SearchForTarget(bars, selected, out reason);
        if (result == ForeverNavigationResult.Blocked && !flags.PlayerCombat)
        {
            Fail($"selected target's local approach is blocked: {reason}");
            return;
        }
        if (result == ForeverNavigationResult.Failed)
        {
            if (!flags.PlayerCombat &&
                reason == "selected nameplate marker stayed unavailable")
            {
                target = null;
                Enter(Phase.Acquire);
            }
            else
                Fail(reason);
        }
    }

    private void Fight(VisualBarSample bars, VisualTargetFlags flags)
    {
        if (!flags.Present)
        {
            navigator.StopPursuit();
            if (targetRestoreAt == 0)
            {
                targetRestoreAt = GetTimestamp();
                selector.RestoreLastTarget();
            }
            else if (GetElapsedTime(targetRestoreAt).TotalMilliseconds > 3000)
                Fail("selected target disappeared and could not be restored for the death check");
            return;
        }
        // The target is back; the next disappearance gets a fresh window.
        targetRestoreAt = 0;
        if (bars.TargetIdentity is null)
        {
            targetUnreadableAt = targetUnreadableAt == 0 ? GetTimestamp() : targetUnreadableAt;
            if (GetElapsedTime(targetUnreadableAt).TotalMilliseconds > 2000)
                Fail($"combat target identity remained unreadable; flags={flags}, health={bars.TargetHealth:P0}");
            return;
        }
        targetUnreadableAt = 0;
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present)
        {
            Fail($"target identity was lost during combat; expected={target}, actual={bars.TargetIdentity}, flags={flags}, health={bars.TargetHealth:P0}");
            return;
        }
        ForeverCombatResult result = combat.Update(bars, selected,
            out string reason);
        switch (result)
        {
            case ForeverCombatResult.NeedRange:
                Enter(Phase.Pursue);
                return;
            case ForeverCombatResult.TargetDead:
                TargetDied(bars, selected);
                return;
            case ForeverCombatResult.Failed:
                Fail(reason);
                return;
        }
        if (Elapsed() > 75000)
            Fail("fight did not end within the combat bound");
    }

    private void Recover(VisualBarSample bars, VisualTargetFlags flags)
    {
        if (flags.Present)
        {
            if (!flags.Dead)
            {
                Fail("new live target appeared during recovery");
                return;
            }
            // Any corpse is safe to deselect, including one whose identity did
            // not read back; repeat the command only after the flags had time.
            if (GetElapsedTime(lastClearTarget).TotalMilliseconds >= ClearTargetRetryMs)
            {
                selector.ClearTarget();
                lastClearTarget = GetTimestamp();
            }
            if (Elapsed() > 30000)
                Fail("dead target stayed selected during recovery");
            return;
        }
        if (flags.PlayerCombat)
        {
            if (Elapsed() > 30000)
                Fail("combat did not clear after target death");
            return;
        }
        if (bars.PlayerHealth < 0.90)
        {
            if (HolyLight() is not { } heal)
            {
                Fail("Holy Light recovery action missing");
                return;
            }
            if (caster.TryCast(heal, () => token.IsCancellationRequested) ==
                ForeverCastResult.Failed)
                Fail("recovery heal success was not confirmed");
            if (Elapsed() > 45000)
                Fail("health did not recover in time");
            return;
        }
        if (bars.PlayerPower < 0.60)
        {
            if (Elapsed() > 45000)
                Fail("mana did not recover in time");
            return;
        }
        if (!observedDeadTarget)
        {
            Fail("target death was not verified");
            return;
        }
        completedKills++;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever autonomous kill {Count} verified", CompletedKills);
        target = null;
        observedDeadTarget = false;
        Enter(runOptions.MaxCompletedCycles is { } maximum && completedKills >= maximum
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
                Fail("target death confirmed, but loot event telemetry is unavailable");
                return;
            }
            loot.Begin(killed, state);
            Enter(Phase.Loot);
        }
        else
            Enter(Phase.Recover);
    }

    private KeyAction? HolyLight() => config.Combat.Sequence.SingleOrDefault(
        action => action.SpellId == ForeverPaladin.HolyLight);

    private double Elapsed() => GetElapsedTime(phaseStarted).TotalMilliseconds;

    private void Enter(Phase next)
    {
        if (phase == Phase.Pursue && next != Phase.Pursue)
            navigator.StopPursuit();
        if (next == Phase.Fight)
        {
            // Both Fight entry paths (pursuit and in-combat aggro) start with
            // no pending restore or unreadable-identity window from earlier.
            targetRestoreAt = 0;
            targetUnreadableAt = 0;
        }
        if (next == Phase.Acquire)
        {
            rejectedTargets.Clear();
            emptyTargetPresses = 0;
        }
        phase = next;
        phaseStarted = GetTimestamp();
    }

    private void Fail(string reason)
    {
        if (phase == Phase.Failed)
            return;
        navigator.StopPursuit();
        lastFailure = reason;
        phase = Phase.Failed;
        logger.LogWarning("Forever controller stopped: {Reason}", reason);
    }
}
