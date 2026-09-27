using PPather.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Wmo;

internal static partial class WmoRootFile
{
    private const uint GFID = 0x47464944;
    private const uint MODI = 0x4D4F4449;
    private const int DoodadSetSize = 32;
    private const int DoodadDefinitionSize = 40;
    private const uint DoodadNameIndexMask = 0x00FFFFFF;

    /// <summary>
    /// Reads a FileDataID WMO root. Chunk order is not significant: MODD can
    /// precede MODI or MODN. Returns only the full-detail GFID group IDs.
    /// </summary>
    public static uint[] LoadNumeric(IGameFileSource archive, uint fileDataId, WMORoot root)
    {
        if (!archive.SupportsFileDataIds)
            throw new NotSupportedException("This game-file source cannot open numeric WMO roots.");

        using Stream stream = archive.OpenRead(fileDataId);
        using BinaryReader reader = new(stream);
        Dictionary<uint, byte[]> chunks = new();
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8)
                throw new InvalidDataException($"WMO {fileDataId} ends inside a chunk header.");

            uint tag = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            long end = stream.Position + size;
            if (end > stream.Length)
                throw new InvalidDataException($"WMO {fileDataId} chunk extends beyond the file.");

            if (tag is ChunkReader.MOHD or ChunkReader.MOGI or ChunkReader.MODS or
                ChunkReader.MODD or ChunkReader.MODN or GFID or MODI)
            {
                if (!chunks.TryAdd(tag, reader.ReadBytes(checked((int)size))))
                    throw new InvalidDataException($"WMO {fileDataId} has a duplicate required chunk {tag:X8}.");
            }
            stream.Position = end;
        }

        if (!chunks.TryGetValue(ChunkReader.MOHD, out byte[]? mohd) || mohd.Length < 64 ||
            !chunks.TryGetValue(ChunkReader.MOGI, out byte[]? mogi) ||
            !chunks.TryGetValue(GFID, out byte[]? gfid))
        {
            throw new InvalidDataException($"WMO {fileDataId} lacks MOHD, MOGI, or GFID.");
        }

        using (BinaryReader header = new(new MemoryStream(mohd, writable: false)))
            HandleMOHD(header, root, (uint)mohd.Length);

        int groupCount = root.groups.Length;
        if (groupCount == 0 || mogi.Length != groupCount * 32 ||
            gfid.Length % sizeof(uint) != 0 || gfid.Length < groupCount * sizeof(uint) ||
            gfid.Length % (groupCount * sizeof(uint)) != 0)
        {
            throw new InvalidDataException($"WMO {fileDataId} has inconsistent group metadata.");
        }

        using (BinaryReader groupInfo = new(new MemoryStream(mogi, writable: false)))
            HandleMOGI(groupInfo, root, (uint)mogi.Length);

        uint[] groupIds = new uint[groupCount];
        using (BinaryReader groupIdReader = new(new MemoryStream(gfid, writable: false)))
        {
            for (int i = 0; i < groupCount; i++)
            {
                groupIds[i] = groupIdReader.ReadUInt32();
                if (groupIds[i] == 0)
                    throw new InvalidDataException($"WMO {fileDataId} full-detail group {i} has no FileDataID.");
            }
        }

        byte[] mods = chunks.GetValueOrDefault(ChunkReader.MODS) ?? [];
        byte[] modd = chunks.GetValueOrDefault(ChunkReader.MODD) ?? [];
        byte[] modn = chunks.GetValueOrDefault(ChunkReader.MODN) ?? [];
        byte[] modi = chunks.GetValueOrDefault(MODI) ?? [];
        if (mods.Length != checked((int)root.nDoodadSets * DoodadSetSize) ||
            modd.Length % DoodadDefinitionSize != 0 ||
            modi.Length % sizeof(uint) != 0)
        {
            throw new InvalidDataException($"WMO {fileDataId} has inconsistent doodad metadata.");
        }

        root.numericDoodadSets = new WmoDoodadSet[root.nDoodadSets];
        using (BinaryReader setReader = new(new MemoryStream(mods, writable: false)))
        {
            for (int i = 0; i < root.numericDoodadSets.Length; i++)
            {
                setReader.BaseStream.Position += 20; // set name
                uint first = setReader.ReadUInt32();
                uint count = setReader.ReadUInt32();
                setReader.BaseStream.Position += 4; // padding
                if ((ulong)first + count > (uint)(modd.Length / DoodadDefinitionSize))
                    throw new InvalidDataException($"WMO {fileDataId} doodad set {i} exceeds MODD.");
                root.numericDoodadSets[i] = new(first, count);
            }
        }

        uint[] modelIds = new uint[modi.Length / sizeof(uint)];
        using (BinaryReader idReader = new(new MemoryStream(modi, writable: false)))
        {
            for (int i = 0; i < modelIds.Length; i++)
                modelIds[i] = idReader.ReadUInt32();
        }

        // MOHD's doodad count can describe the model/name table rather than
        // actual MODD placements (e.g. 6,752 vs 6,106 in a Forever root).
        root.numericDoodads = new WmoDoodadPlacement[modd.Length / DoodadDefinitionSize];
        using (BinaryReader doodadReader = new(new MemoryStream(modd, writable: false)))
        {
            for (int i = 0; i < root.numericDoodads.Length; i++)
            {
                uint index = doodadReader.ReadUInt32() & DoodadNameIndexMask;
                Vector3 position = new(doodadReader.ReadSingle(), doodadReader.ReadSingle(), doodadReader.ReadSingle());
                float qw = doodadReader.ReadSingle();
                Quaternion rotation = new(doodadReader.ReadSingle(), doodadReader.ReadSingle(), doodadReader.ReadSingle(), qw);
                float scale = doodadReader.ReadSingle();
                doodadReader.BaseStream.Position += 4; // color

                uint modelId = 0;
                string? path = null;
                if (modelIds.Length > 0)
                {
                    if (index >= modelIds.Length || (modelId = modelIds[index]) == 0)
                        throw new InvalidDataException($"WMO {fileDataId} doodad {i} has no MODI model.");
                }
                else
                {
                    if (index >= modn.Length ||
                        (index > 0 && modn[index - 1] != 0) ||
                        modn.AsSpan((int)index).IndexOf((byte)0) < 0 ||
                        string.IsNullOrWhiteSpace(path = ChunkReader.ExtractString(modn, (int)index)))
                        throw new InvalidDataException($"WMO {fileDataId} doodad {i} has no MODN model.");
                }

                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                    !float.IsFinite(position.Z) || !float.IsFinite(scale) || scale <= 0 ||
                    !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) ||
                    !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W) ||
                    rotation.LengthSquared() < 0.01f)
                {
                    throw new InvalidDataException($"WMO {fileDataId} doodad {i} has an invalid transform.");
                }

                root.numericDoodads[i] = new(modelId, path, position, Quaternion.Normalize(rotation), scale);
            }
        }

        root.numericPlacement = true;
        return groupIds;
    }
}
