using Core.Database;
using Core.Goals;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// One frame of the fight. <see cref="StepIn"/>: the game refused the
/// target's reach; close in now. <see cref="Unreachable"/>: refused again and
/// again, or out of sight before the fight began; the unit is given up.
/// <see cref="LostSight"/>: in the fight, the game says the target is out of
/// the player's line of sight; the player walks to see it again.
/// </summary>
public enum ForeverCombatResult { Waiting, ActionSent, NeedRange, TargetDead, Failed, StepIn, StepBack, Unreachable, LostSight }

/// <summary>
/// How the fight stands to the unit. <see cref="Ranged"/>: it is not on the
/// player (it is on the pet, or not pulled yet) and a ranged attack can be
/// used, so the player keeps the range, stepping back to it when the unit
/// comes too close. <see cref="Melee"/>: it goes for the player, or no ranged
/// attack can be used (no ammo, or a class without one), so the player shoots
/// while it comes and fights it in melee once it is there.
/// </summary>
public enum ForeverStance { Ranged, Melee }

/// <summary>What the fight does about a press the game refused with an error message.</summary>
public enum ForeverRefusal { Retry, StepIn, GiveUp, RegainSight }

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
    /// <summary>
    /// The profile's attacks are usable, or lack only mana; false, with
    /// which and why, when the client reports one unusable for another reason.
    /// </summary>
    bool TryAttacksUsable(VisualBarSample bars, out string reason);
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
    /// <summary>
    /// While the player walks to see the target again: presses the next
    /// instant attack without stopping, the walk going on through
    /// <paramref name="keepWalking"/> while the answer is awaited.
    /// <see cref="ForeverCombatResult.ActionSent"/> once one lands, proof the
    /// target is in sight; <see cref="ForeverCombatResult.LostSight"/> while
    /// the game still says it is not; <see cref="ForeverCombatResult.Waiting"/>
    /// when nothing could be pressed on this frame.
    /// </summary>
    ForeverCombatResult ShootOnTheMove(VisualBarSample bars, VisualTargetIdentity identity,
        Action<VisualBarSample> keepWalking, out string reason);
    /// <summary>The run fell back: casting closed by an unproved press opens again (<see cref="ForeverCaster.Release"/>).</summary>
    void ReleaseCasting();
}

public enum ForeverFightStep { Wait, NeedRange, StepBack, Pull, StartAttack, Combat, PetAttack }

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
    // Pet Attack waits this long between presses, for the pet's target to follow.
    private const int PetAttackRetryMs = 1500;
    // A refused reach holds the fight back until the character has stepped
    // this far toward the target, or this long has passed.
    private const float StepInYards = 3;
    private const int StepInMs = 3000;
    // Refused this many times in a row, the target counts as unreachable.
    private const int MaxReachRefusals = 4;
    /// <summary>
    /// A pet's pull: the attacks wait until the mob has been on the pet this
    /// long, so the pet holds it and the player stays at range.
    /// </summary>
    public const int PetHoldsMobMs = 1500;
    /// <summary>A pet that has not drawn the mob after this long is not waited for.</summary>
    public const int PetEngageMs = 8000;
    /// <summary>
    /// <see cref="ForeverStance.Ranged"/> too close for every ranged attack:
    /// the steps back last at most this long. Without the range back by then
    /// (the mob follows, or the ground behind stops the steps) the fight goes
    /// on where it stands for <see cref="StepBackRestMs"/>.
    /// </summary>
    public const int StepBackMs = 2500;
    public const int StepBackRestMs = 4000;

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
    private long lastPetAttack;
    private long auraUnreadableSince;
    private long buffMissingSince;
    private VisualTargetIdentity? target;
    // The last refused reach: the unit, where the player stood, when.
    private VisualTargetIdentity? refusedTarget;
    private Vector2? refusedFrom;
    private long refusedAt;
    private int reachRefusals;
    // The last error message the fight has seen.
    private int seenErrorSequence;
    // The pet's pull of one target: when it began, since when the mob is on
    // the pet, and whether the player's attacks are free.
    private VisualTargetIdentity? petPullTarget;
    private long petPullSince;
    private long mobOnPetSince;
    private bool petPullOver;
    // The target last pulled on its own with the pet held back, said once.
    private VisualTargetIdentity? petHeldBackFor;
    // Stepping back to range: since when, and until when it rests after
    // stepping back did not bring the range back.
    private long stepBackSince;
    private long stepBackRestUntil;

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
            TakeRankFromBar(bars, action);
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
        if (config.AutoPetAttack && bars.Pet is { Active: true } &&
            !ForeverBindings.PetAttack.IsBoundIn(bars))
        {
            reason = "Pet Attack has no key binding; bind it in the client's Key Bindings, " +
                $"or set {nameof(ClassConfiguration.AutoPetAttack)} to false in the profile";
            return false;
        }
        return TryAttacksUsable(bars, out reason);
    }

    /// <summary>
    /// A profile names a spell by one rank's ID, and the player trains higher
    /// ranks as they level; the client puts the new rank on the bar. When the
    /// action's slot holds another rank of the same spell, the action takes
    /// that rank's ID, so the slot checks and the cast confirmation follow
    /// the bar. Another spell on the slot is left for the preflight to refuse.
    /// </summary>
    private void TakeRankFromBar(VisualBarSample bars, KeyAction action)
    {
        if (action.SpellId <= 0 ||
            bars.ActionBar?.GetSlot(action.Slot) is not { Kind: VisualActionKind.Spell, Id: var placed } ||
            placed == action.SpellId ||
            !spells.Spells.TryGetValue(action.SpellId, out Spell named) ||
            !spells.Spells.TryGetValue(placed, out Spell onBar) ||
            !string.Equals(named.Name, onBar.Name, StringComparison.Ordinal))
            return;
        logger.LogInformation("Forever {Action}: slot {Slot} holds the level {Level} rank, spell {Placed}, " +
            "where the profile names {Named}; using the bar's", action.Name, action.Slot, onBar.Level, placed, action.SpellId);
        action.SpellId = placed;
    }

    /// <summary>
    /// The client reports every Combat action, and every opener while the
    /// buffs it waits for are up, usable or short only of mana. Unusable for
    /// another reason means something the attack needs is missing, such as
    /// ammo in the ammo slot or a ranged weapon: a person stops to fix that
    /// rather than fight without it, so the run stops before the next pull.
    /// </summary>
    public bool TryAttacksUsable(VisualBarSample bars, out string reason)
    {
        bool openersCount = BuffsUp(bars, inCombat: false);
        foreach (KeyAction action in config.Combat.Sequence)
        {
            if (Unusable(bars, action))
                return Refuse(action, out reason);
        }
        if (openersCount)
        {
            foreach (KeyAction action in config.Pull.Sequence)
            {
                if (Unusable(bars, action))
                    return Refuse(action, out reason);
            }
        }
        reason = string.Empty;
        return true;

        static bool Refuse(KeyAction action, out string reason)
        {
            reason = $"{action.Name} on slot {action.Slot} is not usable, and not for lack of mana: " +
                "something it needs is missing, such as ammo in the ammo slot or a ranged weapon";
            return false;
        }
    }

    // The client reports the action on its slot unusable, and not for lack of
    // mana or power. A slot without the action is the preflight's to refuse.
    private static bool Unusable(VisualBarSample bars, KeyAction action) =>
        !action.BaseAction &&
        bars.ActionBar?.GetSlot(action.Slot) is { Kind: VisualActionKind.Spell, Id: var placed } &&
        (action.SpellId == 0 || placed == action.SpellId) &&
        bars.ActionStates?.GetSlot(action.Slot) is { Usable: false, NoMana: false };

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
            reachRefusals = 0;
            // Messages from before this target are history.
            seenErrorSequence = bars.LastError?.Sequence ?? 0;
        }
        // Melee is on but cannot reach: the client says "You are too far away!".
        if (bars.LastError is { } error && error.Sequence != seenErrorSequence)
        {
            seenErrorSequence = error.Sequence;
            if (error.Error == VisualGameError.TooFarAway && bars.Attack?.Melee == true)
                return RefuseReach(bars, identity, out reason);
            // Auto Shot reports a target that went out of sight the same way.
            if (ReactionTo(error.Error, flags.PlayerCombat) == ForeverRefusal.RegainSight)
            {
                reason = "the target went out of sight";
                return ForeverCombatResult.LostSight;
            }
        }

        // Attacking and facing go together: the target is turned toward on
        // every frame of the fight, and an attack waits until it is in front.
        bool inFront = facing.FaceTarget(bars);
        ForeverFightPlan plan = Plan(bars, identity);
        // Stepping back waits for the facing too: backing away from a mob
        // behind the player would walk into it.
        if (!inFront && plan.Step is ForeverFightStep.Pull or
                ForeverFightStep.StartAttack or ForeverFightStep.Combat or ForeverFightStep.StepBack)
            return ForeverCombatResult.Waiting;
        switch (plan.Step)
        {
            case ForeverFightStep.NeedRange:
                return ForeverCombatResult.NeedRange;
            case ForeverFightStep.StepBack:
                return ForeverCombatResult.StepBack;
            case ForeverFightStep.Pull:
                ForeverCombatResult pulled = Cast(plan.Action!, bars, identity, out reason);
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
            case ForeverFightStep.PetAttack:
                if (!ForeverBindings.PetAttack.TryGetKeyAction(bars, "Forever Pet Attack",
                        out KeyAction petAttack))
                {
                    reason = "pet attack binding changed";
                    return ForeverCombatResult.Failed;
                }
                input.PressRandom(petAttack, token);
                lastPetAttack = GetTimestamp();
                logger.LogInformation("Forever Pet Attack pressed; the pet was {PetTarget}",
                    bars.Pet is { TargetHash1: 0, TargetHash2: 0 } ? "on no unit" : "on another unit");
                return ForeverCombatResult.ActionSent;
            case ForeverFightStep.Combat:
                return Cast(plan.Action!, bars, identity, out reason);
            default:
                return ForeverCombatResult.Waiting;
        }
    }

    /// <summary>
    /// What the fight does about a press the game refused with
    /// <paramref name="error"/>, as upstream reacts to the same messages: out
    /// of range or melee reach it steps in; out of sight before the fight
    /// began it gives the unit up, as upstream clears such a target, and in
    /// combat it steps in instead, since that unit fights on. Anything else,
    /// such as not ready or too close, is tried again.
    /// </summary>
    public static ForeverRefusal ReactionTo(VisualGameError error, bool inCombat) => error switch
    {
        VisualGameError.LineOfSight => inCombat ? ForeverRefusal.RegainSight : ForeverRefusal.GiveUp,
        VisualGameError.OutOfRange or VisualGameError.TooFarAway => ForeverRefusal.StepIn,
        _ => ForeverRefusal.Retry,
    };

    // The game refused the target's reach: the fight holds back until the
    // character stepped toward it. A refusal while one is still open
    // renews it without counting; too many in a row give the unit up.
    private ForeverCombatResult RefuseReach(VisualBarSample bars, VisualTargetIdentity identity,
        out string reason)
    {
        reason = string.Empty;
        if (!ReachRefused(bars, identity) && ++reachRefusals >= MaxReachRefusals)
        {
            reachRefusals = 0;
            refusedAt = 0;
            reason = $"the game refused the target's reach {MaxReachRefusals} times";
            return ForeverCombatResult.Unreachable;
        }
        refusedTarget = identity;
        refusedAt = GetTimestamp();
        refusedFrom = bars.WorldPosition is { } world ? new((float)world.X, (float)world.Y) : null;
        return ForeverCombatResult.StepIn;
    }

    // The game refused the target's reach a moment ago, and the character
    // has not yet stepped toward it.
    private bool ReachRefused(VisualBarSample bars, VisualTargetIdentity identity) =>
        refusedAt != 0 && refusedTarget == identity &&
        GetElapsedTime(refusedAt).TotalMilliseconds < StepInMs &&
        !(refusedFrom is { } from && bars.WorldPosition is { } world &&
          Vector2.Distance(new((float)world.X, (float)world.Y), from) >= StepInYards);

    /// <summary>
    /// The fight's next step against <paramref name="identity"/>: the pet
    /// sent in first, whenever it attacks something else, and on a pull held
    /// to until the mob is on it; an opener next, each once per target and
    /// only with every buff up; then the Combat list in priority order, where
    /// <c>AutoAttack</c> starts the attack that fits the distance whenever the
    /// attack state asks for it.
    /// </summary>
    public ForeverFightPlan Plan(VisualBarSample bars, VisualTargetIdentity identity)
    {
        // Ranged while the mob is not on the player: too close for every
        // ranged attack, a hunter steps back to range rather than melee it.
        if (StepsBack(bars))
            return new(ForeverFightStep.StepBack);
        if (ReachRefused(bars, identity))
            return new(ForeverFightStep.NeedRange);
        ForeverSlotRanges ranges = ForeverSlotRanges.Target(bars);
        bool combatRange = AnyInRange(bars, config.Combat.Sequence, ranges);
        KeyAction? pull = NextPull(bars, identity);
        // Out of reach the fight hands the target back for range, and the pet
        // waits too: a press here would hold that hand-back off.
        if (pull is null && !combatRange)
            return new(ForeverFightStep.NeedRange);
        if (SendsPet(bars, identity))
            return new(ForeverFightStep.PetAttack);
        if (WaitsForPet(bars, identity))
            return new(ForeverFightStep.Wait);
        if (pull is not null)
            return new(ForeverFightStep.Pull, pull);

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

    public void ReleaseCasting() => caster.Release();

    public ForeverCombatResult ShootOnTheMove(VisualBarSample bars, VisualTargetIdentity identity,
        Action<VisualBarSample> keepWalking, out string reason)
    {
        reason = string.Empty;
        if (bars.TargetIdentity != identity || NextShotOnTheMove(bars) is not { } shot)
            return ForeverCombatResult.Waiting;
        switch (caster.TryCast(shot, Cancelled, whileWaiting: keepWalking))
        {
            case ForeverCastResult.Succeeded:
                return ForeverCombatResult.ActionSent;
            case ForeverCastResult.Refused when caster.LastRefusal.Error == VisualGameError.LineOfSight:
                seenErrorSequence = caster.LastRefusal.Sequence;
                reason = $"{shot.Name} was refused: the target is still out of sight";
                return ForeverCombatResult.LostSight;
            case ForeverCastResult.Refused:
                seenErrorSequence = caster.LastRefusal.Sequence;
                return ForeverCombatResult.Waiting;
            case ForeverCastResult.Failed:
                reason = $"{shot.Name} success was not confirmed";
                return ForeverCombatResult.Failed;
            default:
                return ForeverCombatResult.Waiting;
        }
    }

    /// <summary>
    /// The Combat action to press while walking: the first in priority order
    /// that is instant, as a press on the move must be, ready and in range by
    /// its slot, with the target on screen, which puts it in front. Never
    /// <c>AutoAttack</c> nor a next-swing ability, which need the player
    /// standing or in contact.
    /// </summary>
    public KeyAction? NextShotOnTheMove(VisualBarSample bars)
    {
        if (bars.TargetScreenPosition is null)
            return null;
        ForeverSlotRanges ranges = ForeverSlotRanges.Target(bars);
        foreach (KeyAction action in config.Combat.Sequence)
        {
            if (IsAutoAttack(action) || action.AfterCastWaitSwing || action.HasCastBar ||
                ranges.InRange(action.Slot) != true || !RequirementsMet(action) ||
                !ForeverCastGate.CanPress(bars, action, spells,
                    maxLevelsAbove: config.NPCMaxLevels_Above))
                continue;
            return action;
        }
        return null;
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

    // Pet Attack sends a living pet at the target while it attacks nothing or
    // something else, as upstream does; between presses its target has time
    // to follow. No facing needed: a person sends the pet while turning.
    private bool SendsPet(VisualBarSample bars, VisualTargetIdentity identity) =>
        config.AutoPetAttack &&
        bars.Pet is { Active: true } pet && !pet.Attacks(identity) &&
        ForeverBindings.PetAttack.IsBoundIn(bars) &&
        GetElapsedTime(lastPetAttack).TotalMilliseconds >= PetAttackRetryMs &&
        !HoldsPetBack(bars, identity);

    // A mob with another hostile unit standing near it is pulled on its own:
    // the opener draws it to the player, and the pet stays back until it
    // comes, since the pet's run in would draw the other one too. Out of
    // combat only: once the mob fights, the pet goes in.
    private bool HoldsPetBack(VisualBarSample bars, VisualTargetIdentity identity)
    {
        if (bars.TargetFlags is not { PlayerCombat: false } || bars.Nameplates?.Target is not { } plate ||
            ForeverPullOptions.HostileNear(bars, plate) == 0)
            return false;
        if (petHeldBackFor != identity)
        {
            petHeldBackFor = identity;
            logger.LogInformation("Forever pulling the mob on its own: another hostile unit stands near it, " +
                "so the shot goes first and the pet once the mob comes");
        }
        return true;
    }

    // A Hunter's pull: the pet goes in first and the player holds its attacks
    // until the mob has turned on the pet and stayed on it a moment, so the
    // mob stays on the pet and the player at range. Nothing is held once the
    // mob, or anything else, goes for the player, nor for a pet that has not
    // drawn the mob in time; once the hold is over it stays over for that
    // target. Without the roster nothing tells where the mob is, so the hold
    // runs to its bound.
    private bool WaitsForPet(VisualBarSample bars, VisualTargetIdentity identity)
    {
        if (!config.AutoPetAttack || bars.Pet is not { Active: true } pet)
            return false;
        if (petPullTarget != identity)
        {
            petPullTarget = identity;
            petPullSince = GetTimestamp();
            mobOnPetSince = 0;
            petPullOver = false;
        }
        if (petPullOver)
            return false;
        // Pulled on its own, the shot goes first: nothing waits for the pet.
        if (HoldsPetBack(bars, identity))
            return false;
        bool goesForPlayer = AnyGoesForPlayer(bars.Nameplates);
        if (goesForPlayer || bars.Attack is { AutoRepeat: true } or { Melee: true })
        {
            logger.LogInformation("Forever pet pull not held: {Why}",
                goesForPlayer ? "a unit goes for the player" : "an auto attack already runs");
            petPullOver = true;
            return false;
        }
        if (pet.Attacks(identity) && bars.Nameplates?.Target is { TargetsPet: true })
        {
            if (mobOnPetSince == 0)
                mobOnPetSince = GetTimestamp();
            if (GetElapsedTime(mobOnPetSince).TotalMilliseconds < PetHoldsMobMs)
                return true;
            logger.LogInformation("Forever pet holds the mob; the attacks begin");
            petPullOver = true;
            return false;
        }
        mobOnPetSince = 0;
        if (GetElapsedTime(petPullSince).TotalMilliseconds < PetEngageMs)
            return true;
        logger.LogInformation("Forever pet did not draw the mob within {Seconds} seconds; the attacks begin",
            PetEngageMs / 1000);
        petPullOver = true;
        return false;
    }

    private static bool AnyGoesForPlayer(VisualNameplateRoster? roster)
    {
        for (int slot = 1; roster is not null && slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is { IsEmpty: false, TargetsPlayer: true })
                return true;
        }
        return false;
    }

    /// <summary>
    /// How the fight stands to the target (<see cref="ForeverStance"/>):
    /// ranged only when its own plate says it targets someone other than the
    /// player and a ranged attack can be used; melee otherwise, including when
    /// the roster cannot tell.
    /// </summary>
    public ForeverStance Stance(VisualBarSample bars)
    {
        if (bars.Nameplates?.Target is not { TargetsPlayer: false })
            return ForeverStance.Melee;
        foreach (KeyAction action in RangedAttacks())
        {
            if (Placed(bars, action) && !Unusable(bars, action))
                return ForeverStance.Ranged;
        }
        return ForeverStance.Melee;
    }

    // Ranged and too close for every ranged attack that can be used: inside
    // a ranged weapon's minimum range, the client reports each such attack's
    // slot out of range while the target is close. A Paladin's Judgement
    // reaches a target in melee, so a Paladin never is. Steps back for at
    // most StepBackMs, then rests StepBackRestMs, fighting where it stands.
    private bool StepsBack(VisualBarSample bars)
    {
        long now = GetTimestamp();
        if (now < stepBackRestUntil || Stance(bars) != ForeverStance.Ranged || !TooCloseForRanged(bars))
        {
            stepBackSince = 0;
            return false;
        }
        if (stepBackSince == 0)
        {
            stepBackSince = now;
            logger.LogInformation("Forever fight: the mob is not on the player and too close for the ranged attacks; stepping back to range");
        }
        if (GetElapsedTime(stepBackSince).TotalMilliseconds <= StepBackMs)
            return true;
        stepBackSince = 0;
        stepBackRestUntil = now + (long)(StepBackRestMs * (Frequency / 1000.0));
        logger.LogInformation("Forever fight: stepping back did not bring the range back; fighting where it stands for now");
        return false;
    }

    private bool TooCloseForRanged(VisualBarSample bars)
    {
        ForeverSlotRanges ranges = ForeverSlotRanges.Target(bars);
        if (ranges.Close != true)
            return false;
        bool any = false;
        foreach (KeyAction action in RangedAttacks())
        {
            if (!Placed(bars, action) || Unusable(bars, action) || ranges.InRange(action.Slot) is not { } inRange)
                continue;
            if (inRange)
                return false;
            any = true;
        }
        return any;
    }

    // The profile's attacks other than the auto attack and next-swing melee
    // abilities: the shots, and for a Paladin, Judgement.
    private IEnumerable<KeyAction> RangedAttacks()
    {
        foreach (KeyAction action in config.Pull.Sequence)
        {
            if (!action.BaseAction && !action.AfterCastWaitSwing && !IsAutoAttack(action))
                yield return action;
        }
        foreach (KeyAction action in config.Combat.Sequence)
        {
            if (!action.BaseAction && !action.AfterCastWaitSwing && !IsAutoAttack(action))
                yield return action;
        }
    }

    // The action's spell is on its slot, as the profile names it.
    private static bool Placed(VisualBarSample bars, KeyAction action) =>
        bars.ActionBar?.GetSlot(action.Slot) is { Kind: VisualActionKind.Spell, Id: var placed } &&
        (action.SpellId == 0 || placed == action.SpellId);

    // A next-swing ability pressed and waiting for its swing shows as current
    // on its slot; pressing it again would only repeat the request.
    private static bool Queued(VisualBarSample bars, KeyAction action) =>
        action.AfterCastWaitSwing && bars.Attack?.IsCurrent(action.Slot) == true;

    // An opener counts only while it can actually be pressed. Otherwise
    // pursuit hands the target to the fight, the fight hands it back for
    // range, and neither ever moves the character closer. A reach the game
    // just refused does not count until the character stepped in.
    public bool InRange(VisualBarSample bars, VisualTargetIdentity identity) =>
        !ReachRefused(bars, identity) &&
        (AnyInRange(bars, config.Combat.Sequence) || NextPull(bars, identity) is not null);

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
    /// cooldown never sends the fight back to pursuit. A spell the client
    /// reports unusable for a reason other than mana, such as a shot without
    /// ammo, reaches nothing: the fight closes in to what it can still use
    /// rather than stand at range and start a melee attack there.
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
                !Unusable(bars, action) &&
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

    private ForeverCombatResult Cast(KeyAction action, VisualBarSample bars, VisualTargetIdentity identity,
        out string reason)
    {
        ForeverCastResult result = caster.TryCast(action, Cancelled, whileWaiting: faceWhileWaiting);
        reason = result == ForeverCastResult.Failed ? $"{action.Name} success was not confirmed" : string.Empty;
        switch (result)
        {
            case ForeverCastResult.Succeeded or ForeverCastResult.Queued:
                reachRefusals = 0;
                return ForeverCombatResult.ActionSent;
            case ForeverCastResult.Failed:
                return ForeverCombatResult.Failed;
            case ForeverCastResult.Refused:
                // The fight has seen this message; the next update does not take it again.
                seenErrorSequence = caster.LastRefusal.Sequence;
                switch (ReactionTo(caster.LastRefusal.Error, bars.TargetFlags is { PlayerCombat: true }))
                {
                    case ForeverRefusal.StepIn:
                        return RefuseReach(bars, identity, out reason);
                    case ForeverRefusal.RegainSight:
                        reason = $"{action.Name} was refused: the target is out of sight";
                        return ForeverCombatResult.LostSight;
                    case ForeverRefusal.GiveUp:
                        reason = $"{action.Name} was refused: the target is out of sight";
                        return ForeverCombatResult.Unreachable;
                }
                return ForeverCombatResult.Waiting;
            default:
                return ForeverCombatResult.Waiting;
        }
    }
}
