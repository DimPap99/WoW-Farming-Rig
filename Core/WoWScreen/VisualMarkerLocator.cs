using SixLabors.ImageSharp.PixelFormats;

using System;

namespace Core;

/// <summary>
/// Finds the nameplate roster's markers in a full viewport frame. Each marker
/// is a 28-unit magenta square with a black ring around a 6-unit centre whose
/// red channel names its slot (16 + 32 per slot, green and blue full), drawn
/// at its plate's centre by ForeverNameplates.lua. Positions are normalized
/// to the frame, with Y measured up from the bottom as the game's UI does.
/// </summary>
public static class VisualMarkerLocator
{
    // The red code of slot 1 and the step between slots.
    private const int FirstCode = 16;
    private const int CodeStep = 32;
    private const int CodeTolerance = 12;

    /// <summary>One position per roster slot, null where no marker was found.</summary>
    /// <param name="scale">The UI scale: the white row's width over its 320 units.</param>
    public static VisualScreenPointSample?[] Locate(Bgra32[] pixels, int width, int height, double scale)
    {
        const int slots = VisualNameplateRoster.SlotCount;
        VisualScreenPointSample?[] found = new VisualScreenPointSample?[slots];
        if (pixels.Length != width * height)
            return found;

        Span<int> count = stackalloc int[slots];
        Span<long> sumX = stackalloc long[slots];
        Span<long> sumY = stackalloc long[slots];
        Span<int> minX = stackalloc int[slots];
        Span<int> maxX = stackalloc int[slots];
        Span<int> minY = stackalloc int[slots];
        Span<int> maxY = stackalloc int[slots];
        Span<int> anchorX = stackalloc int[slots];
        Span<int> anchorY = stackalloc int[slots];
        Span<bool> anchored = stackalloc bool[slots];
        minX.Fill(int.MaxValue);
        minY.Fill(int.MaxValue);

        int offset = Math.Max(8, (int)Math.Round(12 * scale));
        int blackOffset = Math.Max(4, (int)Math.Round(8 * scale));
        // A centre-coloured pixel is searched around at full resolution: the
        // whole pattern only fits within a pixel or so of the true centre.
        int window = (int)Math.Ceiling(6 * scale) + 1;
        int sameMarker = (int)Math.Ceiling(10 * scale);

        // The centre is at least four pixels wide at the smallest UI scale,
        // so every second pixel of every second row lands in it.
        for (int y = offset; y < height - offset; y += 2)
        {
            for (int x = offset; x < width - offset; x += 2)
            {
                Bgra32 pixel = pixels[y * width + x];
                if (pixel.G < 240 || pixel.B < 240 || SlotOf(pixel) is not int slot)
                    continue;
                if (anchored[slot] && Math.Abs(x - anchorX[slot]) <= sameMarker &&
                    Math.Abs(y - anchorY[slot]) <= sameMarker)
                    continue;
                anchored[slot] = true;
                anchorX[slot] = x;
                anchorY[slot] = y;

                for (int cy = Math.Max(offset, y - window); cy <= Math.Min(height - offset - 1, y + window); cy++)
                {
                    for (int cx = Math.Max(offset, x - window); cx <= Math.Min(width - offset - 1, x + window); cx++)
                    {
                        if (!IsCenter(pixels, width, cx, cy, slot, offset, blackOffset))
                            continue;
                        count[slot]++;
                        sumX[slot] += cx;
                        sumY[slot] += cy;
                        minX[slot] = Math.Min(minX[slot], cx);
                        maxX[slot] = Math.Max(maxX[slot], cx);
                        minY[slot] = Math.Min(minY[slot], cy);
                        maxY[slot] = Math.Max(maxY[slot], cy);
                    }
                }
            }
        }

        for (int slot = 0; slot < slots; slot++)
        {
            // One marker per slot: matches spread wider than one centre are
            // two things wearing the same colours, and neither is trusted.
            if (count[slot] < 2 || maxX[slot] - minX[slot] > 10 * scale ||
                maxY[slot] - minY[slot] > 10 * scale)
                continue;
            found[slot] = new((double)sumX[slot] / count[slot] / width,
                1 - (double)sumY[slot] / count[slot] / height);
        }
        return found;
    }

    // The zero-based slot whose centre colour this pixel has, or null.
    private static int? SlotOf(Bgra32 pixel)
    {
        int slot = pixel.R / CodeStep;
        return Math.Abs(pixel.R - (FirstCode + slot * CodeStep)) <= CodeTolerance ? slot : null;
    }

    // The slot's centre colour with the border at the four offsets and the
    // ring (or, at small scales, the border) either side.
    private static bool IsCenter(Bgra32[] pixels, int width, int x, int y, int slot,
        int offset, int blackOffset)
    {
        int at = y * width + x;
        Bgra32 center = pixels[at];
        return center.G >= 240 && center.B >= 240 && SlotOf(center) == slot &&
            Magenta(pixels[at - offset]) && Magenta(pixels[at + offset]) &&
            Magenta(pixels[at - offset * width]) && Magenta(pixels[at + offset * width]) &&
            (Black(pixels[at - blackOffset]) && Black(pixels[at + blackOffset]) ||
             Magenta(pixels[at - blackOffset]) && Magenta(pixels[at + blackOffset]));
    }

    private static bool Magenta(Bgra32 pixel) =>
        pixel.R >= 240 && pixel.G <= 20 && pixel.B >= 240;

    private static bool Black(Bgra32 pixel) =>
        pixel.R <= 20 && pixel.G <= 20 && pixel.B <= 20;
}
