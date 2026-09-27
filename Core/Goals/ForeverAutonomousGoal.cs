using Core.Forever;
using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Linq;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>Forever's visual-only acquisition, local pursuit, combat and recovery loop.</summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Initial, Acquire, Pursue, Fight, Recover, Failed }

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly PlayerReader player;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly CastingHandler caster;
    private readonly SpellBookReader spells;
    private readonly ClassConfiguration config;
    private readonly CancellationToken token;
    private volatile Phase phase = Phase.Initial;
    private volatile int completedKills;
    private volatile string lastFailure = string.Empty;
    private long phaseStarted = GetTimestamp();
    private long lastTargetPress;
    private long lastActionFrame;
    private VisualTargetIdentity? target;
    private bool observedDeadTarget;

    public override float Cost => 1;
    public override bool CanRun() => phase != Phase.Failed;
    public string Status => phase.ToString();
    public string LastFailure => lastFailure;
    public int CompletedKills => completedKills;

    public ForeverAutonomousGoal(ILogger<ForeverAutonomousGoal> logger,
        PlayerReader player, ForeverVisualBarTracker tracker,
        ForeverTargetSelector selector, ForeverNavigator navigator,
        IForeverCombatRoutine combat, CastingHandler caster,
        SpellBookReader spells, ClassConfiguration config,
        CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverAutonomousGoal))
    {
        this.logger = logger;
        this.player = player;
        this.tracker = tracker;
        this.selector = selector;
        this.navigator = navigator;
        this.combat = combat;
        this.caster = caster;
        this.spells = spells;
        this.config = config;
        token = cts.Token;
    }

    public bool TryReadOnlyPreflight(out string reason)
    {
        if (!player.TryGetForeverVisualVitals(out VisualBarSample bars) ||
            !FrameSafe(bars) || bars.TargetFlags is not { } flags ||
            flags.PlayerCombat || bars.PlayerHealth < 0.70 ||
            bars.PlayerPower < 0.60 ||
            !ForeverTargetSelector.HasBinding(bars, 9,
                BindingID.TARGETNEARESTENEMY, ConsoleKey.Tab,
                ModifierKey.None) ||
            !ForeverTargetSelector.HasBinding(bars, 15,
                BindingID.STARTATTACK, ConsoleKey.D2,
                ModifierKey.Shift) ||
            !ForeverTargetSelector.HasBinding(bars, 18,
                BindingID.INTERACTTARGET, ConsoleKey.D3,
                ModifierKey.Shift))
        {
            reason = "current frame, resources, or target and attack bindings are incomplete";
            return false;
        }
        return navigator.TryReadOnlyPreflight(bars, out reason);
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is AbortEvent && CompletedKills == 0)
            Fail("Controller stopped");
    }

    public override void Update()
    {
        if (!CanRun() || token.IsCancellationRequested)
            return;
        try
        {
            if (!player.TryGetForeverVisualVitals(out VisualBarSample bars) ||
                !FrameSafe(bars))
            {
                if (Elapsed() > 5000)
                    Fail("visual controller frame missing or changed");
                return;
            }
            if (tracker.LatestFrameId == lastActionFrame)
                return;
            lastActionFrame = tracker.LatestFrameId;
            VisualTargetFlags flags = bars.TargetFlags!.Value;
            if (flags.PlayerDead)
            {
                Fail("player died; corpse recovery has not been verified");
                return;
            }
            if (phase != Phase.Recover && bars.PlayerHealth < 0.70)
            {
                Fail("health fell below combat safety threshold");
                return;
            }

            switch (phase)
            {
                case Phase.Initial:
                    if (!TryReadOnlyPreflight(out string initialReason))
                    {
                        Fail(initialReason);
                        return;
                    }
                    // Start by acquiring through the verified Tab binding,
                    // even when a human left a mob selected for preflight.
                    if (flags.Present)
                        selector.ClearTarget();
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
        if (flags.PlayerCombat)
        {
            Fail("unexpected combat during target acquisition");
            return;
        }
        if (bars.PlayerPower < 0.60)
            return;
        if (flags.Present)
        {
            if (ForeverTargetSelector.TrySafeTarget(bars,
                    out VisualTargetIdentity selected) &&
                bars.TargetHealth >= 0.99)
            {
                target = selected;
                Enter(Phase.Pursue);
            }
            else
                selector.ClearTarget();
            return;
        }
        if (GetElapsedTime(lastTargetPress).TotalMilliseconds >= 1500 &&
            selector.SelectNearest(bars))
        {
            lastTargetPress = GetTimestamp();
            return;
        }
        if (Elapsed() > 6000)
        {
            if (navigator.Roam(bars, out string reason) ==
                ForeverNavigationResult.Failed)
                Fail(reason);
        }
        if (Elapsed() > 30000)
            Fail("no safe nearby target found");
    }

    private void Pursue(VisualBarSample bars, VisualTargetFlags flags)
    {
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
        if (ForeverTargetSelector.InStrikeRange(bars) ||
            flags.PlayerCombat)
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
        if (result == ForeverNavigationResult.Failed)
            Fail(reason);
    }

    private void Fight(VisualBarSample bars, VisualTargetFlags flags)
    {
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present)
        {
            Fail("target identity was lost during combat");
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
                observedDeadTarget = true;
                Enter(Phase.Recover);
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
            if (flags.Dead && bars.TargetIdentity == target)
                selector.ClearTarget();
            else if (!flags.Dead)
                Fail("new live target appeared during recovery");
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
            KeyAction? heal = config.Combat.Sequence.SingleOrDefault(
                action => action.SpellId == 635);
            if (heal is null)
            {
                Fail("Holy Light recovery action missing");
                return;
            }
            if (ForeverCastGate.CanPress(bars, heal, spells) &&
                !caster.Cast(heal, () => token.IsCancellationRequested))
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
        Enter(Phase.Acquire);
    }

    private static bool FrameSafe(VisualBarSample bars) =>
        AddonReader.HasForeverControllerFrame(bars) &&
        bars.Position is { UIMapId: 1420 } &&
        bars.WorldPosition is { InstanceMapId: 0 } &&
        bars.PlayerIdentity is { ClassId: (int)UnitClass.Paladin } &&
        bars.PlayerPowerType == 0 &&
        bars.TargetFlags is { PlayerDead: false };

    private double Elapsed() => GetElapsedTime(phaseStarted).TotalMilliseconds;

    private void Enter(Phase next)
    {
        phase = next;
        phaseStarted = GetTimestamp();
    }

    private void Fail(string reason)
    {
        if (phase == Phase.Failed)
            return;
        lastFailure = reason;
        phase = Phase.Failed;
        logger.LogWarning("Forever controller stopped: {Reason}", reason);
    }
}
