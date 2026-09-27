using SixLabors.ImageSharp.PixelFormats;

using System;

namespace Core;

public readonly record struct VisualDeathEvent(int Sequence, int GuidHash1, int GuidHash2)
{
    public bool Confirms(VisualTargetIdentity target, int beforeSequence) =>
        Sequence > 0 && Sequence != beforeSequence && target.Present &&
        GuidHash1 == target.GuidHash1 && GuidHash2 == target.GuidHash2;
}

public readonly record struct VisualLootState(int OpenSequence, int CloseSequence,
    int ClearedSequence, int InitialSlots, int RemainingSlots, bool IsOpen,
    bool? TargetLootable, int TargetHash1, int TargetHash2)
{
    public bool Matches(VisualTargetIdentity target) => target.Present &&
        TargetHash1 == target.GuidHash1 && TargetHash2 == target.GuidHash2;

    public bool CompletedSince(VisualLootState before) =>
        OpenSequence != before.OpenSequence && CloseSequence != before.CloseSequence &&
        !IsOpen && RemainingSlots == 0 &&
        (InitialSlots == 0 || ClearedSequence != before.ClearedSequence);
}

/// <summary>Independent, checked event latches beside the spellbook grid.</summary>
public static class VisualLifecycleReader
{
    public static VisualDeathEvent? ReadDeath(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale)
    {
        Span<int> values = stackalloc int[4];
        if (!Read(pixels, width, height, left, whiteY, scale, 205, 0xA5D611, values) ||
            values[0] > 65535 || (values[0] == 0) != (values[1] == 0 && values[2] == 0))
            return null;
        return new(values[0], values[1], values[2]);
    }

    public static VisualLootState? ReadLoot(Bgra32[] pixels, int width, int height,
        int left, int whiteY, double scale)
    {
        Span<int> v = stackalloc int[9];
        if (!Read(pixels, width, height, left, whiteY, scale, 225, 0xA5D722, v) ||
            v[0] > 65535 || v[1] > 65535 || v[2] > 65535 ||
            v[3] > 256 || v[4] / 2 > v[3] || v[5] > 2)
            return null;
        return new(v[0], v[1], v[2], v[3], v[4] / 2, (v[4] & 1) != 0,
            v[5] == 0 ? null : v[5] == 2, v[6], v[7]);
    }

    private static bool Read(Bgra32[] pixels, int width, int height, int left,
        int whiteY, double scale, int row, int seed, Span<int> values)
    {
        int y = whiteY + (int)Math.Round((row - 68 + 5) * scale);
        if (y < 0 || y >= height) return false;
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int x = left + (int)Math.Round((345 + i * 20) * scale);
            if (x < 0 || x >= width) return false;
            Bgra32 p = pixels[y * width + x];
            values[i] = (p.R << 16) | (p.G << 8) | p.B;
            sum += values[i];
        }
        return (sum & 0xFFFFFF) == seed;
    }
}
