using System;

namespace Core;

/// <summary>Each main-bar slot's range to one plate's unit, in the action-state codes.</summary>
public readonly record struct VisualSlotRanges(int Packed)
{
    /// <summary>In or out of range; null when unknown or the slot holds nothing.</summary>
    public bool? InRange(int slot) => slot is >= 1 and <= VisualCooldownGridSnapshot.SlotCount
        ? ((Packed >> ((slot - 1) * 2)) & 3) switch { 1 => true, 2 => false, _ => null }
        : null;
}

/// <summary>
/// One roster slot: a unit the client draws a nameplate for, read without
/// selecting it. A flag the client kept from the add-on is null, never false.
/// </summary>
/// <param name="Incarnation">Changes whenever the slot takes a new unit; 0 for an empty slot.</param>
/// <param name="GuidHash1">With <paramref name="GuidHash2"/>, the target identity hashes; both 0 while the GUID is protected.</param>
/// <param name="Level">0 when unreadable.</param>
/// <param name="OnThreatTable">The unit has the player on its threat table, that is, it fights the player.</param>
/// <param name="ThreatStatus">The player's threat status on the unit, 0 to 3, when on its table.</param>
/// <param name="Alpha">The plate's alpha, which the client fades with distance and occlusion.</param>
public readonly record struct VisualNameplate(
    int Slot, int Incarnation, int GuidHash1, int GuidHash2, int Level,
    bool? Attackable, bool? Dead, bool? IsPlayer, bool? TapDenied, bool? InCombat,
    bool? IsTarget, bool? IsMouseover, bool? BehindCamera, bool? TargetsPlayer,
    int? Reaction, bool? OnThreatTable, int? ThreatStatus, double? Alpha,
    VisualSlotRanges Ranges)
{
    /// <summary>
    /// Below this alpha the client faded the plate by its occluded multiplier
    /// (0.4 by default): unoccluded plates fade with distance only down to
    /// <c>nameplateMinAlpha</c>, 0.6 by default.
    /// </summary>
    public const double OccludedAlpha = 0.5;

    public bool IsEmpty => Incarnation == 0;

    public bool HasIdentity => GuidHash1 != 0 || GuidHash2 != 0;

    /// <summary>Where this frame shows the plate's marker; null when it was not found.</summary>
    public VisualScreenPointSample? Screen { get; init; }

    /// <summary>
    /// The camera cannot see the unit, read from the plate's fade. Unknown for
    /// the target and in combat, whose plates the client keeps opaque.
    /// </summary>
    public bool? Occluded => IsTarget == false && InCombat == false && Alpha is { } alpha
        ? alpha < OccludedAlpha : null;

    /// <summary>The unit fights the player: the player is on its threat table or its target.</summary>
    public bool Attacks => OnThreatTable == true || TargetsPlayer == true;

    /// <summary>The same unit as <paramref name="identity"/>, by GUID hashes.</summary>
    public bool Is(VisualTargetIdentity identity) =>
        HasIdentity && identity.Present &&
        GuidHash1 == identity.GuidHash1 && GuidHash2 == identity.GuidHash2;
}

/// <summary>
/// The units the client draws a nameplate for, in up to <see cref="SlotCount"/>
/// slots, with the plate counts and the settings that decide which plates
/// exist. An unreadable row is a null slot; an empty slot is explicit.
/// </summary>
public sealed class VisualNameplateRoster
{
    public const int SlotCount = 8;

    private readonly VisualNameplate?[] slots;

    /// <summary>Every plate the client shows, including those without a slot.</summary>
    public int PlateCount { get; }
    public int AttackableCount { get; }
    public int FriendlyPlayerCount { get; }
    public bool? ShowEnemies { get; }
    public bool? ShowAll { get; }
    /// <summary><c>nameplateMaxDistance</c> in whole yards; 0 when unreadable.</summary>
    public int MaxDistance { get; }
    public int ReadableCount { get; }

    /// <summary>The slot holding the target, if any.</summary>
    public VisualNameplate? Target { get; }

    /// <summary>Enemy plates exist for units that are neither targeted nor fighting.</summary>
    public bool PlatesShown => ShowEnemies == true && ShowAll == true;

    public VisualNameplateRoster(VisualNameplate?[] slots, int plateCount, int attackableCount,
        int friendlyPlayerCount, bool? showEnemies, bool? showAll, int maxDistance)
    {
        if (slots.Length != SlotCount)
            throw new ArgumentException($"A roster has {SlotCount} slots", nameof(slots));
        this.slots = slots;
        PlateCount = plateCount;
        AttackableCount = attackableCount;
        FriendlyPlayerCount = friendlyPlayerCount;
        ShowEnemies = showEnemies;
        ShowAll = showAll;
        MaxDistance = maxDistance;
        foreach (VisualNameplate? slot in slots)
        {
            if (slot is not { } plate)
                continue;
            ReadableCount++;
            if (plate is { IsEmpty: false, IsTarget: true })
                Target = plate;
        }
    }

    public VisualNameplate? GetSlot(int slot) =>
        slot is >= 1 and <= SlotCount ? slots[slot - 1] : null;

    /// <summary>A slot may show a marker: it holds a unit that is not behind the camera.</summary>
    public bool MayShowMarkers
    {
        get
        {
            foreach (VisualNameplate? slot in slots)
            {
                if (slot is { IsEmpty: false, BehindCamera: not true })
                    return true;
            }
            return false;
        }
    }

    /// <summary>This roster with each slot's marker position from the same frame.</summary>
    public VisualNameplateRoster WithScreens(ReadOnlySpan<VisualScreenPointSample?> screens)
    {
        VisualNameplate?[] located = new VisualNameplate?[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            located[i] = slots[i] is { IsEmpty: false, BehindCamera: not true } plate &&
                i < screens.Length
                    ? plate with { Screen = screens[i] }
                    : slots[i];
        }
        return new(located, PlateCount, AttackableCount, FriendlyPlayerCount,
            ShowEnemies, ShowAll, MaxDistance);
    }
}

/// <summary>
/// Decodes the add-on's nameplate roster (ForeverNameplates.lua): a header,
/// then one checksummed row of seven values per slot beside the event latches.
/// </summary>
public static class VisualNameplateReader
{
    /// <summary>The roster layout this reader accepts; the add-on writes it in the header.</summary>
    public const int ProtocolVersion = 1;

    // Probe rows below the white row, and the first value cell of each row.
    internal const int HeaderRow = 182;
    internal const int FirstSlotRow = 197;
    internal const int SlotRowStep = 15;
    internal const int FirstCell = 17;
    internal const int HeaderSeed = 0xA5C211;
    internal const int SlotSeed = 0xA5C311;

    /// <summary>
    /// The roster, or null when its header is unreadable or from another
    /// layout. A row claiming the target is checked against
    /// <paramref name="target"/>, the identity read from the same frame.
    /// </summary>
    internal static VisualNameplateRoster? Read(in VisualBarReader.Probe probe,
        VisualTargetIdentity? target)
    {
        int counts = probe.Cell(HeaderRow, FirstCell);
        int settings = probe.Cell(HeaderRow, FirstCell + 1);
        int version = probe.Cell(HeaderRow, FirstCell + 2);
        int marker = probe.Cell(HeaderRow, FirstCell + 3);
        if (counts < 0 || settings < 0 || version != ProtocolVersion ||
            marker != ((HeaderSeed - counts - settings - version) & 0xFFFFFF) ||
            (settings & 3) == 3 || ((settings >> 2) & 3) == 3)
            return null;

        VisualNameplate?[] slots = new VisualNameplate?[VisualNameplateRoster.SlotCount];
        for (int slot = 1; slot <= slots.Length; slot++)
            slots[slot - 1] = ReadSlot(probe, slot, target);
        return new(slots, counts & 255, (counts >> 8) & 255, (counts >> 16) & 255,
            Tri(settings & 3), Tri((settings >> 2) & 3), (settings >> 4) & 255);
    }

    private static VisualNameplate? ReadSlot(in VisualBarReader.Probe probe, int slot,
        VisualTargetIdentity? target)
    {
        double row = FirstSlotRow + (slot - 1) * SlotRowStep;
        Span<int> v = stackalloc int[7];
        int sum = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = probe.Cell(row, FirstCell + i);
            if (v[i] < 0)
                return null;
            if (i < 6)
                sum += v[i];
        }

        int flags = v[3];
        int isTarget = (flags >> 18) & 3;
        int checksum = SlotSeed - slot - sum;
        if (isTarget == 2)
        {
            if (target is not { Present: true } checkedTarget)
                return null;
            checksum -= checkedTarget.GuidHash1 + checkedTarget.GuidHash2;
        }
        if (v[6] != (checksum & 0xFFFFFF))
            return null;

        if (v[0] == 0)
        {
            return v[1] == 0 && v[2] == 0 && v[3] == 0 && v[4] == 0 && v[5] == 0
                ? Empty(slot) : null;
        }

        int more = v[4];
        int reaction = (more >> 2) & 15;
        int threat = (more >> 6) & 7;
        int alpha = (more >> 9) & 255;
        if (v[0] > 65535 || reaction > 8 || threat > 5 || (more >> 17) != 0 ||
            !ValidTris(flags >> 8, 8) || !ValidTris(more, 1) || !ValidTris(v[5], 12))
            return null;

        return new(slot, v[0], v[1], v[2], flags & 255,
            Attackable: Tri(flags >> 8), Dead: Tri(flags >> 10), IsPlayer: Tri(flags >> 12),
            TapDenied: Tri(flags >> 14), InCombat: Tri(flags >> 16), IsTarget: Tri(flags >> 18),
            IsMouseover: Tri(flags >> 20), BehindCamera: Tri(flags >> 22),
            TargetsPlayer: Tri(more),
            Reaction: reaction == 0 ? null : reaction,
            OnThreatTable: threat == 0 ? null : threat > 1,
            ThreatStatus: threat > 1 ? threat - 2 : null,
            Alpha: alpha == 0 ? null : (alpha - 1) / 254.0,
            Ranges: new(v[5]));
    }

    private static VisualNameplate Empty(int slot) => new(slot, 0, 0, 0, 0,
        null, null, null, null, null, null, null, null, null, null, null, null, null, default);

    // Two bits per value: 0 unknown, 1 false, 2 true; 3 is never written.
    private static bool? Tri(int bits) => (bits & 3) switch { 1 => false, 2 => true, _ => null };

    private static bool ValidTris(int packed, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (((packed >> (i * 2)) & 3) == 3)
                return false;
        }
        return true;
    }
}
