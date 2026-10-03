using Core.Database;
using Core.Goals;
using Core.GOAP;

using SharedLib;

using System;
using System.Linq;
using System.Threading;

namespace Core.Forever;

public enum ForeverCombatResult { Waiting, ActionSent, NeedRange, TargetDead, Failed }

public interface IForeverCombatRoutine
{
    bool InRange(VisualBarSample bars, VisualTargetIdentity identity);
    /// <param name="beforeCast">Runs right before a blocking seal cast, so the
    /// caller can release held movement before the wait for the success event.</param>
    bool EnsureSeal(VisualBarSample bars, Action beforeCast, out string reason);
    ForeverCombatResult Update(VisualBarSample bars,
        VisualTargetIdentity identity, out string reason);
}

/// <summary>One checked Paladin combat decision per fresh visual frame.</summary>
public sealed class ForeverCombat : IForeverCombatRoutine
{
    private readonly ForeverCaster caster;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration config;
    private readonly SpellDB spells;
    private readonly CancellationToken token;
    private bool attackStarted;
    private bool judgedTarget;
    private long sealUnreadableAt;
    private VisualTargetIdentity? target;

    public ForeverCombat(ForeverCaster caster, ConfigurableInput input,
        ClassConfiguration config, SpellDB spells,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.caster = caster;
        this.input = input;
        this.config = config;
        this.spells = spells;
        token = cts.Token;
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
            attackStarted = false;
            judgedTarget = false;
        }
        if (!InRange(bars, identity))
            return ForeverCombatResult.NeedRange;

        if (!judgedTarget && ForeverTargetSelector.InJudgementRange(bars))
        {
            if (Action(ForeverPaladin.Judgement) is not { } judgement)
            {
                reason = "Judgement action missing";
                return ForeverCombatResult.Failed;
            }
            if (ForeverCastGate.CanPress(bars, judgement, spells))
            {
                ForeverCombatResult result = Cast(judgement, out reason);
                if (result == ForeverCombatResult.ActionSent)
                    judgedTarget = true;
                return result;
            }
        }

        if (!ForeverTargetSelector.InStrikeRange(bars))
            return ForeverCombatResult.NeedRange;

        if (!attackStarted)
        {
            if (!ForeverBindings.StartAttack.IsBoundIn(bars))
            {
                reason = "start attack binding changed";
                return ForeverCombatResult.Failed;
            }
            input.PressRandom(ForeverBindings.StartAttack.ToKeyAction("Forever Start Attack"), token);
            attackStarted = true;
            return ForeverCombatResult.ActionSent;
        }

        if (config.Combat.Sequence.SingleOrDefault(
                action => ForeverPaladin.IsHolyStrike(action.SpellId)) is not { } strike)
        {
            reason = "Holy Strike action missing";
            return ForeverCombatResult.Failed;
        }
        if (ForeverCastGate.CanPress(bars, strike, spells))
            return Cast(strike, out reason);

        return ForeverCombatResult.Waiting;
    }

    // Judgement range counts only while Judgement can actually be pressed.
    // Otherwise pursuit hands the target to the fight, the fight hands it back
    // for range, and neither ever moves the character closer.
    public bool InRange(VisualBarSample bars, VisualTargetIdentity identity) =>
        ForeverTargetSelector.InStrikeRange(bars) ||
        ((target != identity || !judgedTarget) &&
            ForeverTargetSelector.InJudgementRange(bars) &&
            Action(ForeverPaladin.Judgement) is { } judgement &&
            ForeverCastGate.CanPress(bars, judgement, spells));

    public bool EnsureSeal(VisualBarSample bars, Action beforeCast, out string reason)
    {
        reason = string.Empty;
        bool inCombat = bars.TargetFlags is { PlayerCombat: true };
        if (bars.SealOfRighteousnessActive is null)
        {
            // Missing telemetry is not proof that the buff expired. Keep
            // defending with melee while withholding Seal and Judgement.
            if (inCombat)
                return true;
            sealUnreadableAt = sealUnreadableAt == 0
                ? Environment.TickCount64 : sealUnreadableAt;
            if (Environment.TickCount64 - sealUnreadableAt > 2000)
                reason = "Seal aura state stayed unreadable";
            return false;
        }
        sealUnreadableAt = 0;
        if (bars.SealOfRighteousnessActive == true)
            return true;
        if (Action(ForeverPaladin.SealOfRighteousness) is not { } seal)
        {
            reason = "Seal of Righteousness action missing";
            return false;
        }
        // In combat the Seal is best effort. Keep fighting while it cannot be
        // cast, for example below the 70% health or 25% mana the gate needs.
        if (inCombat && !ForeverCastGate.CanPress(bars, seal, spells))
            return true;
        beforeCast();
        if (caster.TryCast(seal, () => token.IsCancellationRequested) ==
            ForeverCastResult.Failed)
            reason = "Seal cast success was not confirmed";
        return false;
    }

    private KeyAction? Action(int spellId) =>
        config.Combat.Sequence.SingleOrDefault(action => action.SpellId == spellId);

    private ForeverCombatResult Cast(KeyAction action, out string reason)
    {
        ForeverCastResult result = caster.TryCast(action,
            () => token.IsCancellationRequested);
        if (result != ForeverCastResult.Failed)
        {
            reason = string.Empty;
            return result == ForeverCastResult.Succeeded
                ? ForeverCombatResult.ActionSent : ForeverCombatResult.Waiting;
        }
        reason = $"{action.Name} success was not confirmed";
        return ForeverCombatResult.Failed;
    }
}
