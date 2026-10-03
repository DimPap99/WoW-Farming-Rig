namespace Core;

/// <summary>
/// The unit the client's Interact Key would act on now (the soft-interact
/// target), and the two client settings looting relies on, from one capture.
/// </summary>
/// <param name="GuidHash1">With <paramref name="GuidHash2"/>, the unit's
/// identity hashes as the target identity carries them; both 0 with none.</param>
/// <param name="Dead">The unit is dead; null when unknown or no unit.</param>
/// <param name="Lootable">The unit has loot the player may take
/// (<c>CanLootUnit</c>); null when unknown or no unit.</param>
/// <param name="SoftTargetInteract">The <c>SoftTargetInteract</c> setting:
/// 0 off, 1 gamepad, 2 keyboard and mouse, 3 always; null when unreadable.</param>
/// <param name="AutoLoot">The client's Auto Loot setting; null when unreadable.</param>
public readonly record struct VisualSoftInteract(int GuidHash1, int GuidHash2,
    bool? Dead, bool? Lootable, int? SoftTargetInteract, bool? AutoLoot)
{
    public bool Present => GuidHash1 != 0 || GuidHash2 != 0;

    /// <summary>The Interact Key acts on the soft target for keyboard and mouse too.</summary>
    public bool InteractKeyEnabled => SoftTargetInteract == 3;

    /// <summary>A body the Interact Key would loot: dead, with loot the player may take.</summary>
    public bool IsLootableCorpse => Present && Dead == true && Lootable == true;
}

/// <summary>Decodes the add-on's soft-interact block (ForeverVisual.lua), beside the death latch.</summary>
public static class VisualInteractReader
{
    /// <summary>The layout this reader accepts; the add-on writes it in the settings cell.</summary>
    public const int ProtocolVersion = 1;

    // Frame row 205 on the probe grid, five cells from cell 22.
    internal const int Row = 142;
    internal const int FirstCell = 22;
    internal const int Seed = 0xA5E211;

    /// <summary>The soft-interact state, or null when the block is unreadable or from another layout.</summary>
    internal static VisualSoftInteract? Read(in VisualBarReader.Probe probe)
    {
        int hash1 = probe.Cell(Row, FirstCell);
        int hash2 = probe.Cell(Row, FirstCell + 1);
        int unit = probe.Cell(Row, FirstCell + 2);
        int settings = probe.Cell(Row, FirstCell + 3);
        int marker = probe.Cell(Row, FirstCell + 4);
        if (hash1 < 0 || hash2 < 0 || unit < 0 || settings < 0 || marker < 0 ||
            (settings >> 6) != ProtocolVersion ||
            marker != ((Seed - hash1 - hash2 - unit - settings) & 0xFFFFFF))
            return null;

        int dead = unit & 3, lootable = (unit >> 2) & 3, level = settings & 7, autoLoot = (settings >> 3) & 3;
        bool present = hash1 != 0 || hash2 != 0;
        if ((unit >> 4) != 0 || dead == 3 || lootable == 3 || level > 4 || autoLoot == 3 ||
            !present && unit != 0)
            return null;
        return new(hash1, hash2, Tri(dead), Tri(lootable), level == 0 ? null : level - 1, Tri(autoLoot));
    }

    // Two bits per value: 0 unknown, 1 false, 2 true.
    private static bool? Tri(int bits) => bits switch { 1 => false, 2 => true, _ => null };
}
