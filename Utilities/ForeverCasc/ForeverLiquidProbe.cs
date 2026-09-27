using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using PPather;
using PPather.Navmesh;
using PPather.Triangles;
using SharedLib.Data;
using WowTriangles;

namespace ForeverCasc;

/// <summary>Read-only MH2O mask and layer check against two installed Azeroth ADTs.</summary>
internal static class ForeverLiquidProbe
{
    public static void Run(CascGameFileSource source)
    {
        if (source.ResolveLiquid(1240, 20335) is not { VertexFormat: 0, IsMagmaOrSlime: false, IsVisualOnly: false } ||
            source.ResolveLiquid(3, 1) is not { VertexFormat: 1, IsMagmaOrSlime: true, IsVisualOnly: false })
            throw new InvalidDataException("Forever liquid metadata did not classify water and magma correctly.");
        ContinentDB.IdToName[0] = "Azeroth";
        MPQTriangleSupplier supplier = new(NullLogger.Instance, source, 0);
        try
        {
            Vector3 wetPoint = Check(supplier, 32, 48, 2360);
            Check(supplier, 30, 50, 8182);
            ChunkedTriangleCollection world = new(NullLogger.Instance, 16, supplier);
            try
            {
                NavmeshCoords.GetTileIndex(wetPoint.X, wetPoint.Y, out int tileX, out int tileZ);
                TileGeometry geometry = TileGeometryExtractor.Extract(world, tileX, tileZ);
                if (geometry.FailedChunkLoads != 0 || geometry.LiquidTriangleCount == 0 ||
                    !Array.Exists(geometry.LiquidAreas, area => area == NavmeshSettings.AreaWater))
                    throw new InvalidDataException("Wet tile has no water area in navmesh geometry.");
                var mesh = NavmeshTileBuilder.Bake(geometry, tileX, tileZ);
                if (mesh is null || mesh.header.polyCount == 0)
                    throw new InvalidDataException("Wet tile did not produce a navmesh.");
                int waterPolygons = 0;
                foreach (var polygon in mesh.polys)
                    if ((polygon.flags & NavmeshSettings.FlagWater) != 0)
                        waterPolygons++;
                Console.WriteLine($"Wet navmesh tile {tileX},{tileZ}: {geometry.LiquidTriangleCount} liquid faces, {waterPolygons} water polygons.");
                if (waterPolygons == 0)
                    throw new InvalidDataException("Wet tile has no water polygons in its baked navmesh.");
            }
            finally
            {
                world.Close();
            }
        }
        finally
        {
            supplier.Clear();
        }
    }

    private static Vector3 Check(MPQTriangleSupplier supplier, int adtX, int adtY,
        int expectedLiquidFaces)
    {
        int liquidFaces = 0;
        int hazardFaces = 0;
        Vector3 wetPoint = default;
        for (int chunkX = 0; chunkX < 16; chunkX++)
        {
            for (int chunkY = 0; chunkY < 16; chunkY++)
            {
                TriangleCollection chunk = new(NullLogger.Instance);
                supplier.GetMCNKTriangles(chunk, adtX, adtY, chunkX, chunkY);
                ReadOnlySpan<Vector3> vertices = chunk.VerteciesSpan;
                foreach (Triangle<int> face in chunk.TrianglesSpan)
                {
                    if ((face.Flags & TriangleType.Water) == 0)
                        continue;
                    if (liquidFaces == 0)
                        wetPoint = (vertices[face.V0] + vertices[face.V1] + vertices[face.V2]) / 3;
                    liquidFaces++;
                    if ((face.Flags & TriangleType.MagmaSlime) != 0)
                        hazardFaces++;
                }
            }
        }
        Console.WriteLine($"Azeroth ADT {adtX}_{adtY}: {liquidFaces} liquid faces, {hazardFaces} magma/slime faces.");
        if (liquidFaces != expectedLiquidFaces)
            throw new InvalidDataException($"ADT {adtX}_{adtY}: expected {expectedLiquidFaces} liquid faces from the raw MH2O masks.");
        return wetPoint;
    }
}
