using Core.Goals;
using Core.GOAP;

using Game;

using SharedLib;

using System;
using System.Linq;
using System.Threading;

namespace Core.Forever;

public enum ForeverCombatResult { Waiting, ActionSent, NeedRange, TargetDead, Failed }

public interface IForeverCombatRoutine
{
    bool InRange(VisualBarSample bars, VisualTargetIdentity identity);
    bool EnsureSeal(VisualBarSample bars, out string reason);
    ForeverCombatResult Update(VisualBarSample bars,
        VisualTargetIdentity identity, out string reason);
}

/// <summary>One checked Paladin combat decision per fresh visual frame.</summary>
public sealed class ForeverCombat : IForeverCombatRoutine
{
    private readonly CastingHandler caster;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration config;
    private readonly SpellBookReader spells;
    private readonly CancellationToken token;
    private bool attackStarted;
    private bool judgedTarget;
    private long sealUnreadableAt;
    private VisualTargetIdentity? target;

    public ForeverCombat(CastingHandler caster, ConfigurableInput input,
        ClassConfiguration config, SpellBookReader spells,
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
            KeyAction? judgement = config.Combat.Sequence.SingleOrDefault(
                action => action.SpellId == 20271);
            if (judgement is null)
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
            if (!ForeverTargetSelector.HasBinding(bars, 15,
                    BindingID.STARTATTACK, ConsoleKey.D2,
                    ModifierKey.Shift))
            {
                reason = "start attack binding changed";
                return ForeverCombatResult.Failed;
            }
            input.PressRandom(new KeyAction
            {
                Name = "Forever Start Attack", BaseAction = true,
                ConsoleKey = ConsoleKey.D2, Modifier = ModifierKey.Shift,
                PressDuration = 75
            }, token);
            attackStarted = true;
            return ForeverCombatResult.ActionSent;
        }

        KeyAction? strike = config.Combat.Sequence.SingleOrDefault(
            action => action.SpellId is 678 or 679);
        if (strike is null)
        {
            reason = "Holy Strike action missing";
            return ForeverCombatResult.Failed;
        }
        if (ForeverCastGate.CanPress(bars, strike, spells))
            return Cast(strike, out reason);

        return ForeverCombatResult.Waiting;
    }

    public bool InRange(VisualBarSample bars, VisualTargetIdentity identity) =>
        ForeverTargetSelector.InStrikeRange(bars) ||
        (bars.SealOfRighteousnessActive == true &&
            (target != identity || !judgedTarget) &&
            ForeverTargetSelector.InJudgementRange(bars));

    public bool EnsureSeal(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.SealOfRighteousnessActive is null)
        {
            // Missing telemetry is not proof that the buff expired. Keep
            // defending with melee while withholding Seal and Judgement.
            if (bars.TargetFlags is { PlayerCombat: true })
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
        if (bars.TargetFlags is { PlayerCombat: true } && bars.PlayerPower < 0.25)
            return true;
        KeyAction? seal = config.Combat.Sequence.SingleOrDefault(
            action => action.SpellId == 20287);
        if (seal is null)
        {
            reason = "Seal of Righteousness action missing";
            return false;
        }
        if (caster.TryCastForever(seal, () => token.IsCancellationRequested) ==
            ForeverCastResult.Failed)
            reason = "Seal cast success was not confirmed";
        return false;
    }

    private ForeverCombatResult Cast(KeyAction action, out string reason)
    {
        ForeverCastResult result = caster.TryCastForever(action,
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
