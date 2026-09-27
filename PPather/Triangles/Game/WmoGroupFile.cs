/*
  This file is part of ppather.

    PPather is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    PPather is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with ppather.  If not, see <http://www.gnu.org/licenses/>.

    Copyright Pontus Borg 2008

 */

using PPather.Extensions;
using PPather.Storage;

using StormDll;

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace Wmo;

public static class WmoGroupFile
{
    public static void Load(IGameFileSource archive, ReadOnlySpan<char> name, WMORoot wmoRoot, WMOGroup g)
    {
        Stream mpq;

        try
        {
            mpq = archive.OpenRead(name);
        }
        catch
        {
            // Possible files which were not found
            // lichking.mpq - world\wmo\northrend\buildings\forsaken\nd_forsaken_apothecary_004.wmo
            return;
        }

        // The native MPQ handle leaked whenever parsing threw; `using` releases
        // it on every path.
        using Stream _ = mpq;
        Load(mpq, wmoRoot, g);
    }

    /// <summary>Loads one full-detail group by its GFID FileDataID.</summary>
    public static void Load(IGameFileSource archive, uint fileDataId, WMORoot wmoRoot, WMOGroup g)
    {
        if (!archive.SupportsFileDataIds)
            throw new NotSupportedException("This game-file source has no FileDataID namespace.");

        using Stream stream = archive.OpenRead(fileDataId);
        Load(stream, wmoRoot, g);
    }

    private static void Load(Stream mpq, WMORoot wmoRoot, WMOGroup g)
    {
        var pooler = ArrayPool<byte>.Shared;
        int length = checked((int)mpq.Length);
        byte[] buffer = pooler.Rent(length);
        try
        {
            mpq.ReadExactly(buffer.AsSpan(0, length));

            using MemoryStream stream = new(buffer, 0, length, false);
            using BinaryReader file = new(stream);
            bool sawPolygons = false;
            bool sawIndices = false;

            do
            {
                uint type = file.ReadUInt32();
                uint size = file.ReadUInt32();

                if (type == ChunkReader.MOGP)
                    size = 68;

                long nextPos = file.BaseStream.Position + size;
                if (nextPos > stream.Length)
                    throw new InvalidDataException("WMO group chunk extends beyond the file.");

                switch (type)
                {
                    case ChunkReader.MOGP:
                        HandleMOGP(file, wmoRoot, g, size);
                        break;
                    case ChunkReader.MOPY:
                        sawPolygons = true;
                        HandleMOPY(file, g, size);
                        break;
                    case ChunkReader.MPY2:
                        sawPolygons = true;
                        HandleMPY2(file, g, size);
                        break;
                    case ChunkReader.MOVI:
                        sawIndices = true;
                        HandleMOVI(file, g, size);
                        break;
                    case ChunkReader.MOVT:
                        HandleMOVT(file, g, size);
                        break;
                    case ChunkReader.MLIQ:
                        HandleMLIQ(file, g, size);
                        break;
                }

                file.BaseStream.Seek(nextPos, SeekOrigin.Begin);
            } while (!file.EOF());

            if (sawIndices && !sawPolygons ||
                sawPolygons && g.nTriangles != 0 && !sawIndices ||
                sawIndices && g.triangles.Length != g.nTriangles * 3)
            {
                throw new InvalidDataException("WMO group is missing matching polygon flags or vertex indices.");
            }

            if (sawIndices && g.nTriangles != 0 && g.vertices is null)
                throw new InvalidDataException("WMO group has triangles but no vertices.");

            if (sawIndices && g.vertices is not null)
            {
                foreach (ushort index in g.triangles)
                {
                    if (index >= g.nVertices)
                        throw new InvalidDataException("WMO group triangle index exceeds the vertex count.");
                }
            }
        }
        finally
        {
            pooler.Return(buffer);
        }
    }

    // MopyFlags
    private static void HandleMOPY(BinaryReader file, WMOGroup g, uint size)
    {
        g.nTriangles = size / 2;
        g.materials = new ushort[g.nTriangles];
        file.BaseStream.ReadExactly(MemoryMarshal.Cast<ushort, byte>(g.materials.AsSpan()));
    }

    // 10.0+ MPY2 replaces each two-byte MOPY record with a uint16 flag and
    // uint16 material ID. The collision extractor uses only the flag bits.
    private static void HandleMPY2(BinaryReader file, WMOGroup g, uint size)
    {
        if (size % 4 != 0)
            throw new InvalidDataException("WMO MPY2 size is not a multiple of four bytes.");

        g.nTriangles = size / 4;
        g.materials = new ushort[g.nTriangles];
        for (int i = 0; i < g.materials.Length; i++)
        {
            g.materials[i] = file.ReadUInt16();
            _ = file.ReadUInt16(); // material ID, unused by collision extraction
        }
    }

    private static void HandleMOVI(BinaryReader file, WMOGroup g, uint size)
    {
        g.triangles = new ushort[g.nTriangles * 3];
        file.BaseStream.ReadExactly(MemoryMarshal.Cast<ushort, byte>(g.triangles.AsSpan()));
    }

    private static void HandleMOVT(BinaryReader file, WMOGroup g, uint size)
    {
        // let's hope it's padded to 12 bytes, not 16...

        g.nVertices = size / 12;
        g.vertices = new float[g.nVertices * 3];

        file.BaseStream.ReadExactly(MemoryMarshal.Cast<float, byte>(g.vertices.AsSpan()));
    }

    public static uint GetLiquidTypeId(uint mogpFlags, uint liquidTypeId)
    {
        if (liquidTypeId is < 21 and not 0)
        {
            switch ((liquidTypeId - 1) & 3)
            {
                case 0: return (((mogpFlags & 0x80000) != 0) ? 1U : 0U) + 13;
                case 1: return 14;
                case 2: return 19;
                case 3: return 20;
                default: break;
            }
        }
        return liquidTypeId;
    }

    private static void HandleMLIQ(BinaryReader file, WMOGroup g, uint size)
    {
        g.liquflags |= 1;

        Span<byte> buffer = stackalloc byte[Marshal.SizeOf<WMOLiquidHeader>()];
        int readSize = file.Read(buffer);
        g.hlq = MemoryMarshal.Read<WMOLiquidHeader>(buffer);

        g.LiquEx_size = Marshal.SizeOf<WMOLiquidVert>() * g.hlq.xverts * g.hlq.yverts;
        g.LiquEx = new WMOLiquidVert[g.hlq.xverts * g.hlq.yverts];

        Span<byte> byteSpan = MemoryMarshal.Cast<WMOLiquidVert, byte>(g.LiquEx.AsSpan());
        file.Read(byteSpan);

        int nLiquBytes = g.hlq.xtiles * g.hlq.ytiles;
        g.LiquBytes = new char[nLiquBytes];
        file.Read(MemoryMarshal.Cast<char, byte>(g.LiquBytes.AsSpan()));

        if (g.groupLiquid == 0)
        {
            for (int i = 0; i < g.hlq.xtiles * g.hlq.ytiles; ++i)
            {
                if ((g.LiquBytes[i] & 0xF) != 15)
                {
                    g.groupLiquid = GetLiquidTypeId(g.mogpFlags, (uint)(g.LiquBytes[i] & 0xF) + 1);
                    break;
                }
            }
        }
    }

    private static void HandleMOGP(BinaryReader file, WMORoot wmoRoot, WMOGroup g, uint size)
    {
        g.nameStart = file.ReadUInt32();
        g.nameStart2 = file.ReadUInt32();
        g.mogpFlags = file.ReadUInt32();

        g.v1 = file.ReadVector3();
        g.v2 = file.ReadVector3();

        g.portalStart = file.ReadUInt16();
        g.portalCount = file.ReadUInt16();

        g.batchesA = file.ReadUInt16();
        g.batchesB = file.ReadUInt16();
        g.batchesC = file.ReadUInt32();

        g.fogIdx = file.ReadUInt32();
        g.groupLiquid = file.ReadUInt32();

        g.id = file.ReadUInt32();

        // according to WoW.Dev Wiki:
        if ((wmoRoot.flags & 4) == 0)
            g.groupLiquid = GetLiquidTypeId(g.mogpFlags, g.groupLiquid);
        else if (g.groupLiquid == 15)
            g.groupLiquid = 0;
        else
            g.groupLiquid = GetLiquidTypeId(g.mogpFlags, g.groupLiquid + 1);

        if (g.groupLiquid != 0)
            g.liquflags |= 2;
    }
}
