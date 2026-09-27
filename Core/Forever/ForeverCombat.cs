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
        if (bars.PlayerHealth < 0.70 || bars.PlayerPower < 0.25 ||
            !flags.Attackable || flags.PlayerDead)
        {
            reason = "combat safety threshold crossed";
            return ForeverCombatResult.Failed;
        }
        if (target != identity)
        {
            target = identity;
            attackStarted = false;
        }
        if (!ForeverTargetSelector.InStrikeRange(bars) &&
            !flags.PlayerCombat)
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

    private ForeverCombatResult Cast(KeyAction action, out string reason)
    {
        if (caster.Cast(action, () => token.IsCancellationRequested))
        {
            reason = string.Empty;
            return ForeverCombatResult.ActionSent;
        }
        reason = $"{action.Name} success was not confirmed";
        return ForeverCombatResult.Failed;
    }
}
