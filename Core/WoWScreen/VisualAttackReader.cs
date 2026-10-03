namespace Core;

/// <summary>
/// What the player's attacks are doing, and how close the target is, from one
/// capture.
/// </summary>
/// <param name="CurrentSlots">Bit <c>n - 1</c> is main-bar slot <c>n</c> being
/// current: cast, toggled on, or pressed and waiting for the next swing, as a
/// next-swing ability is until the swing lands.</param>
/// <param name="AutoRepeatSlots">Bit <c>n - 1</c> is main-bar slot <c>n</c>
/// repeating on its own, as Auto Shot does.</param>
/// <param name="Melee">Melee auto attack is on; null when unknown.</param>
/// <param name="TargetClose">The target is within the client's duel distance
/// (<c>CheckInteractDistance</c> 3). On this client that turns true as the
/// target crosses a ranged weapon's 8-yard minimum range. Null without a
/// checked target.</param>
public readonly record struct VisualAttackState(int CurrentSlots, int AutoRepeatSlots,
    bool? Melee, bool? TargetClose)
{
    /// <summary>Main-bar slot <paramref name="slot"/> is current.</summary>
    public bool IsCurrent(int slot) => IsSet(CurrentSlots, slot);

    /// <summary>Main-bar slot <paramref name="slot"/> repeats on its own.</summary>
    public bool IsAutoRepeating(int slot) => IsSet(AutoRepeatSlots, slot);

    /// <summary>A main-bar action repeats on its own: a ranged auto attack runs.</summary>
    public bool AutoRepeat => AutoRepeatSlots != 0;

    private static bool IsSet(int bits, int slot) =>
        slot is >= 1 and <= VisualCooldownGridSnapshot.SlotCount && ((bits >> (slot - 1)) & 1) != 0;
}

/// <summary>Decodes the add-on's attack-state block (ForeverVisual.lua) on the target probe row.</summary>
public static class VisualAttackReader
{
    /// <summary>The layout this reader accepts; the add-on writes it in the state cell.</summary>
    public const int ProtocolVersion = 1;

    internal const int FirstCell = 17;
    internal const int Seed = 0xA5E111;

    /// <summary>
    /// The attack state, or null when the block is unreadable or from another
    /// layout. A known target distance is checked against
    /// <paramref name="target"/>, the identity read from the same frame.
    /// </summary>
    internal static VisualAttackState? Read(in VisualBarReader.Probe probe, VisualTargetIdentity? target)
    {
        int slots = probe.Cell(VisualBarReader.TargetRow, FirstCell);
        int state = probe.Cell(VisualBarReader.TargetRow, FirstCell + 1);
        int marker = probe.Cell(VisualBarReader.TargetRow, FirstCell + 2);
        if (slots < 0 || state < 0 || marker < 0 || (state >> 4) != ProtocolVersion)
            return null;

        int melee = state & 3, close = (state >> 2) & 3;
        if (melee == 3 || close == 3)
            return null;
        int checksum = Seed - slots - state;
        if (close != 0)
        {
            if (target is not { Present: true } checkedTarget)
                return null;
            checksum -= checkedTarget.GuidHash1 + checkedTarget.GuidHash2;
        }
        if (marker != (checksum & 0xFFFFFF))
            return null;

        return new(slots & 0xFFF, (slots >> 12) & 0xFFF, Tri(melee), Tri(close));
    }

    // Two bits per value: 0 unknown, 1 false, 2 true.
    private static bool? Tri(int bits) => bits switch { 1 => false, 2 => true, _ => null };
}
