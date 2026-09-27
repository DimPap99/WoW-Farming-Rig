using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using PPather.Storage;

namespace Wmo;

internal static partial class MapTileFile
{
    private const int Mh2oCellCount = 16 * 16;
    private const int Mh2oCellHeaderSize = 12;
    private const int Mh2oInstanceSize = 24;

    /// <summary>
    /// Numeric CASC clients can use LiquidObject IDs in the MH2O vertex-format
    /// field and can put several independently masked layers in one MCNK.
    /// Offsets here are relative to the MH2O payload after its eight-byte tag.
    /// </summary>
    private static void HandleNumericMH2O(BinaryReader file, uint size,
        ILiquidMetadataSource metadata, out LiquidData[] data)
    {
        if (size < Mh2oCellCount * Mh2oCellHeaderSize || size > int.MaxValue)
            throw new InvalidDataException("Numeric MH2O has an invalid header size.");

        byte[] payload = file.ReadBytes((int)size);
        if (payload.Length != size)
            throw new EndOfStreamException("Numeric MH2O ends inside its payload.");

        data = new LiquidData[Mh2oCellCount];
        ReadOnlySpan<byte> bytes = payload;
        for (int cell = 0; cell < Mh2oCellCount; cell++)
        {
            int entry = cell * Mh2oCellHeaderSize;
            uint instanceOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(entry, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(entry + 4, 4));
            if (count == 0)
            {
                data[cell] = new(0, 0, 0, EmptyMH2OData1, [], []);
                continue;
            }
            if (instanceOffset == 0 || (ulong)instanceOffset + (ulong)count * Mh2oInstanceSize > (uint)payload.Length)
                throw new InvalidDataException($"Numeric MH2O cell {cell} has invalid layer references.");

            List<ModernLiquidLayer> layers = new(checked((int)count));
            for (int layer = 0; layer < count; layer++)
            {
                int start = checked((int)instanceOffset + layer * Mh2oInstanceSize);
                ReadOnlySpan<byte> instance = bytes.Slice(start, Mh2oInstanceSize);
                ushort liquidType = BinaryPrimitives.ReadUInt16LittleEndian(instance);
                ushort formatOrObject = BinaryPrimitives.ReadUInt16LittleEndian(instance.Slice(2));
                LiquidDescriptor descriptor = metadata.ResolveLiquid(liquidType, formatOrObject);
                if (descriptor.IsVisualOnly)
                    continue;
                byte offsetX = instance[12];
                byte offsetY = instance[13];
                byte width = instance[14];
                byte height = instance[15];
                if (formatOrObject >= 42)
                {
                    offsetX = 0;
                    offsetY = 0;
                    width = 8;
                    height = 8;
                }
                if (width == 0 || height == 0 || offsetX + width > 8 || offsetY + height > 8)
                    throw new InvalidDataException($"Numeric MH2O cell {cell} layer {layer} has invalid dimensions.");

                uint maskOffset = BinaryPrimitives.ReadUInt32LittleEndian(instance.Slice(16));
                ulong mask = maskOffset == 0 ? ulong.MaxValue : ReadMask(bytes, maskOffset, cell, layer);
                uint vertexOffset = BinaryPrimitives.ReadUInt32LittleEndian(instance.Slice(20));
                float[] heights = ReadHeights(bytes, vertexOffset, descriptor.VertexFormat,
                    width, height, cell, layer);
                layers.Add(new(offsetX, offsetY, width, height, mask, heights,
                    descriptor.IsMagmaOrSlime));
            }
            data[cell] = new(0, 0, 0, EmptyMH2OData1, [], [], [.. layers]);
        }
    }

    private static ulong ReadMask(ReadOnlySpan<byte> payload, uint offset, int cell, int layer)
    {
        if ((ulong)offset + sizeof(ulong) > (uint)payload.Length)
            throw new InvalidDataException($"Numeric MH2O cell {cell} layer {layer} has an invalid mask offset.");
        return BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice((int)offset, sizeof(ulong)));
    }

    private static float[] ReadHeights(ReadOnlySpan<byte> payload, uint offset, byte format,
        byte width, byte height, int cell, int layer)
    {
        int count = (width + 1) * (height + 1);
        float[] heights = new float[count];
        if (offset == 0 || format == 2)
            return heights;

        int stride = format is 4 or 5 ? 8 : 4;
        int first = format is 4 or 5 ? 4 : 0;
        if ((ulong)offset + (ulong)first + (ulong)(count - 1) * (uint)stride + sizeof(float) > (uint)payload.Length)
            throw new InvalidDataException($"Numeric MH2O cell {cell} layer {layer} has invalid vertex data.");

        for (int i = 0; i < count; i++)
        {
            float heightValue = BinaryPrimitives.ReadSingleLittleEndian(
                payload.Slice(checked((int)offset + first + i * stride), sizeof(float)));
            if (!float.IsFinite(heightValue))
                throw new InvalidDataException($"Numeric MH2O cell {cell} layer {layer} has non-finite height.");
            heights[i] = heightValue;
        }
        return heights;
    }
}
