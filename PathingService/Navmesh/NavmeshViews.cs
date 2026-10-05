using Contracts.Pathing;

using DotRecast.Detour;

using PPather.Navmesh;
using PPather.Planning;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace PathingService.Navmesh;

/// <summary>
/// The navmesh as a map draws it: a continent's relief from its tile graph, and one tile's
/// polygons straight from its file. Read-only, and never through a planner lane, so drawing
/// costs the planning nothing.
/// </summary>
public static class NavmeshViews
{
    public static NavmeshOverview Overview(RoutePlanner planner, string continent)
    {
        NavmeshTileGraph graph = planner.TileGraph(continent);
        List<int> tiles = new(graph.TileCount * 2);
        List<float> heights = new(graph.TileCount);
        foreach ((int tileX, int tileZ, float height) in graph.TileHeights())
        {
            tiles.Add(tileX);
            tiles.Add(tileZ);
            heights.Add(Round(height));
        }

        return new NavmeshOverview(continent, NavmeshCoords.Origin, NavmeshSettings.TileWorldSize, [.. tiles], [.. heights]);
    }

    /// <summary>The tile's polygons in world yards; null when it is not baked or cannot be read.</summary>
    public static NavmeshTileGeometry? Tile(string cacheDir, int tileX, int tileZ)
    {
        DtMeshData? data;
        try
        {
            data = NavmeshTileFiles.Read(cacheDir, tileX, tileZ);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }

        if (data is null)
        {
            return null;
        }

        float[] vertices = new float[data.header.vertCount * 3];
        for (int v = 0; v < data.header.vertCount; v++)
        {
            Vector3 wow = NavmeshCoords.ToWow(new Vector3(data.verts[v * 3], data.verts[(v * 3) + 1], data.verts[(v * 3) + 2]));
            vertices[v * 3] = Round(wow.X);
            vertices[(v * 3) + 1] = Round(wow.Y);
            vertices[(v * 3) + 2] = Round(wow.Z);
        }

        List<int> polygons = [];
        List<int> water = [];
        for (int p = 0; p < data.header.polyCount; p++)
        {
            DtPoly poly = data.polys[p];
            if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                continue;
            }

            polygons.Add(poly.vertCount);
            for (int v = 0; v < poly.vertCount; v++)
            {
                polygons.Add(poly.verts[v]);
            }

            water.Add(poly.GetArea() == NavmeshSettings.AreaWater ? 1 : 0);
        }

        return new NavmeshTileGeometry(tileX, tileZ, vertices, [.. polygons], [.. water]);
    }

    // A tenth of a yard is finer than a map can show, and keeps the numbers short on the wire.
    private static float Round(float value)
    {
        return MathF.Round(value, 1);
    }
}
