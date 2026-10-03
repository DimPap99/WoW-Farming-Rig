using SharedLib;

using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Numerics;

namespace Core;

public readonly record struct VisualPositionSample(
    int UIMapId,
    double MapX,
    double MapY,
    double Direction)
{
    /// <inheritdoc cref="VisualMapPointSample.ToWorld(WorldMapArea)"/>
    public Vector3 ToWorld(WorldMapArea area) =>
        new VisualMapPointSample(UIMapId, MapX, MapY).ToWorld(area);
}

public readonly record struct VisualWorldPositionSample(
    double X,
    double Y,
    double Z,
    int InstanceMapId);

public readonly record struct VisualMapPointSample(
    int UIMapId, double MapX, double MapY)
{
    /// <summary>World X/Y of this normalized map point. Z is 0: the map has no height.</summary>
    public Vector3 ToWorld(WorldMapArea area) =>
        WorldMapAreaDB.ToWorld_FlipXY(
            new Vector3((float)(MapX * 100), (float)(MapY * 100), 0), area);
}

public readonly record struct VisualScreenPointSample(double X, double Y);

public readonly record struct VisualPlayerIdentity(
    int ClassId, int RaceId, int Level);

public readonly record struct VisualTargetFlags(
    bool Present, bool Attackable, bool Dead,
    bool PlayerCombat, bool PlayerDead);

public readonly record struct VisualTargetIdentity(
    int GuidHash1, int GuidHash2, int Level, bool IsPlayer)
{
    public bool Present => Level > 0;
}

public readonly record struct VisualCastEvent(int Sequence, int SpellId)
{
    public bool ConfirmsNewSuccess(VisualCastEvent before, int expectedSpellId) =>
        Sequence > 0 && Sequence != before.Sequence &&
        SpellId == expectedSpellId;
}

public enum VisualActionKind : byte { Empty, Spell, Item, Macro, Flyout }

public readonly record struct VisualActionSample(
    int Slot,
    VisualActionKind Kind,
    int Id);

public sealed class VisualActionBarSnapshot
{
    public const int SlotCount = 120;

    private readonly VisualActionSample?[] slots;

    public int ReadableCount { get; }
    public int OccupiedCount { get; }

    internal VisualActionBarSnapshot(VisualActionSample?[] slots)
    {
        this.slots = slots;
        foreach (VisualActionSample? action in slots)
        {
            if (action is not { } value)
                continue;

            ReadableCount++;
            if (value.Kind != VisualActionKind.Empty)
                OccupiedCount++;
        }
    }

    public VisualActionSample? GetSlot(int slot) => slot is >= 1 and <= SlotCount
        ? slots[slot - 1] : null;
}

public readonly record struct VisualActionState(
    int Slot, bool Usable, bool NoMana, bool? InRange);

public sealed class VisualActionStateSnapshot
{
    private readonly VisualActionState?[] states;
    public int ReadableCount { get; }

    internal VisualActionStateSnapshot(VisualActionState?[] states)
    {
        this.states = states;
        foreach (var state in states)
            if (state is not null)
                ReadableCount++;
    }

    public VisualActionState? GetSlot(int slot) =>
        slot is >= 1 and <= VisualActionBarSnapshot.SlotCount
            ? states[slot - 1] : null;
}

public sealed class VisualCooldownGridSnapshot
{
    public const int SlotCount = 12;
    private readonly double?[] fills;
    public int ReadableCount { get; }

    internal VisualCooldownGridSnapshot(double?[] fills)
    {
        this.fills = fills;
        foreach (double? fill in fills)
            if (fill is not null)
                ReadableCount++;
    }

    public double? GetSlot(int slot) => slot is >= 1 and <= SlotCount
        ? fills[slot - 1] : null;
}

public readonly record struct VisualBagHeader(
    int Bag, int SlotCount, int FreeSlots, int Family);

public readonly record struct VisualBagSlot(
    int Bag, int Slot, int ItemId, int Count);

public sealed class VisualBagSnapshot
{
    public const int BagCount = 5;
    public const int SlotsPerBag = 40;
    private readonly VisualBagHeader?[] headers;
    private readonly VisualBagSlot?[] slots;
    public int ReadableHeaders { get; }
    public int ReadableSlots { get; }
    public bool IsComplete => ReadableHeaders == BagCount &&
        ReadableSlots == BagCount * SlotsPerBag;

    internal VisualBagSnapshot(VisualBagHeader?[] headers,
        VisualBagSlot?[] slots)
    {
        this.headers = headers;
        this.slots = slots;
        foreach (var header in headers)
            if (header is not null) ReadableHeaders++;
        foreach (var slot in slots)
            if (slot is not null) ReadableSlots++;
    }

    public VisualBagHeader? GetBag(int bag) =>
        bag is >= 0 and < BagCount ? headers[bag] : null;

    public VisualBagSlot? GetSlot(int bag, int slot) =>
        bag is >= 0 and < BagCount && slot is >= 1 and <= SlotsPerBag
            ? slots[bag * SlotsPerBag + slot - 1] : null;
}

public enum VisualSpellIdSource : byte { SpellId = 1, ActionId = 2 }

public readonly record struct VisualSpellBookEntry(
    int Slot,
    VisualSpellIdSource Source,
    int Id);

public sealed class VisualSpellBookSnapshot
{
    public const int MaxSlots = 200;

    private readonly VisualSpellBookEntry?[] entries;

    public int Count => entries.Length;
    public int ReadableCount { get; }

    internal VisualSpellBookSnapshot(VisualSpellBookEntry?[] entries)
    {
        this.entries = entries;
        foreach (VisualSpellBookEntry? entry in entries)
            if (entry is not null)
                ReadableCount++;
    }

    public VisualSpellBookEntry? GetSlot(int slot) => slot is >= 1 and <= MaxSlots &&
        slot <= entries.Length ? entries[slot - 1] : null;
}

public sealed class VisualBindingSnapshot
{
    public const int Count = 62;
    private readonly int?[] codes;

    public int ReadableCount { get; }

    internal VisualBindingSnapshot(int?[] codes)
    {
        this.codes = codes;
        foreach (int? code in codes)
            if (code is not null)
                ReadableCount++;
    }

    public int? GetCode(int index) => index is >= 1 and <= Count
        ? codes[index - 1] : null;
}

public readonly record struct VisualBarSample(
    double PlayerHealth,
    double PlayerPower,
    double TargetHealth,
    double Calibration,
    int Left,
    int Bottom,
    int Width,
    VisualPositionSample? Position = null,
    bool? TargetPresent = null,
    VisualActionBarSnapshot? ActionBar = null,
    int? PlayerHealthMax = null,
    int? PlayerPowerMax = null,
    VisualWorldPositionSample? WorldPosition = null,
    VisualSpellBookSnapshot? SpellBook = null,
    VisualBindingSnapshot? Bindings = null,
    VisualActionStateSnapshot? ActionStates = null,
    VisualCooldownGridSnapshot? Cooldowns = null,
    int? PlayerPowerType = null,
    VisualBagSnapshot? Bags = null,
    VisualPlayerIdentity? PlayerIdentity = null,
    VisualTargetFlags? TargetFlags = null,
    VisualCastEvent? LastCastEvent = null,
    VisualTargetIdentity? TargetIdentity = null,
    VisualWorldPositionSample? TargetWorldPosition = null,
    VisualMapPointSample? TargetMapPosition = null,
    VisualScreenPointSample? TargetScreenPosition = null,
    VisualSlotAuraSnapshot? SlotAuras = null,
    VisualDeathEvent? LastDeathEvent = null,
    VisualLootState? Loot = null,
    VisualNameplateRoster? Nameplates = null,
    VisualAttackState? Attack = null)
{
    /// <summary>
    /// The screen frame this sample was decoded from, set by
    /// <see cref="ForeverVisualBarTracker"/>. It travels with the data so a
    /// reader can never pair a newer frame ID with an older frame's values.
    /// </summary>
    public long FrameId { get; init; }
}

/// <summary>
/// Reads the fixed-color rows drawn by /forevervisual. The full and
/// half rows establish pixel position and scale without relying on the game's
/// UI scale setting. Checked samples feed the guarded Forever controller.
/// </summary>
public static class VisualBarReader
{
    private enum BarColor { White, Yellow, Green, Blue, Red, Cyan }

    /// <summary>The white calibration row is searched for in this top-left window.</summary>
    public const int SearchWidth = 650;
    public const int SearchHeight = 250;

    /// <summary>The aura state value the add-on writes once the slot reference icons rendered.</summary>
    public const int SlotAuraState = 3;

    // Width of the white row at scale 1; every offset in this file uses these units.
    private const double ReferenceWidth = 320.0;

    // Furthest any telemetry read reaches right of the white row's left edge and
    // below the row, in reference units: the last aura slot reference (x 592)
    // and the bottom of the aura icon strip (y 666). Grow these with /forevervisual; a
    // read outside the copied region returns null rather than wrong data.
    private const int ReadExtentX = 600;
    private const int ReadExtentY = 680;

    /// <summary>
    /// Size of the top-left frame region that holds every telemetry read, so a
    /// caller can copy that instead of the whole frame. <paramref name="pixels"/>
    /// needs to cover only the search window. The nameplate markers are not
    /// included; they need the full viewport (see <see cref="LocateMarkers"/>).
    /// </summary>
    public static bool TryGetReadRegion(Bgra32[] pixels, int width, int height,
        int frameWidth, int frameHeight, out int regionWidth, out int regionHeight)
    {
        regionWidth = regionHeight = 0;
        if (pixels.Length != width * height ||
            !TryFindWhiteRow(pixels, width, height,
                out int whiteLeft, out int whiteY, out int whiteWidth))
            return false;

        double scale = whiteWidth / ReferenceWidth;
        regionWidth = Math.Min(frameWidth, Math.Max(SearchWidth,
            whiteLeft + (int)Math.Ceiling(ReadExtentX * scale) + 1));
        regionHeight = Math.Min(frameHeight, Math.Max(SearchHeight,
            whiteY + (int)Math.Ceiling(ReadExtentY * scale) + 1));
        return true;
    }

    /// <summary>True when a roster slot may show a marker, so the frame has to be searched.</summary>
    public static bool NeedsMarkerScan(VisualBarSample sample) =>
        sample.Nameplates is { MayShowMarkers: true };

    /// <summary>
    /// <paramref name="telemetry"/> with each roster slot's marker and the
    /// target's screen point, found in the full viewport frame it was read from.
    /// </summary>
    public static VisualBarSample LocateMarkers(Bgra32[] pixels, int width, int height,
        VisualBarSample telemetry)
    {
        if (telemetry.Nameplates is not { MayShowMarkers: true } roster)
            return telemetry;
        VisualNameplateRoster located = roster.WithScreens(VisualMarkerLocator.Locate(
            pixels, width, height, telemetry.Width / ReferenceWidth));
        return telemetry with
        {
            Nameplates = located,
            TargetScreenPosition = located.Target?.Screen
        };
    }

    /// <param name="locateMarkers">Search the whole frame for the nameplate
    /// markers. Pass false when <paramref name="pixels"/> is only the read region.</param>
    public static bool TryRead(Bgra32[] pixels, int width, int height, out VisualBarSample sample,
        bool locateMarkers = true)
    {
        sample = default;
        if (pixels.Length != width * height || width < 200 || height < 100)
            return false;

        if (!TryFindWhiteRow(pixels, width, height,
                out int whiteLeft, out int whiteY, out int whiteWidth))
            return false;

        double scale = whiteWidth / ReferenceWidth;
        int rowStep = (int)Math.Round(17 * scale);
        int tolerance = Math.Max(2, (int)Math.Round(3 * scale));

        double half = RowFill(pixels, width, height, whiteLeft,
            whiteY - rowStep, whiteWidth, tolerance, BarColor.Yellow);
        if (half is < 0.45 or > 0.55)
            return false;

        double target = RowFill(pixels, width, height, whiteLeft,
            whiteY - rowStep * 2, whiteWidth, tolerance, BarColor.Red);
        double power = RowFill(pixels, width, height, whiteLeft,
            whiteY - rowStep * 3, whiteWidth, tolerance, BarColor.Blue);
        double health = RowFill(pixels, width, height, whiteLeft,
            whiteY - rowStep * 4, whiteWidth, tolerance, BarColor.Green);

        Probe probe = new(pixels, width, height, whiteLeft, whiteY, scale);
        VisualPositionSample? position = TryReadPosition(probe);
        bool? targetPresent = TryReadTargetPresence(probe);
        VisualActionBarSnapshot? actionBar = TryReadActionBar(probe);
        VisualActionStateSnapshot? actionStates = TryReadActionStates(probe, actionBar);
        VisualCooldownGridSnapshot? cooldowns = TryReadCooldownGrid(probe, actionBar);
        int? playerHealthMax = TryReadMaximum(probe, 11, 12, 0xA55A11);
        int? playerPowerMax = TryReadMaximum(probe, 13, 14, 0xA55A22);
        VisualWorldPositionSample? worldPosition = TryReadWorldPosition(probe);
        VisualSpellBookSnapshot? spellBook = TryReadSpellBook(probe);
        VisualBindingSnapshot? bindings = TryReadBindings(probe);
        int? playerPowerType = TryReadPowerType(probe);
        VisualBagSnapshot? bags = TryReadBags(probe);
        VisualPlayerIdentity? playerIdentity = TryReadPlayerIdentity(probe);
        VisualTargetFlags? targetFlags = TryReadTargetFlags(probe);
        VisualCastEvent? castEvent = TryReadCastEvent(probe);
        VisualTargetIdentity? targetIdentity = TryReadTargetIdentity(probe);
        if (targetFlags is { } checkedFlags &&
            targetIdentity is { } checkedIdentity &&
            checkedFlags.Present != checkedIdentity.Present)
            targetIdentity = null;
        VisualWorldPositionSample? targetWorldPosition =
            targetIdentity is { Present: true } targetId
                ? TryReadTargetWorldPosition(probe, targetId)
                : null;
        VisualMapPointSample? targetMapPosition =
            targetIdentity is { Present: true } mapTargetId
                ? TryReadTargetMapPosition(probe, mapTargetId)
                : null;
        VisualSlotAuraSnapshot? slotAuras = TryReadSlotAuras(probe);
        sample = new(health, power, target, half, whiteLeft, whiteY, whiteWidth,
            position, targetPresent, actionBar,
            playerHealthMax, playerPowerMax, worldPosition, spellBook, bindings,
            actionStates, cooldowns, playerPowerType, bags, playerIdentity,
            targetFlags, castEvent, targetIdentity, targetWorldPosition,
            targetMapPosition, TargetScreenPosition: null, slotAuras,
            VisualLifecycleReader.ReadDeath(pixels, width, height, whiteLeft, whiteY, scale),
            VisualLifecycleReader.ReadLoot(pixels, width, height, whiteLeft, whiteY, scale),
            VisualNameplateReader.Read(probe, targetIdentity),
            VisualAttackReader.Read(probe, targetIdentity));
        if (locateMarkers)
            sample = LocateMarkers(pixels, width, height, sample);
        return true;
    }

    private static bool TryFindWhiteRow(Bgra32[] pixels, int width, int height,
        out int whiteLeft, out int whiteY, out int whiteWidth)
    {
        int searchWidth = Math.Min(width, SearchWidth);
        int searchHeight = Math.Min(height, SearchHeight);
        whiteLeft = -1;
        whiteY = -1;
        whiteWidth = 0;

        for (int y = 20; y < searchHeight; y++)
        {
            int x = 0;
            while (x < searchWidth)
            {
                if (!Matches(pixels[y * width + x], BarColor.White))
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < searchWidth && Matches(pixels[y * width + x], BarColor.White))
                    x++;

                int length = x - start;
                if (length > whiteWidth)
                {
                    whiteLeft = start;
                    whiteY = y;
                    whiteWidth = length;
                }
            }
        }

        return whiteWidth >= 100 && whiteLeft >= 0;
    }

    // Probe layout below the white row, in reference units. Value cells on a
    // probe row sit 20 units apart from x 5; grids have 20 columns of 16 units
    // and 8-unit rows. Each block carries its own checksum or marker pixel.
    private const int VitalsRow = 27;          // map position, maxima, grid markers
    private const int WorldRow = 42;           // world position, counts, identity, flags
    private const int CooldownMarkerRow = 67;
    private const int ActionGridTop = 76;
    internal const int TargetRow = 127;        // cast event, the checked target probes, attack state
    private const int SpellGridTop = 141;
    private const int BindingGridTop = 236;
    private const int ActionStateGridTop = 276;
    private const int CooldownBarTop = 335;
    private const int BagHeaderRow = 437;
    private const int BagGridTop = 456;
    private const int AuraStripTop = 632;

    /// <summary>The calibrated telemetry area of one frame.</summary>
    internal readonly struct Probe(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale)
    {
        public Bgra32[] Pixels => pixels;
        public int Width => width;
        public int Height => height;
        public int Left => left;
        public int WhiteY => whiteY;
        public double Scale => scale;

        public int Units(double units) => (int)Math.Round(units * scale);
        public int X(double units) => left + Units(units);
        public int Y(double units) => whiteY + Units(units);

        public bool Contains(int x, int y) =>
            x >= 0 && x < width && y >= 0 && y < height;

        public Bgra32 Pixel(int x, int y) => pixels[y * width + x];

        /// <summary>
        /// The pixel's 24-bit RGB value, or -1 outside the frame. A checksum is
        /// masked to 24 bits, so a read that left the frame never validates.
        /// </summary>
        public int Value(int x, int y)
        {
            if (!Contains(x, y))
                return -1;
            Bgra32 pixel = Pixel(x, y);
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        /// <summary>Value cell <paramref name="index"/> of a probe row.</summary>
        public int Cell(double row, int index) => Value(X(5 + index * 20), Y(row));

        /// <summary>One part of grid entry <paramref name="ordinal"/>, counted from 1.</summary>
        public (int X, int Y) GridPoint(double top, int ordinal, double partX)
        {
            int column = (ordinal - 1) % 20;
            int row = (ordinal - 1) / 20;
            return (X(partX + column * 16), Y(top + row * 8));
        }

        public int Grid(double top, int ordinal, double partX)
        {
            (int x, int y) = GridPoint(top, ordinal, partX);
            return Value(x, y);
        }
    }

    // A world-row value followed by its marker, which holds (seed - value).
    private static int? TryReadCheckedWorldPixel(in Probe probe, int index, int markerSeed)
    {
        int value = probe.Cell(WorldRow, index);
        int marker = probe.Cell(WorldRow, index + 1);
        return value >= 0 && marker == ((markerSeed - value) & 0xFFFFFF) ? value : null;
    }

    private static VisualPlayerIdentity? TryReadPlayerIdentity(in Probe probe)
    {
        int? packed = TryReadCheckedWorldPixel(probe, 11, 0xA57744);
        if (packed is not { } value)
            return null;
        int classId = value % 64;
        int raceId = value / 64 % 256;
        int level = value / 16384;
        return classId is >= 1 and <= 63 && raceId is >= 1 and <= 255 &&
            level is >= 1 and <= 1023
            ? new(classId, raceId, level) : null;
    }

    private static VisualTargetFlags? TryReadTargetFlags(in Probe probe)
    {
        int? flags = TryReadCheckedWorldPixel(probe, 13, 0xA57755);
        if (flags is not int value || value is < 32 or > 63)
            return null;
        value -= 32;
        bool present = (value & 1) != 0;
        bool attackable = (value & 2) != 0;
        bool dead = (value & 4) != 0;
        bool playerCombat = (value & 8) != 0;
        bool playerDead = (value & 16) != 0;
        return !present && (attackable || dead)
            ? null : new(present, attackable, dead,
                playerCombat, playerDead);
    }

    private static VisualCastEvent? TryReadCastEvent(in Probe probe)
    {
        int sequence = probe.Cell(TargetRow, 0);
        int spellId = probe.Cell(TargetRow, 1);
        int marker = probe.Cell(TargetRow, 2);
        if (sequence is < 0 or > 65535 || spellId < 0 || marker < 0 ||
            (sequence == 0) != (spellId == 0) ||
            marker != ((0xA5CC11 - sequence - spellId) & 0xFFFFFF))
            return null;
        return new(sequence, spellId);
    }

    private static VisualTargetIdentity? TryReadTargetIdentity(in Probe probe)
    {
        int hash1 = probe.Cell(TargetRow, 3);
        int hash2 = probe.Cell(TargetRow, 4);
        int packedLevel = probe.Cell(TargetRow, 5);
        int marker = probe.Cell(TargetRow, 6);
        if (hash1 < 0 || hash2 < 0 || packedLevel < 0 || marker < 0 ||
            marker != ((0xA58866 - hash1 - hash2 - packedLevel) & 0xFFFFFF))
            return null;

        if (hash1 == 0 && hash2 == 0 && packedLevel == 0)
            return new(0, 0, 0, false);

        int level = packedLevel / 2;
        return level is >= 1 and <= 255 && (hash1 != 0 || hash2 != 0)
            ? new(hash1, hash2, level, (packedLevel & 1) != 0)
            : null;
    }

    // The target probes below are checksummed together with the identity
    // hashes, so they can only describe the identity read from the same frame.
    private static VisualWorldPositionSample? TryReadTargetWorldPosition(
        in Probe probe, VisualTargetIdentity identity)
    {
        int x = probe.Cell(TargetRow, 7), y = probe.Cell(TargetRow, 8), z = probe.Cell(TargetRow, 9);
        int instanceMapId = probe.Cell(TargetRow, 10);
        if (x < 0 || y < 0 || z < 0 || instanceMapId < 0 ||
            probe.Cell(TargetRow, 11) != ((0xA59977 - x - y - z - instanceMapId -
                identity.GuidHash1 - identity.GuidHash2) & 0xFFFFFF))
            return null;

        return new((x - 8388608) / 100.0, (y - 8388608) / 100.0,
            (z - 8388608) / 100.0, instanceMapId);
    }

    private static VisualMapPointSample? TryReadTargetMapPosition(
        in Probe probe, VisualTargetIdentity identity)
    {
        int mapId = probe.Cell(TargetRow, 12), x = probe.Cell(TargetRow, 13), y = probe.Cell(TargetRow, 14);
        if (mapId <= 0 || x < 0 || y < 0 ||
            probe.Cell(TargetRow, 15) != ((0xA5AA88 - mapId - x - y -
                identity.GuidHash1 - identity.GuidHash2) & 0xFFFFFF))
            return null;
        return new(mapId, x / 16777215.0, y / 16777215.0);
    }

    // The add-on writes the state only after the aura icons and every slot
    // reference rendered; a failed aura enumeration in combat leaves it black.
    // State 2 was the Seal-only layout and is refused.
    private static VisualSlotAuraSnapshot? TryReadSlotAuras(in Probe probe)
    {
        int state = probe.Cell(TargetRow, 28);
        int marker = probe.Cell(TargetRow, 29);
        return state == SlotAuraState &&
            marker == ((0xA5B5A9 - state) & 0xFFFFFF)
            ? VisualAuraReader.Read(probe.Pixels, probe.Width, probe.Height,
                probe.Left, probe.Y(AuraStripTop), probe.Scale)
            : null;
    }

    private static VisualCooldownGridSnapshot? TryReadCooldownGrid(
        in Probe probe, VisualActionBarSnapshot? actionBar)
    {
        if (actionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount })
            return null;

        double?[] fills = new double?[VisualCooldownGridSnapshot.SlotCount];
        int barWidth = probe.Units(ReferenceWidth);
        for (int slot = 1; slot <= fills.Length; slot++)
        {
            int x = probe.X(5 + (slot - 1) * 20);
            int y = probe.Y(CooldownMarkerRow);
            if (!probe.Contains(x, y))
                return null;
            int code = probe.Value(x, y);
            VisualActionSample action = actionBar.GetSlot(slot)!.Value;
            if (code != ((0xA59911 - slot - (int)action.Kind - action.Id) &
                0xFFFFFF))
                continue;
            if (action.Kind == VisualActionKind.Empty)
            {
                fills[slot - 1] = 0;
                continue;
            }

            int centerY = probe.Y(CooldownBarTop + (slot - 1) * 8);
            if (centerY < 0 || centerY >= probe.Height ||
                probe.Left + barWidth > probe.Width)
                return null;
            fills[slot - 1] = RowFill(probe.Pixels, probe.Width, probe.Height,
                probe.Left, centerY, barWidth,
                Math.Max(1, probe.Units(2)), BarColor.Cyan);
        }

        return new(fills);
    }

    private static VisualActionStateSnapshot? TryReadActionStates(
        in Probe probe, VisualActionBarSnapshot? actionBar)
    {
        if (actionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount } ||
            probe.Cell(VitalsRow, 15) != 0x5A88A5)
            return null;

        VisualActionState?[] states =
            new VisualActionState?[VisualActionBarSnapshot.SlotCount];
        for (int slot = 1; slot <= states.Length; slot++)
        {
            int code = probe.Grid(ActionStateGridTop, slot, 4);
            int marker = probe.Grid(ActionStateGridTop, slot, 12);
            VisualActionSample action = actionBar.GetSlot(slot)!.Value;
            if (code is < 1 or > 12 ||
                marker != ((0xA58811 - action.Id - slot -
                    (int)action.Kind - code) & 0xFFFFFF))
                continue;

            int flags = code - 1;
            int range = flags >> 2;
            if (range > 2 ||
                (action.Kind == VisualActionKind.Empty && code != 1))
                continue;
            bool? inRange = range switch
            {
                1 => true,
                2 => false,
                _ => null
            };
            states[slot - 1] = new(slot, (flags & 1) != 0,
                (flags & 2) != 0, inRange);
        }

        return new(states);
    }

    private static VisualBindingSnapshot? TryReadBindings(in Probe probe)
    {
        if (probe.Cell(WorldRow, 7) != VisualBindingSnapshot.Count ||
            probe.Cell(WorldRow, 8) != ((0xA57722 - VisualBindingSnapshot.Count) & 0xFFFFFF))
            return null;

        int?[] codes = new int?[VisualBindingSnapshot.Count];
        for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
        {
            int code = probe.Grid(BindingGridTop, index, 4);
            int valid = probe.Grid(BindingGridTop, index, 12);
            if (code < 0 || valid != ((0xA57711 - code - index) & 0xFFFFFF))
                continue;
            if (code > 0 && ((code >> 14) & 63) != index)
                continue;
            codes[index - 1] = code;
        }
        return new(codes);
    }

    private static VisualSpellBookSnapshot? TryReadSpellBook(in Probe probe)
    {
        int count = probe.Cell(WorldRow, 5);
        if (count < 0 || count > VisualSpellBookSnapshot.MaxSlots ||
            probe.Cell(WorldRow, 6) != ((0xA56622 - count) & 0xFFFFFF))
            return null;

        VisualSpellBookEntry?[] entries = new VisualSpellBookEntry?[count];
        for (int slot = 1; slot <= count; slot++)
        {
            int id = probe.Grid(SpellGridTop, slot, 4);
            int meta = probe.Grid(SpellGridTop, slot, 12);
            if (id <= 0 || meta < 0)
                continue;

            VisualSpellIdSource source = (VisualSpellIdSource)(meta & 15);
            int checksum = (0xA56611 - id - slot) & 0xFFFFFF;
            if (source is not (VisualSpellIdSource.SpellId or VisualSpellIdSource.ActionId) ||
                (meta & 0xFFFFF0) != (checksum & 0xFFFFF0))
                continue;

            entries[slot - 1] = new(slot, source, id);
        }

        return new(entries);
    }

    private static VisualWorldPositionSample? TryReadWorldPosition(in Probe probe)
    {
        int x = probe.Cell(WorldRow, 0), y = probe.Cell(WorldRow, 1), z = probe.Cell(WorldRow, 2);
        int instanceMapId = probe.Cell(WorldRow, 3);
        if (x < 0 || y < 0 || z < 0 || instanceMapId < 0 ||
            probe.Cell(WorldRow, 4) != ((0xA55A33 - x - y - z - instanceMapId) & 0xFFFFFF))
            return null;

        return new((x - 8388608) / 100.0, (y - 8388608) / 100.0,
            (z - 8388608) / 100.0, instanceMapId);
    }

    private static int? TryReadPowerType(in Probe probe)
    {
        int value = probe.Cell(WorldRow, 9);
        int marker = probe.Cell(WorldRow, 10);
        return value is >= 0 and <= 255 &&
            marker == ((0xA56644 - value) & 0xFFFFFF)
            ? value : null;
    }

    private static VisualBagSnapshot? TryReadBags(in Probe probe)
    {
        VisualBagHeader?[] headers = new VisualBagHeader?[VisualBagSnapshot.BagCount];
        VisualBagSlot?[] slots = new VisualBagSlot?[
            VisualBagSnapshot.BagCount * VisualBagSnapshot.SlotsPerBag];
        int headerY = probe.Y(BagHeaderRow);
        for (int bag = 0; bag < headers.Length; bag++)
        {
            int x = probe.X(5 + bag * 60);
            int packed = probe.Value(x, headerY);
            int family = probe.Value(x + probe.Units(20), headerY);
            int marker = probe.Value(x + probe.Units(40), headerY);
            if (packed < 0 || family < 0 || marker < 0 || packed >= 4096 ||
                family > 0xFFFFFF ||
                marker != ((0xA5BA11 - bag - packed - family) & 0xFFFFFF))
                continue;
            int count = packed % 64;
            int free = packed / 64;
            if (count > VisualBagSnapshot.SlotsPerBag || free > count)
                continue;
            headers[bag] = new(bag, count, free, family);
        }

        for (int index = 0; index < slots.Length; index++)
        {
            int bag = index / VisualBagSnapshot.SlotsPerBag;
            int slot = index % VisualBagSnapshot.SlotsPerBag + 1;
            int col = index % 10;
            int row = index / 10;
            int y = probe.Y(BagGridTop + row * 8);
            int x = probe.X(4 + col * 30);
            int id = probe.Value(x, y);
            int count = probe.Value(x + probe.Units(10), y);
            int marker = probe.Value(x + probe.Units(20), y);
            if (id < 0 || count < 0 || marker < 0 ||
                marker != ((0xA5BB11 - index - id - count) & 0xFFFFFF) ||
                (id == 0) != (count == 0) || count > 65535 ||
                headers[bag] is not { } header ||
                (slot > header.SlotCount && id != 0))
                continue;
            slots[index] = new(bag, slot, id, count);
        }
        return new(headers, slots);
    }

    private static int? TryReadMaximum(in Probe probe, int valueIndex, int markerIndex,
        int expectedMarker)
    {
        int maximum = probe.Cell(VitalsRow, valueIndex);
        int checksum = probe.Cell(VitalsRow, markerIndex);
        return maximum > 0 &&
            checksum == ((expectedMarker - maximum) & 0xFFFFFF)
            ? maximum : null;
    }

    private static VisualActionBarSnapshot? TryReadActionBar(in Probe probe)
    {
        int markerY = probe.Y(VitalsRow);
        int markerX = probe.X(205);
        if (!probe.Contains(markerX, markerY))
            return null;

        Bgra32 gridMarker = probe.Pixel(markerX, markerY);
        if (Math.Abs(gridMarker.R - 90) > 3 ||
            Math.Abs(gridMarker.G - 165) > 3 ||
            Math.Abs(gridMarker.B - 90) > 3)
            return null;

        VisualActionSample?[] slots = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
        for (int slot = 1; slot <= slots.Length; slot++)
        {
            (int idX, int y) = probe.GridPoint(ActionGridTop, slot, 4);
            int metaX = probe.GridPoint(ActionGridTop, slot, 12).X;
            if (!probe.Contains(idX, y) || !probe.Contains(metaX, y))
                return null;

            Bgra32 meta = probe.Pixel(metaX, y);
            if (Math.Abs(meta.R - 165) > 3 || meta.G > 3 || meta.B > 4)
                continue;

            int id = probe.Value(idX, y);
            VisualActionKind kind = (VisualActionKind)meta.B;
            if ((kind == VisualActionKind.Empty) != (id == 0))
                continue;

            slots[slot - 1] = new(slot, kind, id);
        }

        return new(slots);
    }

    private static bool? TryReadTargetPresence(in Probe probe)
    {
        int markerX = probe.X(145);
        int markerY = probe.Y(VitalsRow);
        if (!probe.Contains(markerX, markerY))
            return null;

        Bgra32 marker = probe.Pixel(markerX, markerY);
        if (Matches(marker, BarColor.Green))
            return true;
        if (Matches(marker, BarColor.Red))
            return false;
        return null;
    }

    private static VisualPositionSample? TryReadPosition(in Probe probe)
    {
        int mapId = probe.Cell(VitalsRow, 0);
        int encodedX = probe.Cell(VitalsRow, 1);
        int encodedY = probe.Cell(VitalsRow, 2);
        int encodedDirection = probe.Cell(VitalsRow, 3);
        if (mapId <= 0 || encodedX < 0 || encodedY < 0 ||
            encodedDirection < 0 || encodedDirection > 628400 ||
            probe.Cell(VitalsRow, 4) != ((0xA55AA5 - mapId - encodedX - encodedY -
                encodedDirection) & 0xFFFFFF))
            return null;

        return new(mapId, encodedX / 16777215.0,
            encodedY / 16777215.0, encodedDirection / 100000.0);
    }

    private static double RowFill(Bgra32[] pixels, int width, int height,
        int left, int centerY, int barWidth, int tolerance, BarColor color)
    {
        int best = 0;
        for (int y = Math.Max(0, centerY - tolerance);
             y <= Math.Min(height - 1, centerY + tolerance); y++)
        {
            int end = Math.Min(width, left + barWidth);
            int x = left;
            // Allow a one-pixel antialiased edge at the start of a row.
            if (x < end && !Matches(pixels[y * width + x], color))
                x++;
            int start = x;
            while (x < end && Matches(pixels[y * width + x], color))
                x++;
            best = Math.Max(best, x - start);
        }
        return Math.Clamp((double)best / barWidth, 0, 1);
    }

    private static bool Matches(Bgra32 pixel, BarColor color) => color switch
    {
        BarColor.White => pixel.R >= 220 && pixel.G >= 220 && pixel.B >= 220,
        BarColor.Yellow => pixel.R >= 190 && pixel.G >= 190 && pixel.B <= 110,
        BarColor.Green => pixel.G >= 170 && pixel.R <= 110 && pixel.B <= 110,
        BarColor.Blue => pixel.B >= 170 && pixel.R <= 110 && pixel.G <= 130,
        BarColor.Red => pixel.R >= 170 && pixel.G <= 110 && pixel.B <= 110,
        BarColor.Cyan => pixel.R <= 110 && pixel.G >= 170 && pixel.B >= 170,
        _ => false,
    };
}
