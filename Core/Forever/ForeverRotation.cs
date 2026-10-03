using Core.Database;
using Core.Goals;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverCombatResult { Waiting, ActionSent, NeedRange, TargetDead, Failed }

/// <summary>
/// Each main-bar slot's range to one unit: the current target's action states,
/// or the ranges a nameplate roster slot carries for its unit.
/// </summary>
public readonly struct ForeverSlotRanges
{
    private readonly VisualActionStateSnapshot? targetStates;
    private readonly VisualSlotRanges plateRanges;
    private readonly bool isPlate;

    private ForeverSlotRanges(VisualActionStateSnapshot? targetStates, VisualSlotRanges plateRanges,
        bool isPlate, bool? close)
    {
        this.targetStates = targetStates;
        this.plateRanges = plateRanges;
        this.isPlate = isPlate;
        Close = close;
    }

    public static ForeverSlotRanges Target(VisualBarSample bars) =>
        new(bars.ActionStates, default, false, bars.Attack?.TargetClose);

    public static ForeverSlotRanges Of(VisualNameplate plate) => new(null, plate.Ranges, true, plate.Close);

    /// <summary>
    /// The unit is within the client's duel distance, which on this client
    /// begins at a ranged weapon's 8-yard minimum range; null when unknown.
    /// </summary>
    public bool? Close { get; }

    /// <summary>In or out of range; null when unknown.</summary>
    public bool? InRange(int slot) => isPlate
        ? plateRanges.InRange(slot)
        : targetStates?.GetSlot(slot)?.InRange;
}

/// <summary>How the profile's spells reach a unit.</summary>
/// <param name="Opener">An opener's slot reports it in range.</param>
/// <param name="Combat">A Combat spell's slot reports it in range.</param>
/// <param name="TooCloseToOpen">
/// Inside combat range but outside every opener's range, as a hunter's ranged
/// openers are inside their minimum range. A Paladin never is, since
/// Judgement's range covers melee.
/// </param>
public readonly record struct ForeverReach(bool Opener, bool Combat, bool TooCloseToOpen);

/// <summary>
/// Turns the character and camera toward the current target, as a person
/// keeps the opponent in front while fighting it.
/// </summary>
public interface IForeverFacing
{
    /// <summary>Turns toward the target on this frame; true once it is in front.</summary>
    bool FaceTarget(VisualBarSample bars);
}

public interface IForeverCombatRoutine
{
    /// <summary>Every profile action is on its slot and bound, so the routine can run.</summary>
    bool TryPreflight(VisualBarSample bars, out string reason);
    /// <summary>Whether the fight can act on <paramref name="identity"/> from here.</summary>
    bool InRange(VisualBarSample bars, VisualTargetIdentity identity);
    /// <summary>How the profile's spells reach the unit whose slot ranges are <paramref name="ranges"/>.</summary>
    ForeverReach Reach(VisualBarSample bars, ForeverSlotRanges ranges);
    /// <summary>
    /// Runs the profile's Adhoc actions: self heals and buffs. False while the
    /// caller has to wait, because an action was cast or a buff is still missing.
    /// </summary>
    /// <param name="beforeCast">Runs right before a blocking cast, so the
    /// caller can release held movement before the wait for the success event.</param>
    bool RunAdhoc(VisualBarSample bars, Action beforeCast, out string reason);
    ForeverCombatResult Update(VisualBarSample bars,
        VisualTargetIdentity identity, out string reason);
}

public enum ForeverFightStep { Wait, NeedRange, Pull, StartAttack, Combat }

/// <summary>What the fight does on one frame, and with which profile action.</summary>
public readonly record struct ForeverFightPlan(ForeverFightStep Step, KeyAction? Action = null);

public enum ForeverAdhocStep { Continue, Cast, AuraUnreadable, BuffMissing }

/// <summary>What the Adhoc list asks for on one frame, and for which profile action.</summary>
public readonly record struct ForeverAdhocPlan(ForeverAdhocStep Step, KeyAction? Action = null);

/// <summary>
/// The class profile's actions, one checked decision per fresh visual frame.
/// Nothing here names a class or spell: Pull holds the openers, Combat the
/// priority list, Adhoc the self heals and buffs, and each action's
/// requirements say when to use it, as upstream. A buff is an Adhoc action
/// with <c>AfterCastAuraExpected</c>; its aura is read from its slot's icon.
/// <see cref="Plan"/> and <see cref="PlanAdhoc"/> decide without input; the
/// other members carry the decision out.
/// </summary>
public sealed class ForeverRotation : IForeverCombatRoutine
{
    // Aura enumeration can fail for a frame; out of combat give it this long.
    private const int UnreadableAuraMs = 2000;
    // Out of combat a missing buff holds the session until it can be cast.
    private const int MissingBuffMs = 60000;
    // Start attack waits this long between presses, for the attack state to follow.
    private const int StartAttackRetryMs = 1000;

    private readonly ILogger<ForeverRotation> logger;
    private readonly ForeverCaster caster;
    private readonly ConfigurableInput input;
    private readonly IForeverFacing facing;
    // Keeps the target in front on every frame a cast is awaited.
    private readonly Action<VisualBarSample> faceWhileWaiting;
    private readonly ClassConfiguration config;
    private readonly SpellDB spells;
    private readonly CancellationToken token;
    private readonly HashSet<KeyAction> usedPulls = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> unreadableRequirements = [];
    private long lastStartAttack;
    private long auraUnreadableSince;
    private long buffMissingSince;
    private VisualTargetIdentity? target;

    public ForeverRotation(ILogger<ForeverRotation> logger, ForeverCaster caster,
        ConfigurableInput input, IForeverFacing facing, ClassConfiguration config, SpellDB spells,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.logger = logger;
        this.caster = caster;
        this.input = input;
        this.facing = facing;
        faceWhileWaiting = bars => facing.FaceTarget(bars);
        this.config = config;
        this.spells = spells;
        token = cts.Token;
    }

    public bool TryPreflight(VisualBarSample bars, out string reason)
    {
        foreach (KeyAction action in AllActions())
        {
            if (IsAutoAttack(action))
            {
                if (!ForeverBindings.StartAttack.IsBoundIn(bars))
                {
                    reason = "start attack binding is incomplete";
                    return false;
                }
                continue;
            }
            if (bars.ActionBar?.GetSlot(action.Slot) is not
                    { Kind: VisualActionKind.Spell, Id: var placed } ||
                (action.SpellId > 0 && placed != action.SpellId) ||
                !ForeverBindings.ActionButton(action.Slot)
                    .IsBoundTo(bars, action.ConsoleKey, action.Modifier))
            {
                reason = $"{action.Name} is not on slot {action.Slot} bound to its key";
                return false;
            }
            if (action.AfterCastAuraExpected && bars.SlotAuras?.IsActive(action.Slot) is null)
            {
                reason = $"{action.Name} aura state is unreadable";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    public ForeverCombatResult Update(VisualBarSample bars,
        VisualTargetIdentity identity, out string reason)
    {
        reason = string.Empty;
        if (bars.TargetIdentity != identity || bars.TargetFlags is not { } flags)
        {
            reason = "target identity changed";
            return ForeverCombatResult.Failed;
        }
        if (flags.Dead || bars.TargetHealth <= 0)
            return ForeverCombatResult.TargetDead;
        if (bars.PlayerHealth <= 0 ||
            !flags.Attackable || flags.PlayerDead)
        {
            reason = "combat safety threshold crossed";
            return ForeverCombatResult.Failed;
        }
        if (target != identity)
        {
            target = identity;
            usedPulls.Clear();
        }

        // Attacking and facing go together: the target is turned toward on
        // every frame of the fight, and an attack waits until it is in front.
        bool inFront = facing.FaceTarget(bars);
        ForeverFightPlan plan = Plan(bars, identity);
        if (!inFront && plan.Step is ForeverFightStep.Pull or
                ForeverFightStep.StartAttack or ForeverFightStep.Combat)
            return ForeverCombatResult.Waiting;
        switch (plan.Step)
        {
            case ForeverFightStep.NeedRange:
                return ForeverCombatResult.NeedRange;
            case ForeverFightStep.Pull:
                ForeverCombatResult pulled = Cast(plan.Action!, out reason);
                if (pulled == ForeverCombatResult.ActionSent)
                    usedPulls.Add(plan.Action!);
                return pulled;
            case ForeverFightStep.StartAttack:
                if (!ForeverBindings.StartAttack.TryGetKeyAction(bars, "Forever Start Attack",
                        out KeyAction startAttack))
                {
                    reason = "start attack binding changed";
                    return ForeverCombatResult.Failed;
                }
                input.PressRandom(startAttack, token);
                lastStartAttack = GetTimestamp();
                return ForeverCombatResult.ActionSent;
            case ForeverFightStep.Combat:
                return Cast(plan.Action!, out reason);
            default:
                return ForeverCombatResult.Waiting;
        }
    }

    /// <summary>
    /// The fight's next step against <paramref name="identity"/>: an opener
    /// first, each once per target and only with every buff up; then the
    /// Combat list in priority order, where <c>AutoAttack</c> starts the
    /// attack that fits the distance whenever the attack state asks for it.
    /// </summary>
    public ForeverFightPlan Plan(VisualBarSample bars, VisualTargetIdentity identity)
    {
        ForeverSlotRanges ranges = ForeverSlotRanges.Target(bars);
        bool combatRange = AnyInRange(bars, config.Combat.Sequence, ranges);
        if (NextPull(bars, identity) is { } pull)
            return new(ForeverFightStep.Pull, pull);
        if (!combatRange)
            return new(ForeverFightStep.NeedRange);

        foreach (KeyAction action in config.Combat.Sequence)
        {
            if (IsAutoAttack(action))
            {
                if (NeedsStartAttack(bars))
                    return new(ForeverFightStep.StartAttack, action);
                continue;
            }
            if (RangeCounts(action, ranges) && !Queued(bars, action) && RequirementsMet(action) &&
                ForeverCastGate.CanPress(bars, action, spells,
                    maxLevelsAbove: config.NPCMaxLevels_Above))
                return new(ForeverFightStep.Combat, action);
        }
        return new(ForeverFightStep.Wait);
    }

    // Start attack begins the attack that fits the distance: Auto Shot at
    // range, melee up close. It is pressed when no auto attack runs, or when
    // the target is close and melee is off. The client mostly switches by
    // itself (live, it stopped Auto Shot and started melee as the mob crossed
    // the minimum range), and a press then changes nothing. An unreadable
    // attack state presses nothing.
    private bool NeedsStartAttack(VisualBarSample bars) =>
        bars.Attack is { Melee: false } attack &&
        (!attack.AutoRepeat || attack.TargetClose == true) &&
        GetElapsedTime(lastStartAttack).TotalMilliseconds >= StartAttackRetryMs;

    // A next-swing ability pressed and waiting for its swing shows as current
    // on its slot; pressing it again would only repeat the request.
    private static bool Queued(VisualBarSample bars, KeyAction action) =>
        action.AfterCastWaitSwing && bars.Attack?.IsCurrent(action.Slot) == true;

    // An opener counts only while it can actually be pressed. Otherwise
    // pursuit hands the target to the fight, the fight hands it back for
    // range, and neither ever moves the character closer.
    public bool InRange(VisualBarSample bars, VisualTargetIdentity identity) =>
        AnyInRange(bars, config.Combat.Sequence) ||
        NextPull(bars, identity) is not null;

    public ForeverReach Reach(VisualBarSample bars, ForeverSlotRanges ranges)
    {
        bool combatRange = AnyInRange(bars, config.Combat.Sequence, ranges);
        bool tooClose = combatRange && config.Pull.Sequence.Length > 0;
        foreach (KeyAction action in config.Pull.Sequence)
        {
            if (ranges.InRange(action.Slot) != false)
                tooClose = false;
        }
        return new(AnyInRange(bars, config.Pull.Sequence, ranges), combatRange, tooClose);
    }

    /// <summary>The current target is <see cref="ForeverReach.TooCloseToOpen"/>.</summary>
    public bool TooCloseToOpen(VisualBarSample bars) =>
        Reach(bars, ForeverSlotRanges.Target(bars)).TooCloseToOpen;

    public bool RunAdhoc(VisualBarSample bars, Action beforeCast, out string reason)
    {
        reason = string.Empty;
        ForeverAdhocPlan plan = PlanAdhoc(bars);
        if (plan.Step != ForeverAdhocStep.AuraUnreadable)
            auraUnreadableSince = 0;
        if (plan.Step != ForeverAdhocStep.BuffMissing)
            buffMissingSince = 0;

        switch (plan.Step)
        {
            case ForeverAdhocStep.Cast:
                beforeCast();
                if (caster.TryCast(plan.Action!, Cancelled, selfCast: true, faceWhileWaiting) ==
                    ForeverCastResult.Failed)
                    reason = $"{plan.Action!.Name} success was not confirmed";
                return false;
            case ForeverAdhocStep.AuraUnreadable:
                if (auraUnreadableSince == 0)
                    auraUnreadableSince = GetTimestamp();
                if (GetElapsedTime(auraUnreadableSince).TotalMilliseconds > UnreadableAuraMs)
                    reason = $"{plan.Action!.Name} aura state stayed unreadable";
                return false;
            case ForeverAdhocStep.BuffMissing:
                if (buffMissingSince == 0)
                    buffMissingSince = GetTimestamp();
                if (GetElapsedTime(buffMissingSince).TotalMilliseconds > MissingBuffMs)
                    reason = $"{plan.Action!.Name} stayed missing and could not be cast";
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// The first Adhoc action for the current combat state that has work to
    /// do. A heal that is not needed or not ready waits for its turn. A buff
    /// whose aura shows is done; a missing one holds the session out of combat
    /// until it can be cast, and in combat is best effort. Missing aura
    /// telemetry is not proof that a buff expired.
    /// </summary>
    public ForeverAdhocPlan PlanAdhoc(VisualBarSample bars)
    {
        bool inCombat = bars.TargetFlags is { PlayerCombat: true };
        foreach (KeyAction action in config.Adhoc.Sequence)
        {
            if (!AppliesTo(action, inCombat))
                continue;

            bool buff = action.AfterCastAuraExpected;
            if (buff)
            {
                bool? active = bars.SlotAuras?.IsActive(action.Slot);
                if (active == true)
                    continue;
                if (active is null)
                {
                    if (inCombat)
                        continue;
                    return new(ForeverAdhocStep.AuraUnreadable, action);
                }
            }

            if (RequirementsMet(action) &&
                ForeverCastGate.CanPress(bars, action, spells, selfCast: true,
                    maxLevelsAbove: config.NPCMaxLevels_Above))
                return new(ForeverAdhocStep.Cast, action);

            if (buff && !inCombat)
                return new(ForeverAdhocStep.BuffMissing, action);
        }
        return new(ForeverAdhocStep.Continue);
    }

    /// <summary>
    /// Any spell of <paramref name="actions"/> whose slot has a range to the
    /// current target reports it in range. Readiness is not required, so a
    /// cooldown never sends the fight back to pursuit.
    /// </summary>
    public static bool AnyInRange(VisualBarSample bars, ReadOnlySpan<KeyAction> actions) =>
        AnyInRange(bars, actions, ForeverSlotRanges.Target(bars));

    /// <summary>The same for the unit whose slot ranges are <paramref name="ranges"/>.</summary>
    public static bool AnyInRange(VisualBarSample bars, ReadOnlySpan<KeyAction> actions,
        ForeverSlotRanges ranges)
    {
        foreach (KeyAction action in actions)
        {
            if (!action.BaseAction &&
                bars.ActionBar?.GetSlot(action.Slot) is
                    { Kind: VisualActionKind.Spell, Id: var placed } &&
                (action.SpellId == 0 || placed == action.SpellId) &&
                RangeCounts(action, ranges) &&
                ranges.InRange(action.Slot) == true)
                return true;
        }
        return false;
    }

    // The beta reports a next-swing ability's slot (AfterCastWaitSwing, e.g.
    // Raptor Strike) in range out to about 100 yards, and a press out of
    // melee reach never resolves. Its range counts only while the unit is
    // close, inside a ranged weapon's minimum range, where an attacking mob
    // is in contact or a step away. Otherwise the ranged and melee spells
    // decide, and pursuit closes in.
    private static bool RangeCounts(KeyAction action, ForeverSlotRanges ranges) =>
        !action.AfterCastWaitSwing || ranges.Close == true;

    private KeyAction? NextPull(VisualBarSample bars, VisualTargetIdentity identity)
    {
        bool inCombat = bars.TargetFlags is { PlayerCombat: true };
        if (!BuffsUp(bars, inCombat))
            return null;
        bool sameTarget = target == identity;
        ForeverSlotRanges ranges = ForeverSlotRanges.Target(bars);
        foreach (KeyAction action in config.Pull.Sequence)
        {
            if ((sameTarget && usedPulls.Contains(action)) ||
                !RangeCounts(action, ranges) || Queued(bars, action) ||
                !RequirementsMet(action) ||
                !ForeverCastGate.CanPress(bars, action, spells,
                    maxLevelsAbove: config.NPCMaxLevels_Above))
                continue;
            return action;
        }
        return null;
    }

    // Every buff for the current combat state shows on its slot. An unknown
    // state counts as missing: openers wait, the fight itself does not.
    private bool BuffsUp(VisualBarSample bars, bool inCombat)
    {
        foreach (KeyAction action in config.Adhoc.Sequence)
        {
            if (action.AfterCastAuraExpected && AppliesTo(action, inCombat) &&
                bars.SlotAuras?.IsActive(action.Slot) != true)
                return false;
        }
        return true;
    }

    // Upstream KeyAction.InCombat: "false" (the default) out of combat only,
    // "true" in combat only, anything else in both.
    private static bool AppliesTo(KeyAction action, bool inCombat) =>
        !bool.TryParse(action.InCombat, out bool wanted) || wanted == inCombat;

    private bool IsAutoAttack(KeyAction action) =>
        action.BaseAction && action.Name == config.AutoAttack.Name;

    // KeyAction.CanRun caches its result per Classic GlobalTime tick, and that
    // cell never advances on Forever, so the requirements are evaluated here.
    // The Forever vitals they read throw while stale; that frame counts as no.
    private bool RequirementsMet(KeyAction action)
    {
        ReadOnlySpan<Requirement> span = action.RequirementsRuntime;
        try
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (!span[i].HasRequirement())
                    return false;
            }
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (unreadableRequirements.Add(action.Name))
                logger.LogWarning("Forever requirement of {Action} could not be read: {Reason}",
                    action.Name, ex.Message);
            return false;
        }
    }

    private IEnumerable<KeyAction> AllActions()
    {
        foreach (KeyAction action in config.Pull.Sequence) yield return action;
        foreach (KeyAction action in config.Combat.Sequence) yield return action;
        foreach (KeyAction action in config.Adhoc.Sequence) yield return action;
    }

    private bool Cancelled() => token.IsCancellationRequested;

    private ForeverCombatResult Cast(KeyAction action, out string reason)
    {
        ForeverCastResult result = caster.TryCast(action, Cancelled, whileWaiting: faceWhileWaiting);
        reason = result == ForeverCastResult.Failed ? $"{action.Name} success was not confirmed" : string.Empty;
        return result switch
        {
            ForeverCastResult.Succeeded or ForeverCastResult.Queued => ForeverCombatResult.ActionSent,
            ForeverCastResult.Failed => ForeverCombatResult.Failed,
            _ => ForeverCombatResult.Waiting,
        };
    }
}
