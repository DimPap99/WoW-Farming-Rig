using PPather;
using PPather.Navmesh;
using PPather.Triangles;

using System;
using System.Collections.Generic;
using System.Numerics;

using WowTriangles;

namespace PathingService.Watch;

/// <summary>
/// The game geometry under one navmesh tile, for the 3D view's terrain layers: what the tile was
/// baked from, split the way the old view drew it (terrain, liquid, WMO objects, M2 models) on
/// vertices shared within the tile. Laid over the navmesh it shows why the mesh looks as it does.
/// </summary>
/// <param name="Name">The tile's mesh id in the 3D view.</param>
/// <param name="Positions">x, y, z triples in world yards.</param>
public sealed record TerrainMesh(string Name, float[] Positions, int[] Terrain, int[] Liquid, int[] Objects, int[] Models)
{
    // Keeps the samples inside the tile, so a tile on an ADT edge does not pull in the next ADT.
    private const float Inset = 0.5f;

    public static string NameOf(string continent, int x, int z) => $"terrain_{continent}_{x}_{z}";

    public int TriangleCount => (Terrain.Length + Liquid.Length + Objects.Length + Models.Length) / 3;

    /// <summary>
    /// Every triangle of the ADTs under the tile that reaches into the tile's bounds. Loads those
    /// ADTs into <paramref name="world"/>'s cache; the caller lets them go afterwards.
    /// </summary>
    public static TerrainMesh Extract(ChunkedTriangleCollection world, string continent, int tileX, int tileZ)
    {
        NavmeshCoords.GetTileWowBounds(tileX, tileZ, out float minX, out float minY, out float maxX, out float maxY);

        // A navmesh tile is a sixteenth of an ADT; its corners reach every ADT it overlaps.
        List<TriangleCollection> chunks = [];
        foreach ((float x, float y) in (ReadOnlySpan<(float, float)>)
            [(minX + Inset, minY + Inset), (minX + Inset, maxY - Inset), (maxX - Inset, minY + Inset), (maxX - Inset, maxY - Inset)])
        {
            try
            {
                TriangleCollection chunk = world.GetChunkAt(x, y);
                if (!chunks.Contains(chunk))
                {
                    chunks.Add(chunk);
                }
            }
            catch (Exception) when (!world.RequiresCompleteGeometry)
            {
                // An ADT missing at the edge of the world: nothing to show there.
            }
        }

        List<float> positions = [];
        List<int> terrain = [];
        List<int> liquid = [];
        List<int> objects = [];
        List<int> models = [];

        foreach (TriangleCollection chunk in chunks)
        {
            ReadOnlySpan<Triangle<int>> triangles = chunk.TrianglesSpan;
            ReadOnlySpan<Vector3> vertices = chunk.VerteciesSpan;
            Dictionary<int, int> shared = [];

            for (int i = 0; i < triangles.Length; i++)
            {
                Triangle<int> t = triangles[i];
                Vector3 a = vertices[t.V0];
                Vector3 b = vertices[t.V1];
                Vector3 c = vertices[t.V2];

                if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < minX || MathF.Min(a.X, MathF.Min(b.X, c.X)) > maxX ||
                    MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < minY || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > maxY)
                {
                    continue;
                }

                List<int> layer = (t.Flags & TriangleType.Water) != 0 ? liquid
                    : (t.Flags & TriangleType.Model) != 0 ? models
                    : (t.Flags & TriangleType.Object) != 0 ? objects
                    : terrain;

                layer.Add(Index(t.V0, a));
                layer.Add(Index(t.V1, b));
                layer.Add(Index(t.V2, c));
            }

            int Index(int vertex, Vector3 at)
            {
                if (!shared.TryGetValue(vertex, out int index))
                {
                    index = positions.Count / 3;
                    shared[vertex] = index;
                    positions.Add(at.X);
                    positions.Add(at.Y);
                    positions.Add(at.Z);
                }

                return index;
            }
        }

        return new TerrainMesh(NameOf(continent, tileX, tileZ), [.. positions], [.. terrain], [.. liquid], [.. objects], [.. models]);
    }
}
