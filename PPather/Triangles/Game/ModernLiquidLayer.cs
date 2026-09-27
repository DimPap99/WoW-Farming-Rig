namespace Wmo;

/// <summary>One visible MH2O layer, in its local MCNK 8x8 square grid.</summary>
internal readonly record struct ModernLiquidLayer(
    byte OffsetX, byte OffsetY, byte Width, byte Height,
    ulong VisibleMask, float[] Heights, bool IsMagmaOrSlime);
