using System.Collections.Specialized;

namespace Core.GOAP;

/// <summary>
/// Builds only the GOAP facts that have a checked Forever visual source.
/// Forever sessions register their own goal set, so unknown Classic facts
/// cannot accidentally satisfy a normal goal's false precondition.
/// </summary>
public static class ForeverGoapWorldState
{
    public static bool TryBuild(VisualBarSample sample, out BitVector32 state)
    {
        state = default;
        if (!AddonReader.HasForeverControllerFrame(sample) ||
            sample.TargetFlags is not { } flags)
            return false;

        bool alive = flags.Present && !flags.Dead &&
            sample.TargetHealth > 0;
        int data =
            Bit(GoapKey.hastarget, flags.Present) |
            Bit(GoapKey.targetisalive, alive) |
            Bit(GoapKey.targethostile,
                alive && flags.Attackable) |
            Bit(GoapKey.incombat, flags.PlayerCombat) |
            Bit(GoapKey.isdead, flags.PlayerDead) |
            Bit(GoapKey.pulled, flags.PlayerCombat && alive);

        if (alive && sample.ActionBar is { } bar &&
            sample.ActionStates is { } actions)
        {
            data |= Bit(GoapKey.withinpullrange,
                bar.GetSlot(2) is
                    { Kind: VisualActionKind.Spell, Id: 20271 } &&
                actions.GetSlot(2) is { InRange: true });
            data |= Bit(GoapKey.incombatrange,
                bar.GetSlot(3) is
                    { Kind: VisualActionKind.Spell, Id: 679 } &&
                actions.GetSlot(3) is { InRange: true });
        }

        state = new(data);
        return true;
    }

    private static int Bit(GoapKey key, bool value) =>
        value ? 1 << (int)key : 0;
}
