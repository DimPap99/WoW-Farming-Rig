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

using StormDll;
using PPather.Storage;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static System.Buffers.Binary.BinaryPrimitives;

namespace Wmo;

public static class ModelFile
{
    [SkipLocalsInit]
    public static Model Read(IGameFileSource archive, ReadOnlySpan<char> fileName) =>
        Read(archive.OpenRead(fileName), includeVisualVertices: true);

    [SkipLocalsInit]
    public static Model Read(IGameFileSource archive, uint fileDataId) =>
        Read(archive.OpenRead(fileDataId), includeVisualVertices: true);

    public static Model ReadCollision(IGameFileSource archive, ReadOnlySpan<char> fileName) =>
        Read(archive.OpenRead(fileName), includeVisualVertices: false);

    public static Model ReadCollision(IGameFileSource archive, uint fileDataId) =>
        Read(archive.OpenRead(fileDataId), includeVisualVertices: false);

    [SkipLocalsInit]
    private static Model Read(System.IO.Stream input, bool includeVisualVertices)
    {
        using System.IO.Stream mpq = input;
        int length = (int)mpq.Length;

        var pooler = ArrayPool<byte>.Shared;
        byte[] array = null;

        Span<byte> stream = length <= MpqFileStream.MaxStackLimit
            ? stackalloc byte[length]
            : (array = pooler.Rent(length)).AsSpan(0, length);

        mpq.ReadExactly(stream);

        // Modern CASC M2s wrap the otherwise compatible MD20 payload in MD21.
        // All offsets below are relative to MD20, not the outer wrapper.
        const uint MD20 = 0x3032444D;
        const uint MD21 = 0x3132444D;
        if (stream.Length >= 12 && ReadUInt32LittleEndian(stream) == MD21)
            stream = stream[8..];
        if (stream.Length < 12 || ReadUInt32LittleEndian(stream) != MD20)
            throw new System.IO.InvalidDataException("Expected MD20 model payload.");

        ReadOnlySpan<byte> begining = stream;

        stream = stream[((sizeof(byte) * 4) + (sizeof(UInt32) * 14))..];

        uint nVertices = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        uint ofsVertices = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        stream = stream[((sizeof(UInt32) * 23) + (sizeof(Single) * 14))..];

        uint nBoundingTriangles = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        uint ofsBoundingTriangles = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        uint nBoundingVertices = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        uint ofsBoundingVertices = ReadUInt32LittleEndian(stream);
        stream = stream[sizeof(UInt32)..];

        stream = stream[(sizeof(UInt32) * 18)..];

        // `begining` aliases the rented array, so the reads below must happen
        // before it goes back to the pool.
        Model model = new(
            includeVisualVertices ? ReadVertices(begining, nVertices, ofsVertices) : [],
            ReadBoundingTriangles(begining, nBoundingTriangles, ofsBoundingTriangles),
            ReadBoundingVertices(begining, nBoundingVertices, ofsBoundingVertices));

        if (array != null)
            pooler.Return(array);

        return model;
    }

    [SkipLocalsInit]
    private static float[] ReadBoundingVertices(ReadOnlySpan<byte> stream, uint nVertices, uint ofsVertices)
    {
        if (nVertices == 0)
            return [];

        ReadOnlySpan<byte> vertexData = stream.Slice((int)ofsVertices, (int)nVertices * 3 * sizeof(float));
        float[] output = new float[nVertices * 3];

        MemoryMarshal.Cast<byte, float>(vertexData).CopyTo(output);
        return output;
    }

    [SkipLocalsInit]
    private static ushort[] ReadBoundingTriangles(ReadOnlySpan<byte> stream, uint nTriangles, uint ofsTriangles)
    {
        if (nTriangles == 0)
            return [];

        ReadOnlySpan<byte> triangleData = stream.Slice((int)ofsTriangles, (int)(nTriangles * sizeof(ushort)));

        ushort[] output = new ushort[nTriangles];
        MemoryMarshal.Cast<byte, ushort>(triangleData).CopyTo(output.AsSpan());

        return output;
    }

    [SkipLocalsInit]
    private static float[] ReadVertices(ReadOnlySpan<byte> stream, uint nVertices, uint ofsVertices)
    {
        float[] output = new float[nVertices * 3];

        ReadOnlySpan<byte> verticesData = stream[(int)ofsVertices..];

        const int stride = (sizeof(UInt32) * 2) + (sizeof(float) * 7);
        const int vertexCoordsSize = sizeof(float) * 3;

        for (int i = 0; i < nVertices; i++)
        {
            int offset = i * stride;

            ReadOnlySpan<byte> vertexBytes = verticesData.Slice(offset, vertexCoordsSize);
            MemoryMarshal.Cast<byte, float>(vertexBytes)
                         .CopyTo(output.AsSpan(i * 3, 3));
        }
        return output;
    }
}
