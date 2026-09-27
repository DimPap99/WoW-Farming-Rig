using PPather.Storage;

using System;
using System.IO;

namespace Wmo;

/// <summary>
/// Reads the numeric asset references in a CASC-era WMO root without loading
/// its potentially thousands of group meshes and doodad models. The first
/// GFID group-count entries are the full-detail groups used for collision;
/// subsequent entries are LODs and must not be mixed into a navmesh.
/// </summary>
public sealed class WmoFileDataIdReferences
{
    private const uint Gfid = 0x47464944;
    private const uint Modi = 0x4D4F4449;
    private const int DoodadDefinitionSize = 40;
    private const uint DoodadNameIndexMask = 0x00FFFFFF;

    public uint[] FullDetailGroupIds { get; private set; } = [];
    public uint[] DoodadModelIds { get; private set; } = [];
    public uint[] DoodadPlacementModelIndexes { get; private set; } = [];
    public int GroupCount => FullDetailGroupIds.Length;
    public int LodCount { get; private set; }

    public static WmoFileDataIdReferences Read(IGameFileSource source, uint fileDataId)
    {
        if (!source.SupportsFileDataIds)
            throw new NotSupportedException("This game-file source has no FileDataID namespace.");

        using Stream stream = source.OpenRead(fileDataId);
        return Read(stream);
    }

    public static WmoFileDataIdReferences Read(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("WMO root stream must be readable and seekable.", nameof(stream));

        WmoFileDataIdReferences result = new();
        using BinaryReader reader = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        int groupCount = -1;
        uint[] groupIds = [];
        bool sawGfid = false;
        bool sawModi = false;
        bool sawModn = false;
        bool sawModd = false;
        byte[] modn = [];

        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8)
                throw new InvalidDataException("WMO root ends inside a chunk header.");

            uint tag = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            long end = stream.Position + size;
            if (end > stream.Length)
                throw new InvalidDataException("WMO root chunk extends beyond the file.");

            switch (tag)
            {
                case ChunkReader.MOHD:
                    if (size < 8 || groupCount >= 0)
                        throw new InvalidDataException("Invalid or duplicate WMO MOHD chunk.");
                    reader.ReadUInt32(); // material count
                    groupCount = checked((int)reader.ReadUInt32());
                    break;
                case Gfid:
                    if ((size & 3) != 0 || sawGfid)
                        throw new InvalidDataException("Invalid or duplicate WMO GFID chunk.");
                    sawGfid = true;
                    groupIds = new uint[checked((int)(size / 4))];
                    for (int i = 0; i < groupIds.Length; i++)
                        groupIds[i] = reader.ReadUInt32();
                    break;
                case Modi:
                    if ((size & 3) != 0 || sawModi)
                        throw new InvalidDataException("Invalid or duplicate WMO MODI chunk.");
                    sawModi = true;
                    result.DoodadModelIds = new uint[checked((int)(size / 4))];
                    for (int i = 0; i < result.DoodadModelIds.Length; i++)
                        result.DoodadModelIds[i] = reader.ReadUInt32();
                    break;
                case ChunkReader.MODN:
                    if (sawModn)
                        throw new InvalidDataException("Duplicate WMO MODN chunk.");
                    sawModn = true;
                    modn = reader.ReadBytes(checked((int)size));
                    break;
                case ChunkReader.MODD:
                    if (size % DoodadDefinitionSize != 0 || sawModd)
                        throw new InvalidDataException("Invalid or duplicate WMO MODD chunk.");
                    sawModd = true;
                    result.DoodadPlacementModelIndexes = new uint[checked((int)(size / DoodadDefinitionSize))];
                    for (int i = 0; i < result.DoodadPlacementModelIndexes.Length; i++)
                    {
                        result.DoodadPlacementModelIndexes[i] = reader.ReadUInt32() & DoodadNameIndexMask;
                        stream.Position += DoodadDefinitionSize - sizeof(uint);
                    }
                    break;
            }

            stream.Position = end;
        }

        if (groupCount < 0 || groupIds.Length == 0 || groupCount == 0 || groupIds.Length % groupCount != 0)
            throw new InvalidDataException("WMO GFID groups do not match MOHD group count.");

        result.FullDetailGroupIds = groupIds.AsSpan(0, groupCount).ToArray();
        result.LodCount = groupIds.Length / groupCount;
        for (int i = 0; i < result.FullDetailGroupIds.Length; i++)
        {
            if (result.FullDetailGroupIds[i] == 0)
                throw new InvalidDataException($"WMO full-detail group {i} has no FileDataID.");
        }

        if (sawModd && result.DoodadPlacementModelIndexes.Length != 0 &&
            result.DoodadModelIds.Length == 0 && modn.Length == 0)
            throw new InvalidDataException("WMO has doodad placements but no MODI IDs or MODN names.");

        foreach (uint index in result.DoodadPlacementModelIndexes)
        {
            if (result.DoodadModelIds.Length > 0)
            {
                if (index >= result.DoodadModelIds.Length || result.DoodadModelIds[index] == 0)
                    throw new InvalidDataException($"WMO doodad references absent MODI entry {index}.");
            }
            else if (index >= modn.Length ||
                     (index > 0 && modn[index - 1] != 0) ||
                     modn.AsSpan((int)index).IndexOf((byte)0) < 0 ||
                     string.IsNullOrWhiteSpace(ChunkReader.ExtractString(modn, (int)index)))
            {
                throw new InvalidDataException($"WMO doodad references absent MODN name at {index}.");
            }
        }

        return result;
    }
}
