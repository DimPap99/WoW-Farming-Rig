using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Collections.Generic;

namespace Core;

/// <summary>
/// Per main-bar slot: whether one of the player's auras shows the icon of the
/// action on that slot. A slot without a readable reference icon is null.
/// </summary>
public sealed class VisualSlotAuraSnapshot(bool?[] states)
{
    public const int SlotCount = 12;

    public bool? IsActive(int slot) =>
        slot is >= 1 and <= SlotCount && slot <= states.Length
            ? states[slot - 1] : null;

    /// <summary>The slots with a readable state, as <c>slot=on|off</c>.</summary>
    public override string ToString()
    {
        List<string> parts = [];
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            if (IsActive(slot) is { } active)
                parts.Add($"{slot}={(active ? "on" : "off")}");
        }
        return parts.Count == 0 ? "no references" : string.Join(' ', parts);
    }
}

/// <summary>
/// Matches aura icons against one reference icon per main-bar slot, all
/// rendered in the same frame. Icons sit 18 units apart, 21 to a row: the
/// auras fill grid indices 1 to 40 and the slot references continue the first
/// row to its right.
/// </summary>
public static class VisualAuraReader
{
    public const int AuraCount = 40;

    private const int Pitch = 18;
    private const int PerRow = 21;
    private const int IconSize = 16;

    // Right edge of the last slot reference and bottom of the second aura row.
    private const int ExtentX =
        (PerRow - 1 + VisualSlotAuraSnapshot.SlotCount) * Pitch + IconSize;
    private const int ExtentY = Pitch + IconSize;

    /// <summary>Null when the strip at <paramref name="left"/>, <paramref name="top"/> is outside the frame.</summary>
    public static VisualSlotAuraSnapshot? Read(Bgra32[] pixels, int width, int height,
        int left, int top, double scale)
    {
        if (left < 0 || top < 0 ||
            left + (int)Math.Ceiling(ExtentX * scale) >= width ||
            top + (int)Math.Ceiling(ExtentY * scale) >= height)
            return null;

        bool?[] states = new bool?[VisualSlotAuraSnapshot.SlotCount];
        for (int slot = 1; slot <= states.Length; slot++)
            states[slot - 1] = ContainsReference(pixels, width, left, top, scale,
                ReferenceColumn(slot));
        return new(states);
    }

    /// <summary>Grid column of slot <paramref name="slot"/>'s reference icon, on the first row.</summary>
    public static int ReferenceColumn(int slot) => PerRow - 1 + slot;

    private static bool? ContainsReference(Bgra32[] pixels, int width,
        int left, int top, double scale, int referenceColumn)
    {
        Bgra32 Pixel(int column, int row, int x, int y) => pixels[
            (top + (int)Math.Round((row * Pitch + y) * scale)) * width +
            left + (int)Math.Round((column * Pitch + x) * scale)];

        int lit = 0;
        for (int y = 3; y <= 12; y++)
            for (int x = 3; x <= 12; x++)
            {
                Bgra32 p = Pixel(referenceColumn, 0, x, y);
                if (p.R + p.G + p.B > 90) lit++;
            }
        if (lit < 20) return null;

        for (int index = 1; index <= AuraCount; index++)
        {
            int error = 0, close = 0;
            for (int y = 3; y <= 12; y++)
                for (int x = 3; x <= 12; x++)
                {
                    Bgra32 a = Pixel(referenceColumn, 0, x, y);
                    Bgra32 b = Pixel(index % PerRow, index / PerRow, x, y);
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
