using System;

namespace PPather.Storage;

/// <summary>Build-specific MH2O metadata needed when a vertex format is a LiquidObject ID.</summary>
public interface ILiquidMetadataSource
{
    LiquidDescriptor ResolveLiquid(ushort liquidTypeId, ushort vertexFormatOrObjectId);
}

public readonly record struct LiquidDescriptor(byte VertexFormat, bool IsMagmaOrSlime,
    bool IsVisualOnly);
