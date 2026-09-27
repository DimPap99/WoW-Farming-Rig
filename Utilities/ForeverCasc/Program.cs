using System;
using System.IO;
using System.Numerics;
using ForeverCasc;
using Microsoft.Extensions.Logging.Abstractions;
using PPather;
using PPather.Navmesh;
using SharedLib.Data;
using WowTriangles;

if (args.Length < 3 || args.Length > 5)
{
    Console.Error.WriteLine("Usage: ForeverCasc <install-dir> <geometry-listfile> <file-data-id-or-path|--wdt-azeroth|--sample-bake-azeroth|--sample-route-azeroth|--sample-liquid-azeroth> [product] [cache-dir]");
    return 2;
}

string product = args.Length >= 4 ? args[3] : "wow_classic_beta";
string cache = args.Length >= 5 ? args[4] : Path.Combine(Path.GetTempPath(), "WoWFarmingRig", "forever-casc");

try
{
    using CascGameFileSource source = new(args[0], product, args[1], cache);
    if (args[2] == "--wdt-azeroth")
    {
        ContinentDB.IdToName[0] = "Azeroth";
        MPQTriangleSupplier supplier = new(NullLogger.Instance, source, 0);
        bool hasSampleTile = supplier.HasAdtAt(-8800, -267);
        supplier.Clear();
        if (!hasSampleTile)
            throw new InvalidDataException("Azeroth WDT does not mark sample tile 32_48 present.");
        Console.WriteLine("Azeroth WDT parsed through PPather; sample tile 32_48 is present.");
        return 0;
    }

    if (args[2] == "--sample-liquid-azeroth")
    {
        ForeverLiquidProbe.Run(source);
        return 0;
    }

    if (args[2] is "--sample-bake-azeroth" or "--sample-route-azeroth")
    {
        ContinentDB.IdToName[0] = "Azeroth";
        MPQTriangleSupplier supplier = new(NullLogger.Instance, source, 0);
        ChunkedTriangleCollection world = new(NullLogger.Instance, 16, supplier);
        try
        {
            NavmeshCoords.GetTileIndex(-8800, -200, out int tileX, out int tileZ);
            if (args[2] == "--sample-route-azeroth")
            {
                ForeverRouteProbe.Run(world, source, supplier, tileX, tileZ);
                return 0;
            }
            TileGeometry geometry = TileGeometryExtractor.Extract(world, tileX, tileZ);
            if (geometry.IsEmpty || geometry.FailedChunkLoads != 0)
                throw new InvalidDataException("Sample navmesh tile has missing collision geometry.");

            var mesh = NavmeshTileBuilder.Bake(geometry, tileX, tileZ);
            if (mesh is null || mesh.header.polyCount == 0)
                throw new InvalidDataException("Sample navmesh tile produced no walkable polygons.");

            NavmeshCoords.GetTileWowBounds(tileX, tileZ, out float minX, out float minY, out float maxX, out float maxY);
            TriangleCollection sampleChunk = world.GetChunkAt(-8800f, -200f);
            int objects = 0, models = 0;
            ReadOnlySpan<Vector3> vertices = sampleChunk.VerteciesSpan;
            foreach (var triangle in sampleChunk.TrianglesSpan)
            {
                Vector3 v0 = vertices[triangle.V0], v1 = vertices[triangle.V1], v2 = vertices[triangle.V2];
                if (MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < minX ||
                    MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > maxX ||
                    MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < minY ||
                    MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > maxY)
                    continue;

                if ((triangle.Flags & TriangleType.Object) != 0) objects++;
                if ((triangle.Flags & TriangleType.Model) != 0) models++;
            }
            if (objects == 0)
                throw new InvalidDataException("Sample tile did not include WMO collision faces.");

            Console.WriteLine($"Azeroth navmesh tile {tileX},{tileZ}: {geometry.GroundTriangleCount} ground triangles, {geometry.LiquidTriangleCount} liquid triangles, {objects} WMO faces, {models} model faces, {mesh.header.polyCount} polygons.");
            return 0;
        }
        finally
        {
            world.Close();
        }
    }

    using Stream stream = uint.TryParse(args[2], out uint id)
        ? source.OpenRead(id)
        : source.OpenRead(args[2]);

    Console.WriteLine($"{args[2]}: {stream.Length} bytes, readable={stream.CanRead}, seekable={stream.CanSeek}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CASC read failed: {ex}");
    return 1;
}
