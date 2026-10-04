using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core;

/// <summary>
/// Where the minimap sits in the capture, as shares of the frame: the centre
/// across the width and down from the top, and the radius as a share of the
/// width. <see cref="ViewYards"/> is how far its radius reaches in the world;
/// <see cref="Rotates"/>, whether it turns with the player.
/// </summary>
public readonly record struct VisualMinimap(double CenterX, double CenterY, double Radius,
    double ViewYards, bool Rotates);

/// <summary>
/// Decodes the add-on's minimap block (ForeverVisual.lua), and finds the dots
/// the minimap draws for tracked units, such as a Hunter's Track Beasts: a few
/// pixels round in a gold ring, with a gold centre for a neutral unit and a
/// dark red one for a hostile unit; a green centre, a friendly unit, is left
/// out. No game function returns where those units are, but the dots are in
/// the capture, and each becomes a world point from the player's position
/// and, on a minimap that turns with the player, the player's facing.
/// </summary>
public static class VisualMinimapReader
{
    /// <summary>The layout this reader accepts; the add-on writes it in the flags cell.</summary>
    public const int ProtocolVersion = 1;

    // Frame row 105 on the probe grid (the world row), six cells from cell 15.
    internal const int Row = 42;
    internal const int FirstCell = 15;
    internal const int Seed = 0xA5F122;
    // The block's shares of the screen are written out of this.
    private const double Share = 65535;

    // A dot's size at the reference minimap, 94 pixels in radius as the
    // default UI draws it at 1080p: about 19 gold pixels, 8 across, or a dark
    // red core of 16 to 25.
    private const double ReferenceRadius = 94;
    private const double MinDotPixels = 6;
    private const double MaxDotPixels = 60;
    private const double MaxDotWidth = 14;
    // The rim, where the border's gold trim begins, is left out, and so is
    // the outer band where the minimap draws its gold icons, such as the
    // arrows toward quest objectives: a dot farther out than this share of
    // the radius is not taken.
    private const double RimPixels = 3;
    private const double MaxDotRadiusShare = 0.78;

    /// <summary>The minimap, or null when the block is unreadable or from another layout.</summary>
    internal static VisualMinimap? Read(in VisualBarReader.Probe probe)
    {
        int x = probe.Cell(Row, FirstCell);
        int y = probe.Cell(Row, FirstCell + 1);
        int radius = probe.Cell(Row, FirstCell + 2);
        int view = probe.Cell(Row, FirstCell + 3);
        int flags = probe.Cell(Row, FirstCell + 4);
        int marker = probe.Cell(Row, FirstCell + 5);
        if (x is < 0 or > 65535 || y is < 0 or > 65535 || radius is <= 0 or > 65535 ||
            view <= 0 || flags < 0 || (flags >> 4) != ProtocolVersion ||
            marker != ((Seed - x - y - radius - view - flags) & 0xFFFFFF))
            return null;
        return new(x / Share, y / Share, radius / Share, view / 100.0, (flags & 1) != 0);
    }

    /// <summary>The square of the frame, in pixels, that holds the minimap; empty when it lies outside.</summary>
    public static (int Left, int Top, int Size) Region(VisualMinimap map, int frameWidth, int frameHeight)
    {
        double radius = map.Radius * frameWidth;
        int left = (int)Math.Floor(map.CenterX * frameWidth - radius);
        int top = (int)Math.Floor(map.CenterY * frameHeight - radius);
        int size = (int)Math.Ceiling(radius * 2) + 1;
        return left < 0 || top < 0 || left + size > frameWidth || top + size > frameHeight || size < 20
            ? (0, 0, 0) : (left, top, size);
    }

    /// <summary>
    /// The world point of every tracked-unit dot inside the minimap, from
    /// <paramref name="region"/>, the square <see cref="Region"/> named,
    /// copied out of the frame.
    /// </summary>
    /// <param name="player">The player's world X/Y, where the minimap is centred.</param>
    /// <param name="facing">The player's facing, which turns a rotating minimap.</param>
    public static List<Vector2> FindBlips(Bgra32[] region, int left, int top, int size,
        VisualMinimap map, int frameWidth, int frameHeight, Vector2 player, double facing)
    {
        List<Vector2> blips = [];
        if (size <= 0 || region.Length < size * size)
            return blips;

        double radius = map.Radius * frameWidth;
        double centerX = map.CenterX * frameWidth - left;
        double centerY = map.CenterY * frameHeight - top;
        double inside = Math.Max(0, radius - RimPixels);
        double scale = radius / ReferenceRadius;
        double minPixels = Math.Max(4, MinDotPixels * scale * scale);
        double maxPixels = MaxDotPixels * scale * scale;
        double maxWidth = MaxDotWidth * scale + 2;
        double yardsPerPixel = map.ViewYards / radius;
        // Screen up is north, or the player's facing on a rotating minimap.
        double up = map.Rotates ? facing : 0;
        Vector2 forward = new((float)Math.Cos(up), (float)Math.Sin(up));
        Vector2 right = new((float)Math.Cos(up - Math.PI / 2), (float)Math.Sin(up - Math.PI / 2));

        // Each pixel's dot colour: 0 none, else the kind of unit it shows.
        byte[] kind = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - centerX, dy = y - centerY;
                if (dx * dx + dy * dy <= inside * inside)
                    kind[y * size + x] = DotKind(region[y * size + x]);
            }
        }

        Stack<int> pending = new();
        for (int start = 0; start < kind.Length; start++)
        {
            byte dot = kind[start];
            if (dot == 0)
                continue;
            // One dot: the pixels of its colour joined to this one.
            kind[start] = 0;
            pending.Push(start);
            int count = 0;
            long sumX = 0, sumY = 0;
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            while (pending.Count > 0)
            {
                int index = pending.Pop();
                int x = index % size, y = index / size;
                count++;
                sumX += x;
                sumY += y;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(size - 1, y + 1); ny++)
                {
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(size - 1, x + 1); nx++)
                    {
                        int next = ny * size + nx;
                        if (kind[next] != dot)
                            continue;
                        kind[next] = 0;
                        pending.Push(next);
                    }
                }
            }
            if (count < minPixels || count > maxPixels ||
                maxX - minX + 1 > maxWidth || maxY - minY + 1 > maxWidth)
                continue;
            double pixelsRight = sumX / (double)count - centerX;
            double pixelsUp = centerY - sumY / (double)count;
            if (pixelsRight * pixelsRight + pixelsUp * pixelsUp > Math.Pow(MaxDotRadiusShare * radius, 2))
                continue;
            double offsetRight = pixelsRight * yardsPerPixel;
            double offsetUp = pixelsUp * yardsPerPixel;
            blips.Add(player + forward * (float)offsetUp + right * (float)offsetRight);
        }
        return blips;
    }

    // 1 for a neutral unit's gold (bright red and green, little blue), 2 for a
    // hostile unit's dark red (red alone), 0 for anything else: the minimap's
    // terrain and water, the player's arrow, a friendly unit's green.
    private static byte DotKind(Bgra32 pixel) =>
        pixel.R >= 190 && pixel.G >= 140 && pixel.B <= 100 && pixel.R - pixel.B >= 120 && pixel.R >= pixel.G ? (byte)1
        : pixel.R >= 90 && pixel.G <= 30 && pixel.B <= 30 && pixel.R - pixel.G >= 70 ? (byte)2
        : (byte)0;
}
