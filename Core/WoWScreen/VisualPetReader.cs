namespace Core;

/// <summary>
/// The player's pet from one capture: whether it is there, dead and
/// fighting, and the unit it attacks.
/// </summary>
/// <param name="TargetHash1">With <paramref name="TargetHash2"/>, the identity
/// hashes of the unit the pet attacks, as the target identity carries them;
/// both 0 with none.</param>
public readonly record struct VisualPet(bool? Exists, bool? Dead, bool? InCombat,
    int TargetHash1, int TargetHash2)
{
    /// <summary>A pet that is there and alive.</summary>
    public bool Active => Exists == true && Dead == false;

    /// <summary>The pet attacks <paramref name="identity"/>.</summary>
    public bool Attacks(VisualTargetIdentity identity) =>
        identity.Present && (TargetHash1 != 0 || TargetHash2 != 0) &&
        TargetHash1 == identity.GuidHash1 && TargetHash2 == identity.GuidHash2;
}

/// <summary>Decodes the add-on's pet block (ForeverVisual.lua), beside the roster header.</summary>
public static class VisualPetReader
{
    /// <summary>The layout this reader accepts; the add-on writes it in the flags cell.</summary>
    public const int ProtocolVersion = 1;

    // Frame row 245 on the probe grid, four cells from cell 22.
    internal const int Row = 182;
    internal const int FirstCell = 22;
    internal const int Seed = 0xA5E411;

    /// <summary>The pet, or null when the block is unreadable or from another layout.</summary>
    internal static VisualPet? Read(in VisualBarReader.Probe probe)
    {
        int flags = probe.Cell(Row, FirstCell);
        int hash1 = probe.Cell(Row, FirstCell + 1);
        int hash2 = probe.Cell(Row, FirstCell + 2);
        int marker = probe.Cell(Row, FirstCell + 3);
        if (flags < 0 || hash1 < 0 || hash2 < 0 || marker < 0 ||
            (flags >> 6) != ProtocolVersion ||
            marker != ((Seed - flags - hash1 - hash2) & 0xFFFFFF))
            return null;

        int exists = flags & 3, dead = (flags >> 2) & 3, combat = (flags >> 4) & 3;
        // Without a pet the add-on writes nothing else.
        if (exists == 3 || dead == 3 || combat == 3 ||
            exists != 2 && (dead != 0 || combat != 0 || hash1 != 0 || hash2 != 0))
            return null;
        return new(Tri(exists), Tri(dead), Tri(combat), hash1, hash2);
    }

    // Two bits per value: 0 unknown, 1 false, 2 true.
    private static bool? Tri(int bits) => bits switch { 1 => false, 2 => true, _ => null };
}
