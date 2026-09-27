using SixLabors.ImageSharp.PixelFormats;

using System;

namespace Core;

/// <summary>Matches an aura icon against a reference rendered in the same frame.</summary>
public static class VisualAuraReader
{
    public static bool? ContainsReference(Bgra32[] pixels, int width, int height,
        int left, int top, double scale)
    {
        if (left < 0 || top < 0 ||
            left + (int)Math.Ceiling(376 * scale) >= width ||
            top + (int)Math.Ceiling(34 * scale) >= height)
            return null;

        Bgra32 Pixel(int index, int x, int y) => pixels[
            (top + (int)Math.Round((index / 21 * 18 + y) * scale)) * width +
            left + (int)Math.Round((index % 21 * 18 + x) * scale)];

        int lit = 0;
        for (int y = 3; y <= 12; y++)
            for (int x = 3; x <= 12; x++)
            {
                Bgra32 p = Pixel(0, x, y);
                if (p.R + p.G + p.B > 90) lit++;
            }
        if (lit < 20) return null;

        for (int index = 1; index <= 40; index++)
        {
            int error = 0, close = 0;
            for (int y = 3; y <= 12; y++)
                for (int x = 3; x <= 12; x++)
                {
                    Bgra32 a = Pixel(0, x, y), b = Pixel(index, x, y);
                    int difference = Math.Abs(a.R - b.R) +
                        Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                    error += difference;
                    if (difference <= 45) close++;
                }
            if (error <= 4500 && close >= 80) return true;
        }
        return false;
    }
}
