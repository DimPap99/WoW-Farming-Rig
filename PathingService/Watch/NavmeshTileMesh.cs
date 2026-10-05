using DotRecast.Detour;

using PPather.Navmesh;

using System.Collections.Generic;

namespace PathingService.Watch;

/// <summary>A Detour tile as a renderable triangle soup, ground and liquid apart.</summary>
public readonly record struct NavmeshTileMesh(float[] Positions, int[] Ground, int[] Liquid)
{
    /// <summary>The tile's mesh id in the 3D view; the continent keeps two continents' tiles apart.</summary>
    public static string Name(string continent, int x, int z) => $"navmesh_{continent}_{x}_{z}";

    /// <summary>The 3D view's suffixes for the two meshes a tile makes.</summary>
    public const string GroundSuffix = "_g";

    public const string LiquidSuffix = "_w";

    /// <summary>
    /// Fan-triangulates each polygon and splits the indices by area. Vertices go
    /// rc -> wow ({z, x, y}); the 3D view applies the same axis mapping as every
    /// other mesh.
    /// </summary>
    public static NavmeshTileMesh From(DtMeshData data)
    {
        int vertCount = data.header.vertCount;
        float[] positions = new float[vertCount * 3];

        for (int i = 0; i < vertCount; i++)
        {
            // rc (x=wowY, y=wowZ, z=wowX) -> wow (x, y, z)
            positions[(i * 3) + 0] = data.verts[(i * 3) + 2];
            positions[(i * 3) + 1] = data.verts[(i * 3) + 0];
            positions[(i * 3) + 2] = data.verts[(i * 3) + 1];
        }

        List<int> ground = [];
        List<int> liquid = [];

        for (int p = 0; p < data.header.polyCount; p++)
        {
            DtPoly poly = data.polys[p];
            List<int> target = poly.GetArea() == NavmeshSettings.AreaWater ? liquid : ground;

            for (int v = 2; v < poly.vertCount; v++)
            {
                target.Add(poly.verts[0]);
                target.Add(poly.verts[v - 1]);
                target.Add(poly.verts[v]);
            }
        }

        return new(positions, [.. ground], [.. liquid]);
    }
}
