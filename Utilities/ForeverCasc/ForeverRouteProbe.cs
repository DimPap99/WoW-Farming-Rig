using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using DotRecast.Detour;
using Microsoft.Extensions.Logging.Abstractions;
using PPather;
using PPather.Navmesh;
using PPather.Triangles;
using WowTriangles;

namespace ForeverCasc;

/// <summary>Read-only, in-memory seam and path check using the installed Forever geometry.</summary>
internal static class ForeverRouteProbe
{
    private readonly record struct Sample(long PolyRef, Vector3 Rc, Vector3 Wow);

    public static void Run(ChunkedTriangleCollection world, CascGameFileSource source,
        MPQTriangleSupplier supplier, int tileX, int tileZ)
    {
        List<Vector3> objectFaceCenters = CheckWmoPlacement(world, source, tileX, tileZ);
        DtNavMesh mesh = new();
        DtNavMeshParams parameters = new()
        {
            orig = new Vector3(NavmeshCoords.Origin, 0, NavmeshCoords.Origin),
            tileWidth = NavmeshSettings.TileWorldSize,
            tileHeight = NavmeshSettings.TileWorldSize,
            maxTiles = 4,
            maxPolys = NavmeshSettings.MaxPolysPerTile,
        };
        if (!mesh.Init(parameters, NavmeshSettings.VertsPerPoly).Succeeded())
            throw new InvalidDataException("Could not initialize the in-memory navmesh.");

        AddTile(world, mesh, tileX, tileZ);
        AddTile(world, mesh, tileX + 1, tileZ);

        List<Sample> first = GetSamples(mesh, tileX, tileZ);
        List<Sample> second = GetSamples(mesh, tileX + 1, tileZ);
        Console.WriteLine($"Route candidates: {first.Count} + {second.Count} polygon centers.");

        DtNavMeshQuery query = new(mesh);
        WowQueryFilter filter = new();
        filter.BeginQuery();
        long[] corridor = new long[NavmeshPathfinder.MaxPolyPath];
        DtStraightPath[] straight = new DtStraightPath[NavmeshPathfinder.MaxStraightPath];
        float boundaryY = NavmeshCoords.Origin + ((tileX + 1) * NavmeshSettings.TileWorldSize);
        int attempts = 0;

        foreach (Sample from in first)
        {
            float fromGap = boundaryY - from.Wow.Y;
            int nearStart = CountNearbyFaces(objectFaceCenters, from.Wow);
            if (fromGap < 20f || fromGap > 70f || nearStart == 0)
                continue;

            foreach (Sample to in second)
            {
                float toGap = to.Wow.Y - boundaryY;
                if (toGap < 20f || toGap > 70f ||
                    MathF.Abs(to.Wow.X - from.Wow.X) > 20f ||
                    MathF.Abs(to.Wow.Z - from.Wow.Z) > 4f)
                    continue;

                attempts++;
                DtStatus status = query.FindPath(from.PolyRef, to.PolyRef, from.Rc, to.Rc,
                    filter, corridor, out int count, corridor.Length);
                if (!status.Succeeded() || status.IsPartial() || count < 2 || corridor[count - 1] != to.PolyRef)
                    continue;

                bool crosses = false;
                foreach (long polyRef in corridor.AsSpan(0, count))
                {
                    if (mesh.GetTileAndPolyByRef(polyRef, out DtMeshTile tile, out _).Succeeded() &&
                        tile.data.header.x == tileX + 1)
                        crosses = true;
                }
                if (!crosses)
                    continue;

                DtStatus straightStatus = query.FindStraightPath(from.Rc, to.Rc, corridor.AsSpan(0, count),
                    count, straight, out int pointCount, straight.Length, 0);
                if (!straightStatus.Succeeded() || straightStatus.IsPartial() || pointCount < 2)
                    throw new InvalidDataException("The connected corridor did not produce a complete straight path.");

                int nearEnd = CountNearbyFaces(objectFaceCenters, to.Wow);
                ChunkedTriangleCollection.GetMCNKGridCoord(from.Wow.X, from.Wow.Y,
                    out int adtX, out int adtY, out int mcnkX, out int mcnkY);
                TriangleCollection mcnk = new(NullLogger.Instance);
                supplier.GetMCNKTriangles(mcnk, adtX, adtY, mcnkX, mcnkY);
                int mcnkObjects = 0;
                int mcnkModels = 0;
                foreach (Triangle<int> face in mcnk.TrianglesSpan)
                {
                    if ((face.Flags & TriangleType.Object) != 0)
                        mcnkObjects++;
                    if ((face.Flags & TriangleType.Model) != 0)
                        mcnkModels++;
                }
                int nearMcnkWmo = CountNearbyFaces(mcnk, TriangleType.Object, from.Wow);
                int nearFullModels = CountNearbyFaces(world.GetChunkAt(-8800f, -200f),
                    TriangleType.Model, from.Wow);
                int nearMcnkModels = CountNearbyFaces(mcnk, TriangleType.Model, from.Wow);
                if (nearMcnkWmo != nearStart || nearFullModels == 0 ||
                    nearMcnkModels != nearFullModels)
                    throw new InvalidDataException("MCNK culling lost WMO or model collision near the route start.");

                Console.WriteLine($"Cross-tile path: {FormatPoint(from.Wow)} -> {FormatPoint(to.Wow)}; {count} polygons, {pointCount} straight points, {attempts} candidate pairs tested; WMO faces near endpoints: {nearStart}, {nearEnd}.");
                Console.WriteLine($"Start MCNK {adtX},{adtY}:{mcnkX},{mcnkY} contains {mcnkObjects} WMO and {mcnkModels} model faces; nearby full/MCNK: WMO {nearStart}/{nearMcnkWmo}, models {nearFullModels}/{nearMcnkModels}.");
                return;
            }
        }

        throw new InvalidDataException($"No complete cross-tile route found in {attempts} candidate pairs.");
    }

    private static string FormatPoint(Vector3 point) =>
        string.Create(CultureInfo.InvariantCulture, $"({point.X:F2}, {point.Y:F2}, {point.Z:F2})");

    private readonly record struct Bounds(Vector3 Min, Vector3 Max)
    {
        public bool Contains(Vector3 point, float tolerance) =>
            point.X >= Min.X - tolerance && point.X <= Max.X + tolerance &&
            point.Y >= Min.Y - tolerance && point.Y <= Max.Y + tolerance &&
            point.Z >= Min.Z - tolerance && point.Z <= Max.Z + tolerance;
    }

    private static int CountNearbyFaces(List<Vector3> centers, Vector3 point)
    {
        int count = 0;
        foreach (Vector3 center in centers)
        {
            if (MathF.Abs(center.X - point.X) <= 5f &&
                MathF.Abs(center.Y - point.Y) <= 5f &&
                MathF.Abs(center.Z - point.Z) <= 3f)
                count++;
        }
        return count;
    }

    private static int CountNearbyFaces(TriangleCollection chunk, TriangleType type, Vector3 point)
    {
        int count = 0;
        ReadOnlySpan<Vector3> vertices = chunk.VerteciesSpan;
        foreach (Triangle<int> triangle in chunk.TrianglesSpan)
        {
            if ((triangle.Flags & type) == 0)
                continue;
            Vector3 center = (vertices[triangle.V0] + vertices[triangle.V1] + vertices[triangle.V2]) / 3;
            if (MathF.Abs(center.X - point.X) <= 5f &&
                MathF.Abs(center.Y - point.Y) <= 5f &&
                MathF.Abs(center.Z - point.Z) <= 3f)
                count++;
        }
        return count;
    }

    private static List<Vector3> CheckWmoPlacement(ChunkedTriangleCollection world, CascGameFileSource source,
        int tileX, int tileZ)
    {
        List<Bounds> boxes = [];
        using Stream stream = source.OpenRead(@"World\Maps\Azeroth\Azeroth_32_48_obj0.adt");
        using BinaryReader reader = new(stream);
        while (stream.Position + 8 <= stream.Length)
        {
            string tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint size = reader.ReadUInt32();
            long end = stream.Position + size;
            if (end > stream.Length)
                throw new InvalidDataException($"Object ADT chunk {tag} exceeds file length.");
            if (tag == "FDOM")
            {
                if (size % 64 != 0)
                    throw new InvalidDataException("MODF chunk has partial WMO placement.");
                for (int i = 0; i < size / 64; i++)
                {
                    _ = reader.ReadUInt32(); // root FileDataID
                    _ = reader.ReadUInt32(); // unique instance ID
                    stream.Position += 24; // position and rotation
                    Vector3 a = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    Vector3 b = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    stream.Position += 8; // flags, doodad set, name set, scale
                    Vector3 wa = ToWowPlacement(a);
                    Vector3 wb = ToWowPlacement(b);
                    boxes.Add(new Bounds(Vector3.Min(wa, wb), Vector3.Max(wa, wb)));
                }
            }
            stream.Position = end;
        }
        if (boxes.Count == 0)
            throw new InvalidDataException("Sample object ADT has no WMO placements.");

        NavmeshCoords.GetTileWowBounds(tileX, tileZ, out float minX, out float minY,
            out float maxX, out float maxY);
        TriangleCollection chunk = world.GetChunkAt(-8800f, -200f);
        ReadOnlySpan<Vector3> vertices = chunk.VerteciesSpan;
        int objectFaces = 0;
        int covered = 0;
        List<Vector3> centers = [];
        foreach (Triangle<int> triangle in chunk.TrianglesSpan)
        {
            if ((triangle.Flags & TriangleType.Object) == 0)
                continue;
            Vector3 center = (vertices[triangle.V0] + vertices[triangle.V1] + vertices[triangle.V2]) / 3;
            if (center.X < minX || center.X >= maxX || center.Y < minY || center.Y >= maxY)
                continue;
            objectFaces++;
            centers.Add(center);
            foreach (Bounds box in boxes)
            {
                if (!box.Contains(center, 2f))
                    continue;
                covered++;
                break;
            }
        }
        Console.WriteLine($"WMO placement bounds: {boxes.Count} MODF records, {covered}/{objectFaces} object face centers enclosed.");
        if (objectFaces == 0 || covered != objectFaces)
            throw new InvalidDataException("Sample WMO collision faces fall outside their MODF placement bounds.");
        return centers;
    }

    private static Vector3 ToWowPlacement(Vector3 client) =>
        new(-NavmeshCoords.Origin - client.Z, -NavmeshCoords.Origin - client.X, client.Y);

    private static void AddTile(ChunkedTriangleCollection world, DtNavMesh mesh, int tileX, int tileZ)
    {
        TileGeometry geometry = TileGeometryExtractor.Extract(world, tileX, tileZ);
        if (geometry.IsEmpty || geometry.FailedChunkLoads != 0)
            throw new InvalidDataException($"Tile {tileX},{tileZ} has missing collision geometry.");

        DtMeshData? data = NavmeshTileBuilder.Bake(geometry, tileX, tileZ);
        if (data is null || data.header.polyCount == 0)
            throw new InvalidDataException($"Tile {tileX},{tileZ} produced no polygons.");

        DtStatus status = mesh.AddTile(data, 0, 0, out _);
        if (!status.Succeeded())
            throw new InvalidDataException($"Could not add tile {tileX},{tileZ}: {status.Value}.");

        Console.WriteLine($"Azeroth tile {tileX},{tileZ}: {geometry.GroundTriangleCount} ground faces, {data.header.polyCount} polygons.");
    }

    private static List<Sample> GetSamples(DtNavMesh mesh, int tileX, int tileZ)
    {
        DtMeshTile tile = mesh.GetTileByRef(mesh.GetTileRefAt(tileX, tileZ, 0));
        if (tile?.data?.header is null)
            throw new InvalidDataException($"Tile {tileX},{tileZ} is not resident.");

        List<Sample> samples = new(tile.data.header.polyCount);
        long baseRef = mesh.GetPolyRefBase(tile);
        for (int i = 0; i < tile.data.header.polyCount; i++)
        {
            long polyRef = baseRef | (uint)i;
            Vector3 rc = mesh.GetPolyCenter(polyRef);
            samples.Add(new Sample(polyRef, rc, NavmeshCoords.ToWow(rc)));
        }
        return samples;
    }
}
