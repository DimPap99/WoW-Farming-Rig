using Core.Forever;

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
        if (!ForeverFrame.HasTrialInputs(sample) ||
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

        if (alive)
        {
            data |= Bit(GoapKey.withinpullrange,
                ForeverTargetSelector.InJudgementRange(sample));
            data |= Bit(GoapKey.incombatrange,
                ForeverTargetSelector.InStrikeRange(sample));
        }

        state = new(data);
        return true;
    }

    private static int Bit(GoapKey key, bool value) =>
        value ? 1 << (int)key : 0;
}
