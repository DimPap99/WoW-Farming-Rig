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
    VisualWorldPositionSample? WorldPosition = null);

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
        int? playerHealthMax = TryReadMaximum(pixels, width, height,
            whiteLeft, whiteY, scale, 11, 12, 0xA55A11);
        int? playerPowerMax = TryReadMaximum(pixels, width, height,
            whiteLeft, whiteY, scale, 13, 14, 0xA55A22);
        VisualWorldPositionSample? worldPosition = TryReadWorldPosition(
            pixels, width, height, whiteLeft, whiteY, scale);
        sample = new(health, power, target, half, whiteLeft, whiteY, whiteWidth,
            position, actionCooldownFill, targetPresent, watchedAction, actionBar,
            playerHealthMax, playerPowerMax, worldPosition);
        return true;
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
