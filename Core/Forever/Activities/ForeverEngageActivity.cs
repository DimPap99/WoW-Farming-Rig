using Microsoft.Extensions.Logging;

using System;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverEngageState { Idle, Select, Approach, Fight, Sight, Failed }

/// <summary>
/// The fight with one unit, from the click on its nameplate to its death:
/// Select, Approach and Fight, and Sight when the unit goes out of the
/// player's line of sight. Threat and the agenda's task hand it a plate
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
    // A selection the candidate's own plate confirms gets this long for the
    // target's rows to agree before it counts as a setback.
    private const int SelectSettleMs = 600;
    private const int ApproachMs = 20000;
    private const int FightMs = 75000;
    // Range pixels refresh on their own 500ms cadence; a newly selected
    // identity gets this long before its range bits are read.
    private const int RangeSettleMs = 650;
    // A target identity that stays unreadable this long in the fight fails the engagement.
    private const int UnreadableIdentityMs = 2000;
    // Reach flickers at its edges: the action rows refresh every 0.5 s and
    // the target distance every 0.1 s, and a mob closing in crosses a ranged
    // weapon's minimum range between the two. The fight leaves for pursuit
    // only once reach stayed lost this long.
    private const int ReachLostMs = 1000;
    // A target that vanished gets this long for its death latch to arrive
    // before the engagement ends.
    private const int TargetLostMs = 1500;
    // Out of sight in the fight, the walk to see the target again gets this long.
    private const int SightMs = 20000;
    // The game reports no position for the unit; the walk heads for this share
    // of the way from the nearest to the farthest its nameplate places it.
    private const float SightAlong = 0.5f;
    // Reaching the estimate with the target out of view, this close counts.
    private const float SightReachedYards = 8;
    // A refused attack is tried again once the player moved this far from
    // where it was refused, or after this long, as a person tries again once
    // the view may have changed; never again from the same spot at once.
    private const float SightRetryYards = 3;
    private const int SightRetryMs = 4000;
    // Where the mesh comes no closer, the player steps aside this long, to
    // one side and the next time to the other, to look past the cover.
    private const int SightSidestepMs = 900;
    // A walk that cannot go on says why at most this often.
    private const int SightLogMs = 3000;

    private readonly ILogger<ForeverEngageActivity> logger;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly ForeverCorpses sightings;
    private readonly ForeverStateMachine<ForeverEngageState> machine;
    private readonly ForeverRecovery recovery = new();

    private ForeverFacts facts;
    private VisualNameplate? candidate;
    private VisualTargetIdentity? target;
    private long targetSelectedAt;
    private long selectDoneAt;
    private long targetUnreadableAt;
    private long targetLostAt;
    private long reachLostAt;
    // Out of sight: where and when an attack was last refused, a step aside
    // under way, and when a stalled walk last said why.
    private Vector2? sightRefusedFrom;
    private long sightRefusedAt;
    private long sidestepUntil;
    private int sidestepSide = 1;
    private long sightLoggedAt;
    private bool sightTrackedLogged;
    private int deathSequenceBefore;
    // Move-backward is held for the fight's step back to range.
    private bool steppingBack;

    public ForeverEngageActivity(ILogger<ForeverEngageActivity> logger, ForeverTargetSelector selector,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config,
        ForeverCorpses sightings)
    {
        this.logger = logger;
        this.selector = selector;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        this.sightings = sightings;

        // Select's and Approach's bounds hand over to a setback instead of
        // failing; Fight's fails the engagement, which the run falls back from.
        machine = new ForeverStateMachine<ForeverEngageState>(logger, "Forever engage",
                ForeverEngageState.Idle, ForeverEngageState.Failed)
            .On(ForeverEngageState.Idle, exits: [ForeverEngageState.Select, ForeverEngageState.Fight])
            .On(ForeverEngageState.Select, Select, exits: [ForeverEngageState.Approach, ForeverEngageState.Idle],
                timeoutMs: SelectMs, onTimeout: "selecting the chosen unit did not finish",
                timedOut: reason => SetBack(ForeverSetback.SelectFailed, reason),
                enter: () =>
                {
                    navigator.StopPursuit();
                    selectDoneAt = 0;
                })
            .On(ForeverEngageState.Approach, Approach, exits: [ForeverEngageState.Fight, ForeverEngageState.Idle],
                timeoutMs: ApproachMs, onTimeout: "target did not enter spell range within local pursuit bound",
                timedOut: reason => SetBack(ForeverSetback.OutOfReach, reason),
                exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop)
            .On(ForeverEngageState.Fight, Fight,
                exits: [ForeverEngageState.Approach, ForeverEngageState.Sight, ForeverEngageState.Idle],
                timeoutMs: FightMs, onTimeout: "fight did not end within the combat bound",
                // Both entries, from pursuit and from aggro, start with no
                // lost-target, unreadable-identity or lost-reach window from earlier.
                enter: () =>
                {
                    targetLostAt = 0;
                    targetUnreadableAt = 0;
                    reachLostAt = 0;
                })
            // Out of sight: walk the mesh toward the unit, attacking on the
            // move, until an attack lands. The bound hands over to a setback.
            .On(ForeverEngageState.Sight, Sight, exits: [ForeverEngageState.Fight, ForeverEngageState.Idle],
                timeoutMs: SightMs, onTimeout: "the target stayed out of sight while walking toward it",
                timedOut: reason => SetBack(ForeverSetback.OutOfReach, reason),
                // Entered on a refused attack, from where the player stands.
                enter: () =>
                {
                    sightRefusedFrom = null;
                    sightRefusedAt = GetTimestamp();
                    sidestepUntil = 0;
                    sightLoggedAt = 0;
                    sightTrackedLogged = false;
                },
                exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop)
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
    /// The fight failed: out of combat its unit is set aside for a while, as
    /// after a setback, so the next pull takes another; in combat it stays a
    /// threat and Threat takes it again. The engagement starts afresh.
    /// </summary>
    public string FallBack(string reason)
    {
        navigator.StopPursuit();
        steppingBack = false;
        TimeSpan aside = ForeverRecovery.SetAsideFor(ForeverSetback.SelectFailed, facts.InCombat);
        if (aside > TimeSpan.Zero)
        {
            if (candidate is { } plate)
                Exclusions.Exclude(plate, aside);
            if (target is { } unit)
                Exclusions.Exclude(unit, aside);
        }
        bool hadUnit = candidate is not null || target is not null;
        candidate = null;
        target = null;
        machine.Reset();
        return !hadUnit ? "the fight starts afresh"
            : aside > TimeSpan.Zero ? "its unit is set aside for a while" : "the attackers are looked for afresh";
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
                if (ForeverTargetPolicy.TryEligibleTarget(bars, config, out VisualTargetIdentity selected, out string why) &&
                    (!chosen.HasIdentity || chosen.Is(selected)))
                {
                    candidate = null;
                    Lock(bars, selected);
                    machine.Go(ForeverEngageState.Approach);
                    return;
                }
                if (selectDoneAt == 0)
                    selectDoneAt = GetTimestamp();
                if (GetElapsedTime(selectDoneAt).TotalMilliseconds < SelectSettleMs)
                    return;
                reason = why.Length > 0
                    ? $"the unit selected cannot be taken: {why}"
                    : $"the click selected another unit (target {selected.GuidHash1}/{selected.GuidHash2})";
                break;
        }
        SetBack(ForeverSetback.SelectFailed, $"{reason}; the candidate: {Describe(chosen)}");
    }

    // A candidate as the log names it: identity, level, reaction and where its
    // marker was when it was chosen.
    private static string Describe(VisualNameplate plate) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"plate {plate.GuidHash1}/{plate.GuidHash2}, level {plate.Level}, reaction {plate.Reaction}, " +
            $"marker at {plate.Screen?.X:0.00}, {plate.Screen?.Y:0.00}");

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
        // Close but out of view, as a mob behind the player: a person turns
        // round to it, never walks off searching with it at their back.
        if (bars.Attack?.TargetClose == true && bars.TargetScreenPosition is null)
        {
            navigator.FaceTarget(bars);
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
        // A step back ends as soon as the fight asks for anything else.
        if (steppingBack && result != ForeverCombatResult.StepBack)
        {
            navigator.StopPursuit();
            steppingBack = false;
        }
        switch (result)
        {
            // Ranged while the mob is not on the player, and too close for the
            // ranged attacks: a few steps back, facing it, to the bow's range.
            // Ground that will not take the steps leaves the fight where it is.
            case ForeverCombatResult.StepBack:
                switch (navigator.StepBack(bars, out reason))
                {
                    case ForeverNavigationResult.Failed:
                        machine.Fail(reason);
                        return;
                    case ForeverNavigationResult.StepSent:
                        steppingBack = true;
                        return;
                }
                return;
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
            case ForeverCombatResult.LostSight:
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever lost sight of the target ({Reason}); walking to see it again", reason);
                machine.Go(ForeverEngageState.Sight);
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
    // Out of the player's sight. The game reports no position for the unit,
    // so its nameplate places it: the bearing it was last seen at and how far
    // it can be. The walk goes along the mesh toward that, as close as the
    // mesh comes, which leads round or up to whatever blocks the view; where
    // it comes no closer the player steps aside. The next instant attack is
    // pressed on the move once the player has moved since the last refusal;
    // one that lands proves the target is in sight again, and the fight goes
    // on from there. Death, a lost or changed target, and contact go back to
    // the fight, which handles each.
    private void Sight(VisualBarSample bars)
    {
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (target is not { } selected || bars.TargetIdentity != selected || !flags.Present ||
            flags.Dead || bars.TargetHealth <= 0 || bars.Attack?.TargetClose == true ||
            bars.WorldPosition is not { } world)
        {
            machine.Go(ForeverEngageState.Fight);
            return;
        }
        // Hit by another unit meanwhile: take the attacker, as on the way in.
        if (ForeverTargetPolicy.AttackedByAnother(bars, config))
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever attacked while out of sight of the target; taking the attacker");
            Release();
            return;
        }
        Vector2 here = new((float)world.X, (float)world.Y);
        string reason;
        // A step aside runs its time, then the walk goes on, to the other side next time.
        if (sidestepUntil != 0)
        {
            if (GetTimestamp() < sidestepUntil)
            {
                if (navigator.Sidestep(bars, sidestepSide, out reason) == ForeverNavigationResult.Failed)
                    machine.Fail(reason);
                return;
            }
            navigator.StopPursuit();
            sidestepUntil = 0;
            sidestepSide = -sidestepSide;
        }
        if (!sightings.TryLocate(selected.GuidHash1, selected.GuidHash2, out Vector2 near, out Vector2 far) ||
            (bars.TargetScreenPosition is null &&
             Vector2.Distance(here, Vector2.Lerp(near, far, SightAlong)) <= SightReachedYards))
        {
            // Not seen lately, or reached where it was seen and still out of
            // view: turn to find it, which places it again.
            navigator.FaceTarget(bars);
            return;
        }
        Vector2 toward = Vector2.Lerp(near, far, SightAlong);
        if (near == far && !sightTrackedLogged && logger.IsEnabled(LogLevel.Information))
        {
            sightTrackedLogged = true;
            logger.LogInformation("Forever out of sight, walking to where the minimap's tracking places the target, {Yards:0} yards away",
                Vector2.Distance(here, toward));
        }
        // The attack that sent the fight here was refused where the player stands.
        sightRefusedFrom ??= here;
        if (Vector2.Distance(here, sightRefusedFrom.Value) >= SightRetryYards ||
            GetElapsedTime(sightRefusedAt).TotalMilliseconds >= SightRetryMs)
        {
            switch (combat.ShootOnTheMove(bars, selected,
                frame => navigator.WalkToward(frame, toward, out _, ForeverNavigator.AsCloseAsReachable),
                out reason))
            {
                case ForeverCombatResult.ActionSent:
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Forever sees the target again; the fight goes on");
                    machine.Go(ForeverEngageState.Fight);
                    return;
                case ForeverCombatResult.Failed:
                    machine.Fail(reason);
                    return;
                // Still out of sight: noted where, and the walk went on
                // through the wait for the game's answer.
                case ForeverCombatResult.LostSight:
                    sightRefusedFrom = here;
                    sightRefusedAt = GetTimestamp();
                    return;
            }
        }
        switch (navigator.WalkToward(bars, toward, out reason, ForeverNavigator.AsCloseAsReachable))
        {
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                return;
            // The mesh comes no closer to where the target stands, or the
            // walk stalled: step aside, as a person leans out past cover.
            case ForeverNavigationResult.Blocked or ForeverNavigationResult.Stuck:
                if (GetElapsedTime(sightLoggedAt).TotalMilliseconds >= SightLogMs &&
                    logger.IsEnabled(LogLevel.Information))
                {
                    sightLoggedAt = GetTimestamp();
                    logger.LogInformation("Forever out of sight, the walk toward the target cannot go on ({Reason}); stepping aside",
                        reason);
                }
                sidestepUntil = GetTimestamp() + (long)(SightSidestepMs * (Frequency / 1000.0));
                return;
        }
    }

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
    // engagement, or fail it when recovery refuses (in combat, or past its budget).
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
