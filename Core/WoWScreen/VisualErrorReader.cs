namespace Core;

/// <summary>
/// The game's error messages the Forever runtime reacts to, as the add-on
/// codes them (ForeverEvents.lua). Several messages share one kind.
/// </summary>
public enum VisualGameError
{
    None = 0,
    /// <summary>"Target not in line of sight".</summary>
    LineOfSight = 1,
    /// <summary>"You are too far away!": melee, a body to loot or a unit to interact with is out of reach.</summary>
    TooFarAway = 2,
    /// <summary>"Out of range".</summary>
    OutOfRange = 3,
    /// <summary>"Target too close": inside a ranged attack's minimum range.</summary>
    TooClose = 4,
    /// <summary>"You are facing the wrong way!" or "Target needs to be in front of you."</summary>
    Facing = 5,
    /// <summary>"Can't do that while moving".</summary>
    Moving = 6,
    /// <summary>"Spell is not ready yet." and the like.</summary>
    NotReady = 7,
    /// <summary>"Another action is in progress".</summary>
    InProgress = 8,
    /// <summary>Stunned, pacified or interrupted.</summary>
    Prevented = 9,
    /// <summary>The target is dead or cannot be attacked.</summary>
    BadTarget = 10,
    /// <summary>"Inventory is full.": the bags have no room for the loot.</summary>
    InventoryFull = 11,
    /// <summary>The body is not the player's to loot: another's kill, already looted, or being looted.</summary>
    LootRefused = 12,
    /// <summary>"Requires Skinning 40" or "Skill not high enough".</summary>
    SkillTooLow = 13,
}

/// <summary>The last game error message of a kind the runtime reacts to, latched with a sequence.</summary>
public readonly record struct VisualErrorEvent(int Sequence, VisualGameError Error)
{
    /// <summary>A message that arrived after <paramref name="before"/> was read.</summary>
    public bool IsNewSince(VisualErrorEvent before) => Sequence > 0 && Sequence != before.Sequence;
}

/// <summary>Decodes the add-on's error latch (ForeverVisual.lua), beside the loot latch.</summary>
public static class VisualErrorReader
{
    // Frame row 225 on the probe grid, three cells from cell 26.
    internal const int Row = 162;
    internal const int FirstCell = 26;
    internal const int Seed = 0xA5E311;

    /// <summary>The last error message, or null when the latch is unreadable.</summary>
    internal static VisualErrorEvent? Read(in VisualBarReader.Probe probe)
    {
        int sequence = probe.Cell(Row, FirstCell);
        int code = probe.Cell(Row, FirstCell + 1);
        int marker = probe.Cell(Row, FirstCell + 2);
        if (sequence is < 0 or > 65535 || code is < 0 or > (int)VisualGameError.SkillTooLow ||
            (sequence == 0) != (code == 0) ||
            marker != ((Seed - sequence - code) & 0xFFFFFF))
            return null;
        return new(sequence, (VisualGameError)code);
    }
}
