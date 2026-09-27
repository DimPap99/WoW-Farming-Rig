using SixLabors.ImageSharp.PixelFormats;

using System;

namespace Core;

public readonly record struct VisualPositionSample(
    int UIMapId,
    double MapX,
    double MapY,
    double Direction);

public readonly record struct VisualWorldPositionSample(
    double X,
    double Y,
    double Z,
    int InstanceMapId);

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
    double? ActionCooldownFill = null,
    bool? TargetPresent = null,
    VisualActionSample? WatchedAction = null,
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
    VisualTargetIdentity? TargetIdentity = null);

/// <summary>
/// Reads the fixed-color rows drawn by /forevervisual. The full and
/// half rows establish pixel position and scale without relying on the game's
/// UI scale setting. This is a diagnostic prototype, not the bot data reader.
/// </summary>
public static class VisualBarReader
{
    private enum BarColor { White, Yellow, Green, Blue, Red, Cyan }

    public static bool TryRead(Bgra32[] pixels, int width, int height, out VisualBarSample sample)
    {
        sample = default;
        if (pixels.Length != width * height || width < 200 || height < 100)
            return false;

        int searchWidth = Math.Min(width, 650);
        int searchHeight = Math.Min(height, 250);
        int whiteLeft = -1;
        int whiteY = -1;
        int whiteWidth = 0;

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

        if (whiteWidth < 100 || whiteLeft < 0)
            return false;

        double scale = whiteWidth / 320.0;
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

        VisualPositionSample? position = TryReadPosition(
            pixels, width, height, whiteLeft, whiteY, scale, out VisualPositionSample decoded)
            ? decoded : null;
        double? actionCooldownFill = TryReadCooldown(
            pixels, width, height, whiteLeft, whiteY, scale, rowStep, tolerance);
        bool? targetPresent = TryReadTargetPresence(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualActionSample? watchedAction = TryReadWatchedAction(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualActionBarSnapshot? actionBar = TryReadActionBar(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualActionStateSnapshot? actionStates = TryReadActionStates(
            pixels, width, height, whiteLeft, whiteY, scale, actionBar);
        VisualCooldownGridSnapshot? cooldowns = TryReadCooldownGrid(
            pixels, width, height, whiteLeft, whiteY, scale, actionBar);
        int? playerHealthMax = TryReadMaximum(pixels, width, height,
            whiteLeft, whiteY, scale, 11, 12, 0xA55A11);
        int? playerPowerMax = TryReadMaximum(pixels, width, height,
            whiteLeft, whiteY, scale, 13, 14, 0xA55A22);
        VisualWorldPositionSample? worldPosition = TryReadWorldPosition(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualSpellBookSnapshot? spellBook = TryReadSpellBook(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualBindingSnapshot? bindings = TryReadBindings(
            pixels, width, height, whiteLeft, whiteY, scale);
        int? playerPowerType = TryReadPowerType(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualBagSnapshot? bags = TryReadBags(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualPlayerIdentity? playerIdentity = TryReadPlayerIdentity(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualTargetFlags? targetFlags = TryReadTargetFlags(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualCastEvent? castEvent = TryReadCastEvent(
            pixels, width, height, whiteLeft, whiteY, scale);
        VisualTargetIdentity? targetIdentity = TryReadTargetIdentity(
            pixels, width, height, whiteLeft, whiteY, scale);
        if (targetFlags is { } checkedFlags &&
            targetIdentity is { } checkedIdentity &&
            checkedFlags.Present != checkedIdentity.Present)
            targetIdentity = null;
        sample = new(health, power, target, half, whiteLeft, whiteY, whiteWidth,
            position, actionCooldownFill, targetPresent, watchedAction, actionBar,
            playerHealthMax, playerPowerMax, worldPosition, spellBook, bindings,
            actionStates, cooldowns, playerPowerType, bags, playerIdentity,
            targetFlags, castEvent, targetIdentity);
        return true;
    }

    private static int? TryReadCheckedWorldPixel(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale,
        int index, int markerSeed)
    {
        int y = whiteY + (int)Math.Round(42 * scale);
        int valueX = left + (int)Math.Round((5 + index * 20) * scale);
        int markerX = left + (int)Math.Round((25 + index * 20) * scale);
        if (y < 0 || y >= height || valueX < 0 || valueX >= width ||
            markerX < 0 || markerX >= width)
            return null;
        Bgra32 valuePixel = pixels[y * width + valueX];
        int value = (valuePixel.R << 16) | (valuePixel.G << 8) | valuePixel.B;
        Bgra32 markerPixel = pixels[y * width + markerX];
        int marker = (markerPixel.R << 16) | (markerPixel.G << 8) | markerPixel.B;
        return marker == ((markerSeed - value) & 0xFFFFFF) ? value : null;
    }

    private static VisualPlayerIdentity? TryReadPlayerIdentity(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int? packed = TryReadCheckedWorldPixel(pixels, width, height,
            left, whiteY, scale, 11, 0xA57744);
        if (packed is not { } value)
            return null;
        int classId = value % 64;
        int raceId = value / 64 % 256;
        int level = value / 16384;
        return classId is >= 1 and <= 63 && raceId is >= 1 and <= 255 &&
            level is >= 1 and <= 1023
            ? new(classId, raceId, level) : null;
    }

    private static VisualTargetFlags? TryReadTargetFlags(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int? flags = TryReadCheckedWorldPixel(pixels, width, height,
            left, whiteY, scale, 13, 0xA57755);
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

    private static VisualCastEvent? TryReadCastEvent(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int y = whiteY + (int)Math.Round(127 * scale);
        int Read(int index)
        {
            int x = left + (int)Math.Round((5 + index * 20) * scale);
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 pixel = pixels[y * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        int sequence = Read(0);
        int spellId = Read(1);
        int marker = Read(2);
        if (sequence is < 0 or > 65535 || spellId < 0 || marker < 0 ||
            (sequence == 0) != (spellId == 0) ||
            marker != ((0xA5CC11 - sequence - spellId) & 0xFFFFFF))
            return null;
        return new(sequence, spellId);
    }

    private static VisualTargetIdentity? TryReadTargetIdentity(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int y = whiteY + (int)Math.Round(127 * scale);
        int Read(int index)
        {
            int x = left + (int)Math.Round((5 + index * 20) * scale);
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 pixel = pixels[y * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        int hash1 = Read(3);
        int hash2 = Read(4);
        int packedLevel = Read(5);
        int marker = Read(6);
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

    private static VisualCooldownGridSnapshot? TryReadCooldownGrid(
        Bgra32[] pixels, int width, int height, int left, int whiteY,
        double scale, VisualActionBarSnapshot? actionBar)
    {
        if (actionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount })
            return null;

        double?[] fills = new double?[VisualCooldownGridSnapshot.SlotCount];
        for (int slot = 1; slot <= fills.Length; slot++)
        {
            int x = left + (int)Math.Round((5 + (slot - 1) * 20) * scale);
            int y = whiteY + (int)Math.Round(67 * scale);
            if (x < 0 || x >= width || y < 0 || y >= height)
                return null;
            Bgra32 marker = pixels[y * width + x];
            int code = (marker.R << 16) | (marker.G << 8) | marker.B;
            VisualActionSample action = actionBar.GetSlot(slot)!.Value;
            if (code != ((0xA59911 - slot - (int)action.Kind - action.Id) &
                0xFFFFFF))
                continue;
            if (action.Kind == VisualActionKind.Empty)
            {
                fills[slot - 1] = 0;
                continue;
            }

            int centerY = whiteY + (int)Math.Round(
                (335 + (slot - 1) * 8) * scale);
            int barWidth = (int)Math.Round(320 * scale);
            if (centerY < 0 || centerY >= height ||
                left + barWidth > width)
                return null;
            fills[slot - 1] = RowFill(pixels, width, height,
                left, centerY, barWidth,
                Math.Max(1, (int)Math.Round(2 * scale)), BarColor.Cyan);
        }

        return new(fills);
    }

    private static VisualActionStateSnapshot? TryReadActionStates(
        Bgra32[] pixels, int width, int height, int left, int whiteY,
        double scale, VisualActionBarSnapshot? actionBar)
    {
        if (actionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount })
            return null;

        int ReadAt(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 pixel = pixels[y * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        int markerY = whiteY + (int)Math.Round(27 * scale);
        int markerX = left + (int)Math.Round(305 * scale);
        if (ReadAt(markerX, markerY) != 0x5A88A5)
            return null;

        VisualActionState?[] states =
            new VisualActionState?[VisualActionBarSnapshot.SlotCount];
        for (int slot = 1; slot <= states.Length; slot++)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int y = whiteY + (int)Math.Round((276 + row * 8) * scale);
            int codeX = left + (int)Math.Round((4 + column * 16) * scale);
            int validX = left + (int)Math.Round((12 + column * 16) * scale);
            int code = ReadAt(codeX, y);
            int marker = ReadAt(validX, y);
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

    private static VisualBindingSnapshot? TryReadBindings(
        Bgra32[] pixels, int width, int height, int left, int whiteY, double scale)
    {
        int ReadAt(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 pixel = pixels[y * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }
        int countY = whiteY + (int)Math.Round(42 * scale);
        int ReadCount(int index) => ReadAt(
            left + (int)Math.Round((5 + index * 20) * scale), countY);
        if (ReadCount(7) != VisualBindingSnapshot.Count ||
            ReadCount(8) != ((0xA57722 - VisualBindingSnapshot.Count) & 0xFFFFFF))
            return null;

        int?[] codes = new int?[VisualBindingSnapshot.Count];
        for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
        {
            int column = (index - 1) % 20;
            int row = (index - 1) / 20;
            int y = whiteY + (int)Math.Round((236 + row * 8) * scale);
            int code = ReadAt(left + (int)Math.Round((4 + column * 16) * scale), y);
            int valid = ReadAt(left + (int)Math.Round((12 + column * 16) * scale), y);
            if (code < 0 || valid != ((0xA57711 - code - index) & 0xFFFFFF))
                continue;
            if (code > 0 && ((code >> 14) & 63) != index)
                continue;
            codes[index - 1] = code;
        }
        return new(codes);
    }

    private static VisualSpellBookSnapshot? TryReadSpellBook(
        Bgra32[] pixels, int width, int height, int left, int whiteY, double scale)
    {
        int countY = whiteY + (int)Math.Round(42 * scale);
        int ReadAt(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 pixel = pixels[y * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }
        int CountPixel(int index) => ReadAt(
            left + (int)Math.Round((5 + index * 20) * scale), countY);

        int count = CountPixel(5);
        if (count < 0 || count > VisualSpellBookSnapshot.MaxSlots ||
            CountPixel(6) != ((0xA56622 - count) & 0xFFFFFF))
            return null;

        VisualSpellBookEntry?[] entries = new VisualSpellBookEntry?[count];
        for (int slot = 1; slot <= count; slot++)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int y = whiteY + (int)Math.Round((141 + row * 8) * scale);
            int id = ReadAt(left + (int)Math.Round((4 + column * 16) * scale), y);
            int meta = ReadAt(left + (int)Math.Round((12 + column * 16) * scale), y);
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

    private static VisualWorldPositionSample? TryReadWorldPosition(
        Bgra32[] pixels, int width, int height, int left, int whiteY, double scale)
    {
        int centerY = whiteY + (int)Math.Round(42 * scale);
        int ReadPixel(int index)
        {
            int x = left + (int)Math.Round((5 + index * 20) * scale);
            if (centerY < 0 || centerY >= height || x < 0 || x >= width)
                return -1;
            Bgra32 pixel = pixels[centerY * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        int x = ReadPixel(0), y = ReadPixel(1), z = ReadPixel(2);
        int instanceMapId = ReadPixel(3);
        if (x < 0 || y < 0 || z < 0 || instanceMapId < 0)
            return null;

        if (ReadPixel(4) != ((0xA55A33 - x - y - z - instanceMapId) & 0xFFFFFF))
            return null;

        return new((x - 8388608) / 100.0, (y - 8388608) / 100.0,
            (z - 8388608) / 100.0, instanceMapId);
    }

    private static int? TryReadPowerType(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale)
    {
        int y = whiteY + (int)Math.Round(42 * scale);
        int valueX = left + (int)Math.Round(185 * scale);
        int markerX = left + (int)Math.Round(205 * scale);
        if (y < 0 || y >= height || valueX < 0 || valueX >= width ||
            markerX < 0 || markerX >= width)
            return null;

        Bgra32 valuePixel = pixels[y * width + valueX];
        int value = (valuePixel.R << 16) | (valuePixel.G << 8) |
            valuePixel.B;
        Bgra32 markerPixel = pixels[y * width + markerX];
        int marker = (markerPixel.R << 16) | (markerPixel.G << 8) |
            markerPixel.B;
        return value <= 255 &&
            marker == ((0xA56644 - value) & 0xFFFFFF)
            ? value : null;
    }

    private static VisualBagSnapshot? TryReadBags(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int ReadAt(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return -1;
            Bgra32 p = pixels[y * width + x];
            return (p.R << 16) | (p.G << 8) | p.B;
        }

        VisualBagHeader?[] headers = new VisualBagHeader?[VisualBagSnapshot.BagCount];
        VisualBagSlot?[] slots = new VisualBagSlot?[
            VisualBagSnapshot.BagCount * VisualBagSnapshot.SlotsPerBag];
        int headerY = whiteY + (int)Math.Round(437 * scale);
        for (int bag = 0; bag < headers.Length; bag++)
        {
            int x = left + (int)Math.Round((5 + bag * 60) * scale);
            int packed = ReadAt(x, headerY);
            int family = ReadAt(x + (int)Math.Round(20 * scale), headerY);
            int marker = ReadAt(x + (int)Math.Round(40 * scale), headerY);
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
            int y = whiteY + (int)Math.Round((456 + row * 8) * scale);
            int x = left + (int)Math.Round((4 + col * 30) * scale);
            int id = ReadAt(x, y);
            int count = ReadAt(x + (int)Math.Round(10 * scale), y);
            int marker = ReadAt(x + (int)Math.Round(20 * scale), y);
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

    private static int? TryReadMaximum(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale, int valueIndex, int markerIndex,
        int expectedMarker)
    {
        int centerY = whiteY + (int)Math.Round(27 * scale);
        int valueX = left + (int)Math.Round((5 + valueIndex * 20) * scale);
        int markerX = left + (int)Math.Round((5 + markerIndex * 20) * scale);
        if (centerY < 0 || centerY >= height || valueX < 0 || valueX >= width ||
            markerX < 0 || markerX >= width)
            return null;

        Bgra32 value = pixels[centerY * width + valueX];
        int maximum = (value.R << 16) | (value.G << 8) | value.B;
        Bgra32 marker = pixels[centerY * width + markerX];
        int checksum = (marker.R << 16) | (marker.G << 8) | marker.B;
        return maximum > 0 &&
            checksum == ((expectedMarker - maximum) & 0xFFFFFF)
            ? maximum : null;
    }

    private static VisualActionBarSnapshot? TryReadActionBar(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int markerY = whiteY + (int)Math.Round(27 * scale);
        int markerX = left + (int)Math.Round(205 * scale);
        if (markerY < 0 || markerY >= height || markerX < 0 || markerX >= width)
            return null;

        Bgra32 gridMarker = pixels[markerY * width + markerX];
        if (Math.Abs(gridMarker.R - 90) > 3 ||
            Math.Abs(gridMarker.G - 165) > 3 ||
            Math.Abs(gridMarker.B - 90) > 3)
            return null;

        VisualActionSample?[] slots = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
        for (int slot = 1; slot <= slots.Length; slot++)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int y = whiteY + (int)Math.Round((76 + row * 8) * scale);
            int idX = left + (int)Math.Round((4 + column * 16) * scale);
            int metaX = left + (int)Math.Round((12 + column * 16) * scale);
            if (y < 0 || y >= height || idX < 0 || idX >= width ||
                metaX < 0 || metaX >= width)
                return null;

            Bgra32 meta = pixels[y * width + metaX];
            if (Math.Abs(meta.R - 165) > 3 || meta.G > 3 || meta.B > 4)
                continue;

            Bgra32 idPixel = pixels[y * width + idX];
            int id = (idPixel.R << 16) | (idPixel.G << 8) | idPixel.B;
            VisualActionKind kind = (VisualActionKind)meta.B;
            if ((kind == VisualActionKind.Empty) != (id == 0))
                continue;

            slots[slot - 1] = new(slot, kind, id);
        }

        return new(slots);
    }

    private static VisualActionSample? TryReadWatchedAction(Bgra32[] pixels,
        int width, int height, int left, int whiteY, double scale)
    {
        int centerY = whiteY + (int)Math.Round(27 * scale);
        int idX = left + (int)Math.Round(165 * scale);
        int markerX = left + (int)Math.Round(185 * scale);
        if (centerY < 0 || centerY >= height || idX < 0 || idX >= width ||
            markerX < 0 || markerX >= width)
            return null;

        Bgra32 marker = pixels[centerY * width + markerX];
        if (Math.Abs(marker.R - 165) > 3 || marker.G is < 1 or > 12 || marker.B > 4)
            return null;

        Bgra32 idPixel = pixels[centerY * width + idX];
        int id = (idPixel.R << 16) | (idPixel.G << 8) | idPixel.B;
        VisualActionKind kind = (VisualActionKind)marker.B;
        if ((kind == VisualActionKind.Empty) != (id == 0))
            return null;

        return new VisualActionSample(marker.G, kind, id);
    }

    private static bool? TryReadTargetPresence(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale)
    {
        int markerX = left + (int)Math.Round(145 * scale);
        int markerY = whiteY + (int)Math.Round(27 * scale);
        if (markerX < 0 || markerX >= width || markerY < 0 || markerY >= height)
            return null;

        Bgra32 marker = pixels[markerY * width + markerX];
        if (Matches(marker, BarColor.Green))
            return true;
        if (Matches(marker, BarColor.Red))
            return false;
        return null;
    }

    private static double? TryReadCooldown(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale, int rowStep, int tolerance)
    {
        int markerX = left + (int)Math.Round(125 * scale);
        int markerY = whiteY + (int)Math.Round(27 * scale);
        if (markerX < 0 || markerX >= width || markerY < 0 || markerY >= height)
            return null;

        Bgra32 marker = pixels[markerY * width + markerX];
        if (!Matches(marker, BarColor.Cyan))
            return null;

        return RowFill(pixels, width, height, left, whiteY + 3 * rowStep,
            (int)Math.Round(320 * scale), tolerance, BarColor.Cyan);
    }

    private static bool TryReadPosition(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale, out VisualPositionSample position)
    {
        position = default;
        int centerY = whiteY + (int)Math.Round(27 * scale);
        int markerX = left + (int)Math.Round(85 * scale);
        if (centerY < 0 || centerY >= height || markerX < 0 || markerX >= width)
            return false;

        int ReadPixel(int index)
        {
            int x = left + (int)Math.Round((5 + index * 20) * scale);
            if (x < 0 || x >= width)
                return -1;
            Bgra32 pixel = pixels[centerY * width + x];
            return (pixel.R << 16) | (pixel.G << 8) | pixel.B;
        }

        int mapId = ReadPixel(0);
        int encodedX = ReadPixel(1);
        int encodedY = ReadPixel(2);
        int encodedDirection = ReadPixel(3);
        if (mapId <= 0 || encodedX < 0 || encodedY < 0 ||
            encodedDirection < 0 || encodedDirection > 628400 ||
            ReadPixel(4) != ((0xA55AA5 - mapId - encodedX - encodedY -
                encodedDirection) & 0xFFFFFF))
            return false;

        position = new(mapId, encodedX / 16777215.0,
            encodedY / 16777215.0, encodedDirection / 100000.0);
        return true;
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
