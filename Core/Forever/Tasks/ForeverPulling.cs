namespace Core.Forever;

public enum ForeverPull { Nothing, Offered, Unusable }

/// <summary>
/// How a task takes a unit in view out of combat: an attack the client
/// reports unusable for a reason other than mana stops the run first, as
/// before every pull; otherwise the plate the target policy prefers is
/// offered to <see cref="ForeverEngageActivity"/>.
/// </summary>
public static class ForeverPulling
{
    public static ForeverPull TryPull(VisualBarSample bars, IForeverCombatRoutine combat,
        ClassConfiguration config, ForeverEngageActivity engage, out string reason)
    {
        if (!combat.TryAttacksUsable(bars, out reason))
            return ForeverPull.Unusable;
        if (!ForeverTargetPolicy.TryChoose(bars, config, combat, engage.Exclusions, out VisualNameplate chosen))
            return ForeverPull.Nothing;
        engage.Offer(chosen);
        return ForeverPull.Offered;
    }
}
