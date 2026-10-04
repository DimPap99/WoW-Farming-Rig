namespace Core.Forever;

/// <summary>
/// Which checked inputs one visual frame must carry before the guarded Forever
/// runtime acts on it, and the resource levels it acts at. Each predicate reads
/// a single capture.
/// </summary>
public static class ForeverFrame
{
    /// <summary>The game's own <c>Enum.PowerType.Mana</c>, not <see cref="PowerType.Mana"/>.</summary>
    public const int ManaPowerType = 0;

    /// <summary>Health needed to select or open on a target.</summary>
    public const double ReadyHealth = 0.70;
    /// <summary>Mana needed to open a fight out of combat.</summary>
    public const double ReadyMana = 0.60;
    /// <summary>Mana needed to take on a target while already in combat.</summary>
    public const double CombatMana = 0.25;
    /// <summary>Health recovery waits for between fights.</summary>
    public const double RestedHealth = 0.90;
    /// <summary>
    /// Highest target level accepted, relative to the player's, when no profile
    /// says otherwise; the same default as <see cref="ClassConfiguration.NPCMaxLevels_Above"/>.
    /// </summary>
    public const int DefaultMaxTargetLevelsAbove = 1;

    /// <summary>
    /// Everything the route, target and cast decisions read. The bag rows are
    /// left out: the bounded trial does not read inventory, and the bottom of
    /// the overlay can be clipped.
    /// </summary>
    public static bool HasTrialInputs(VisualBarSample bars) => MissingTrialInput(bars) is null;

    /// <summary>The first of the <see cref="HasTrialInputs"/> that the frame lacks, as the log names it; null with none.</summary>
    public static string? MissingTrialInput(VisualBarSample bars) =>
        bars.Position is not { UIMapId: > 0 } ? "the map position" :
        bars.WorldPosition is null ? "the world position" :
        bars.PlayerIdentity is not { Level: > 0 } ? "the player's identity" :
        bars.PlayerPowerType is null ? "the power type" :
        bars.PlayerHealthMax is not > 0 || bars.PlayerPowerMax is not > 0 ? "the health and power maximums" :
        bars.TargetFlags is not { } flags || bars.TargetPresent != flags.Present ? "the target flags" :
        bars.LastCastEvent is null ? "the cast latch" :
        bars.ActionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount } ? "the action bar" :
        bars.ActionStates is not { ReadableCount: VisualActionBarSnapshot.SlotCount } ? "the action states" :
        bars.Cooldowns is not { ReadableCount: VisualCooldownGridSnapshot.SlotCount } ? "the cooldowns" :
        bars.SpellBook is not { Count: > 0 } book || book.ReadableCount != book.Count ? "the spellbook" :
        bars.Bindings is null ? "the key bindings" :
        null;

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
    /// The trial inputs, from a verified class. Any zone will do: walking
    /// checks the map it is on (<see cref="ForeverNavigator"/>).
    /// </summary>
    public static bool IsVerifiedSetup(VisualBarSample bars) =>
        HasTrialInputs(bars) &&
        bars.PlayerIdentity is { } player &&
        ForeverVerifiedSetup.IsVerifiedClass(player.ClassId);

    /// <summary>
    /// At least <paramref name="fraction"/> mana. Other power types always pass:
    /// rage starts empty and builds in the fight, energy refills on its own.
    /// </summary>
    public static bool HasMana(VisualBarSample bars, double fraction) =>
        bars.PlayerPowerType != ManaPowerType || bars.PlayerPower >= fraction;

    /// <summary>Health and mana to select or open on a target.</summary>
    public static bool CanOpenFight(VisualBarSample bars, bool inCombat) =>
        bars.PlayerHealth >= ReadyHealth &&
        HasMana(bars, inCombat ? CombatMana : ReadyMana);

    /// <summary>Health and mana recovery waits for before the next target.</summary>
    public static bool IsRested(VisualBarSample bars) =>
        bars.PlayerHealth >= RestedHealth && HasMana(bars, ReadyMana);
}
