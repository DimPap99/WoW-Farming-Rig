using Core.GOAP;

using Game;

using SharedLib;

using System;
using System.Threading;

namespace Core.Forever;

/// <summary>Checks the live target and the game's targeting binding before input.</summary>
public sealed class ForeverTargetSelector
{
    private readonly ConfigurableInput input;
    private readonly CancellationToken token;

    public ForeverTargetSelector(ConfigurableInput input,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.input = input;
        token = cts.Token;
    }

    public static bool CanSelect(VisualBarSample bars) =>
        AddonReader.HasForeverTrialFrame(bars) &&
        bars.TargetFlags is { Present: false, PlayerDead: false } flags &&
        bars.PlayerHealth >= 0.70 &&
        bars.PlayerPower >= (flags.PlayerCombat ? 0.25 : 0.60) &&
        HasBinding(bars, 9, BindingID.TARGETNEARESTENEMY,
            ConsoleKey.Tab, ModifierKey.None);

    public bool SelectNearest(VisualBarSample bars)
    {
        if (!CanSelect(bars))
            return false;
        try
        {
            input.PressFixed(ConsoleKey.Tab, 75, token);
            return true;
        }
        finally
        {
            input.SetKeyState(ConsoleKey.Tab, false, true);
        }
    }

    public static bool TrySafeTarget(VisualBarSample bars,
        out VisualTargetIdentity identity)
    {
        identity = default;
        if (!AddonReader.HasForeverTargetIdentity(bars) ||
            bars.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerDead: false } flags ||
            bars.TargetIdentity is not { Present: true, IsPlayer: false } target ||
            bars.PlayerIdentity is not { } player ||
            target.Level > player.Level + 1 ||
            bars.TargetHealth <= 0 || bars.PlayerHealth < 0.70 ||
            bars.PlayerPower < (flags.PlayerCombat ? 0.25 : 0.60))
            return false;
        identity = target;
        return true;
    }

    public static bool InJudgementRange(VisualBarSample bars) =>
        bars.ActionBar?.GetSlot(2) is
            { Kind: VisualActionKind.Spell, Id: 20271 } &&
        bars.ActionStates?.GetSlot(2) is { InRange: true };

    public static bool InStrikeRange(VisualBarSample bars) =>
        bars.ActionBar?.GetSlot(3) is
            { Kind: VisualActionKind.Spell, Id: 678 or 679 } &&
        bars.ActionStates?.GetSlot(3) is { InRange: true };

    public void ClearTarget()
    {
        try
        {
            input.PressFixed(ConsoleKey.Escape, 75, token);
        }
        finally
        {
            input.SetKeyState(ConsoleKey.Escape, false, true);
        }
    }

    public static bool HasBinding(VisualBarSample bars, int index,
        BindingID command, ConsoleKey key, ModifierKey modifier)
    {
        int? code = bars.Bindings?.GetCode(index);
        if (code is not > 0 ||
            KeyReader.DecodeBinding(code.Value) is not { } decoded ||
            decoded.bindingId != command)
            return false;
        return (decoded.key1 == key && decoded.mod1 == modifier) ||
            (decoded.key2 == key && decoded.mod2 == modifier);
    }
}
