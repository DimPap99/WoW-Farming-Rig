using Microsoft.Extensions.Logging;

using System;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverEngageState { Idle, Select, Approach, Fight, Failed }

/// <summary>
/// The fight with one unit, from the click on its nameplate to its death:
/// Select, Approach and Fight. Threat and the agenda's task hand it a plate
/// to click (<see cref="Offer"/>) or a selected unit that already fights the
/// player (<see cref="Adopt"/>), and it wants to run while it holds either.
/// A setback with the unit sets the unit aside and ends the engagement, so
/// the selector moves on; a verified kill ends it too.
/// </summary>
public sealed class ForeverEngageActivity : IForeverActivity
{
    /// <summary>Upstream's CombatGoal cost: nothing comes before the fight in hand.</summary>
    public const float Priority = 4f;

    private const int SelectMs = 8000;
    private const int ApproachMs = 20000;
    private const int FightMs = 75000;
    // Range pixels refresh on their own 500ms cadence; a newly selected
    // identity gets this long before its range bits are read.
    private const int RangeSettleMs = 650;
    // A target identity that stays unreadable this long in the fight stops the run.
    private const int UnreadableIdentityMs = 2000;
    // Reach flickers at its edges: the action rows refresh every 0.5 s and
    // the target distance every 0.1 s, and a mob closing in crosses a ranged
    // weapon's minimum range between the two. The fight leaves for pursuit
    // only once reach stayed lost this long.
    private const int ReachLostMs = 1000;
    // A target that vanished gets this long for its death latch to arrive
    // before the engagement ends.
    private const int TargetLostMs = 1500;

    private readonly ILogger<ForeverEngageActivity> logger;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly ForeverStateMachine<ForeverEngageState> machine;
    private readonly ForeverRecovery recovery = new();

    private ForeverFacts facts;
    private VisualNameplate? candidate;
    private VisualTargetIdentity? target;
    private long targetSelectedAt;
    private long targetUnreadableAt;
    private long targetLostAt;
    private long reachLostAt;
    private int deathSequenceBefore;

    public ForeverEngageActivity(ILogger<ForeverEngageActivity> logger, ForeverTargetSelector selector,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config)
    {
        this.logger = logger;
        this.selector = selector;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;

        // Select's and Approach's bounds hand over to a setback instead of
        // failing; Fight's stops the run.
        machine = new ForeverStateMachine<ForeverEngageState>(logger, "Forever engage",
                ForeverEngageState.Idle, ForeverEngageState.Failed)
            .On(ForeverEngageState.Idle, exits: [ForeverEngageState.Select, ForeverEngageState.Fight])
            .On(ForeverEngageState.Select, Select, exits: [ForeverEngageState.Approach, ForeverEngageState.Idle],
                timeoutMs: SelectMs, onTimeout: "selecting the chosen unit did not finish",
                timedOut: reason => SetBack(ForeverSetback.SelectFailed, reason),
                enter: navigator.StopPursuit)
            .On(ForeverEngageState.Approach, Approach, exits: [ForeverEngageState.Fight, ForeverEngageState.Idle],
                timeoutMs: ApproachMs, onTimeout: "target did not enter spell range within local pursuit bound",
                timedOut: reason => SetBack(ForeverSetback.OutOfReach, reason),
                exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop)
            .On(ForeverEngageState.Fight, Fight, exits: [ForeverEngageState.Approach, ForeverEngageState.Idle],
                timeoutMs: FightMs, onTimeout: "fight did not end within the combat bound",
                // Both entries, from pursuit and from aggro, start with no
                // lost-target, unreadable-identity or lost-reach window from earlier.
                enter: () =>
                {
                    targetLostAt = 0;
                    targetUnreadableAt = 0;
                    reachLostAt = 0;
                })
            .On(ForeverEngageState.Failed, enter: navigator.StopPursuit);
    }

    public string Name => "Engage";
    public float Cost => Priority;
    public ForeverAllowed Allowed => machine.Allowed;
    public string Status => $"{Name}/{machine.State}";
    public ForeverEngageState State => machine.State;
    public string LastFailure => machine.LastFailure;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverEngageState state) => machine.AllowedIn(state);

    /// <summary>A unit is chosen to click or locked to fight.</summary>
    public bool Engaged => candidate is not null || target is not null;

    /// <summary>Units set aside after a setback; Threat and the task pass them over.</summary>
    public ForeverExclusions Exclusions { get; } = new();

    /// <summary>When the last kill was verified, or 0.</summary>
    public long LastKillAt { get; private set; }

    /// <summary>A kill was verified on this frame; the frame is passed along.</summary>
    public event Action<VisualBarSample>? Killed;

    /// <summary>A plate to click and fight. The click begins now; the fight once the selector runs this.</summary>
    public void Offer(VisualNameplate plate)
    {
        navigator.StopPursuit();
        candidate = plate;
        selector.BeginSelect(plate);
    }

    /// <summary>The selected unit already fights the player and is in reach: fought as it is, without a click.</summary>
    public void Adopt(VisualBarSample bars, VisualTargetIdentity identity)
    {
        navigator.StopPursuit();
        Lock(bars, identity);
    }

    public bool Wants(in ForeverFacts facts) => Engaged;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        this.facts = facts;
        if (machine.State == ForeverEngageState.Idle)
            machine.Go(candidate is not null ? ForeverEngageState.Select : ForeverEngageState.Fight);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts)
    {
        this.facts = facts;
        machine.Tick(bars);
        return machine.State == ForeverEngageState.Failed
            ? ForeverActivityResult.Failed(machine.LastFailure)
            : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        navigator.StopPursuit();
        if (machine.State is not (ForeverEngageState.Idle or ForeverEngageState.Failed))
            machine.Go(ForeverEngageState.Idle);
    }

    /// <summary>
    /// The locked unit died: its death latch confirms it, or the target shows
    /// it dead. The host asks before anything else acts on a frame, while the
    /// unit is pursued or fought.
    /// </summary>
    public bool TryVerifyKill(VisualBarSample bars)
    {
        if (machine.State is not (ForeverEngageState.Approach or ForeverEngageState.Fight) ||
            target is not { } killed ||
            !(bars.LastDeathEvent is { } death && death.Confirms(killed, deathSequenceBefore) ||
              bars.TargetIdentity == killed && bars.TargetFlags is { Present: true, Dead: true }))
            return false;
        TargetDied(bars);
        return true;
    }

    private void Select(VisualBarSample bars)
    {
        // Offer always names the candidate before the selector runs this.
        if (candidate is not { } chosen)
        {
            machine.Fail("selecting without a chosen unit");
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
                    machine.Go(ForeverEngageState.Approach);
                    return;
                }
                reason = "the unit selected is not the eligible candidate";
                break;
        }
        SetBack(ForeverSetback.SelectFailed, reason);
    }

    private void Approach(VisualBarSample bars)
    {
        if (GetElapsedTime(targetSelectedAt).TotalMilliseconds < RangeSettleMs)
            return;
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (target is not { } selected ||
            bars.TargetIdentity != selected || !flags.Present ||
            !flags.Attackable || flags.Dead)
        {
            if (flags.PlayerCombat)
                machine.Fail("selected target changed during combat pursuit");
            else
                Release();
            return;
        }
        // Hit by another unit on the way: take the attacker, as a person
        // turns on what hits them instead of pulling a second mob.
        if (ForeverTargetPolicy.AttackedByAnother(bars, config))
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever attacked while approaching another unit; taking the attacker");
            Release();
            return;
        }
        if (combat.InRange(bars, selected))
        {
            machine.Go(ForeverEngageState.Fight);
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
        // The target vanished: the client cleared it or the unit left. Its
        // death latch may still be on its way (the host checks for a kill);
        // past that a person looks for whatever fights them, and Threat takes
        // the attackers by their plates, this unit too if it still fights.
        if (!flags.Present)
        {
            navigator.StopPursuit();
            if (targetLostAt == 0)
                targetLostAt = GetTimestamp();
            else if (GetElapsedTime(targetLostAt).TotalMilliseconds > TargetLostMs)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever target vanished without a death; searching again");
                Release();
            }
            return;
        }
        // The target is back; the next disappearance gets a fresh window.
        targetLostAt = 0;
        if (bars.TargetIdentity is null)
        {
            targetUnreadableAt = targetUnreadableAt == 0 ? GetTimestamp() : targetUnreadableAt;
            if (GetElapsedTime(targetUnreadableAt).TotalMilliseconds > UnreadableIdentityMs)
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
                    machine.Go(ForeverEngageState.Approach);
                return;
            // The game refused the target's reach: a refusal is no flickering
            // range edge, so close in at once.
            case ForeverCombatResult.StepIn:
                machine.Go(ForeverEngageState.Approach);
                return;
            case ForeverCombatResult.Unreachable:
                SetBack(ForeverSetback.OutOfReach, reason);
                return;
            case ForeverCombatResult.TargetDead:
                TargetDied(bars);
                return;
            case ForeverCombatResult.Failed:
                machine.Fail(reason);
                return;
        }
    }

    // A verified kill ends the engagement; the host counts it, and the
    // selector decides what comes next: the next attacker, the bodies, a rest.
    private void TargetDied(VisualBarSample bars)
    {
        navigator.StopPursuit();
        LastKillAt = GetTimestamp();
        recovery.Killed();
        target = null;
        machine.Go(ForeverEngageState.Idle);
        Killed?.Invoke(bars);
    }

    // Keeps the same opponent through pursuit and combat.
    private void Lock(VisualBarSample bars, VisualTargetIdentity selected)
    {
        target = selected;
        targetSelectedAt = GetTimestamp();
        deathSequenceBefore = bars.LastDeathEvent?.Sequence ?? 0;
    }

    // The unit is no longer this engagement's: it changed, vanished or
    // something else attacks. Nothing is set aside.
    private void Release()
    {
        target = null;
        machine.Go(ForeverEngageState.Idle);
    }

    // A setback with the chosen or selected unit: set it aside and end the
    // engagement, or stop when recovery refuses (in combat, or past its budget).
    private void SetBack(ForeverSetback kind, string reason)
    {
        if (!recovery.TryRecover(kind, facts.InCombat, out string refusal))
        {
            machine.Fail($"{reason} ({refusal})");
            return;
        }
        TimeSpan aside = ForeverRecovery.SetAsideFor(kind, facts.InCombat);
        if (aside > TimeSpan.Zero)
        {
            if (candidate is { } plate)
                Exclusions.Exclude(plate, aside);
            if (target is { } unit)
                Exclusions.Exclude(unit, aside);
        }
        candidate = null;
        target = null;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever recovering from {Kind}: {Reason}; {Count} setbacks since the last kill",
                kind, reason, recovery.SinceKill);
        machine.Go(ForeverEngageState.Idle);
    }
}
