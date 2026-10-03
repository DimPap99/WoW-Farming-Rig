namespace Core.Forever;

/// <summary>
/// Which checked inputs one visual frame must carry before the guarded Forever
/// runtime acts on it. Each predicate reads a single capture.
/// </summary>
public static class ForeverFrame
{
    /// <summary>The game's own <c>Enum.PowerType.Mana</c>, not <see cref="PowerType.Mana"/>.</summary>
    public const int ManaPowerType = 0;

    /// <summary>
    /// Everything the route, target and cast decisions read. The bag rows are
    /// left out: the bounded trial does not read inventory, and the bottom of
    /// the overlay can be clipped.
    /// </summary>
    public static bool HasTrialInputs(VisualBarSample bars) =>
        bars.Position is { UIMapId: > 0 } &&
        bars.WorldPosition is not null &&
        bars.PlayerIdentity is { Level: > 0 } &&
        bars.PlayerPowerType is not null &&
        bars.PlayerHealthMax is > 0 && bars.PlayerPowerMax is > 0 &&
        bars.TargetFlags is { } flags &&
        bars.TargetPresent == flags.Present &&
        bars.LastCastEvent is not null &&
        bars.ActionBar is
            { ReadableCount: VisualActionBarSnapshot.SlotCount } &&
        bars.ActionStates is
            { ReadableCount: VisualActionBarSnapshot.SlotCount } &&
        bars.Cooldowns is
            { ReadableCount: VisualCooldownGridSnapshot.SlotCount } &&
        bars.SpellBook is { Count: > 0 } book &&
        book.ReadableCount == book.Count &&
        bars.Bindings is not null;

    /// <summary>The trial inputs plus a complete bag grid.</summary>
    public static bool HasControllerInputs(VisualBarSample bars) =>
        HasTrialInputs(bars) && bars.Bags is { IsComplete: true };

    /// <summary>The trial inputs plus a target identity that agrees with the target flags.</summary>
    public static bool HasTargetIdentity(VisualBarSample bars) =>
        HasTrialInputs(bars) &&
        bars.TargetFlags is { } flags &&
        bars.TargetIdentity is { } identity &&
        flags.Present == identity.Present;

    /// <summary>
    /// The trial inputs, from a mana-using Paladin in the open world on the
    /// one verified map.
    /// </summary>
    public static bool IsVerifiedSetup(VisualBarSample bars) =>
        HasTrialInputs(bars) &&
        bars.Position is { UIMapId: ForeverPaladin.UIMapId } &&
        bars.WorldPosition is { InstanceMapId: 0 } &&
        bars.PlayerIdentity is { ClassId: (int)UnitClass.Paladin } &&
        bars.PlayerPowerType == ManaPowerType;
}
