#nullable enable
using DotRecast.Detour;
using DotRecast.Detour.Io;

using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;

using static DotRecast.Detour.DtDetour;

namespace PPather.Navmesh;

/// <summary>
/// A continent's navmesh as a graph of tile regions: each baked tile split into the parts its
/// polygons connect within the tile, joined where they link to a neighbouring tile's parts. It is
/// small next to the mesh (a node per region, not per polygon) and answers "which way across the
/// continent" in milliseconds, so a route too long or too winding for the search band around the
/// straight line can be searched along that way instead.
/// <para>
/// Built once per bake from the tile files - a sweep that holds at most three rows of tiles - and
/// kept beside them (<see cref="FileName"/>), keyed by the files' count, size and time, so it is
/// rebuilt only when the tiles change. The tile cache's own mesh is never touched.
/// </para>
/// </summary>
public sealed class NavmeshTileGraph
{
    public const int FormatVersion = 1;

    /// <summary>Where the graph is kept, beside the tiles it was built from.</summary>
    public const string FileName = "tilegraph.v1.bin";

    private const int Magic = 0x46524754; // "TGRF"
    private const ushort Unassigned = ushort.MaxValue;

    private readonly Dictionary<long, TileRegions> tiles;
    private readonly Vector3[] points;
    private readonly int[] firstEdge;
    private readonly int[] edgeTo;
    private readonly float[] edgeCost;

    private NavmeshTileGraph(Dictionary<long, TileRegions> tiles, Vector3[] points, int[] firstEdge, int[] edgeTo, float[] edgeCost)
    {
        this.tiles = tiles;
        this.points = points;
        this.firstEdge = firstEdge;
        this.edgeTo = edgeTo;
        this.edgeCost = edgeCost;
    }

    /// <summary>One tile: its first node, and which of its regions each polygon belongs to.</summary>
    private sealed record TileRegions(int FirstNode, ushort[] RegionOfPoly);

    public int TileCount => tiles.Count;

    public int NodeCount => points.Length;

    /// <summary>Joins between regions of neighbouring tiles, each counted once.</summary>
    public int EdgeCount => edgeTo.Length / 2;

    /// <summary>A walkable point of a region: the centre of its polygon nearest the region's middle, in world yards.</summary>
    public Vector3 PointOf(int node)
    {
        return points[node];
    }

    /// <summary>
    /// Every tile with the mean height of its regions' points: a coarse relief of the continent's
    /// walkable ground, one value per tile.
    /// </summary>
    public IEnumerable<(int TileX, int TileZ, float Height)> TileHeights()
    {
        foreach ((long key, TileRegions tile) in tiles)
        {
            int regions = 0;
            foreach (ushort region in tile.RegionOfPoly)
            {
                regions = Math.Max(regions, region + 1);
            }

            float sum = 0;
            for (int r = 0; r < regions; r++)
            {
                sum += points[tile.FirstNode + r].Z;
            }

            yield return ((int)(key >> 32), (int)key, regions > 0 ? sum / regions : 0);
        }
    }

    /// <summary>The region polygon <paramref name="polyIndex"/> of tile (<paramref name="tileX"/>, <paramref name="tileZ"/>) is in.</summary>
    public bool TryNode(int tileX, int tileZ, int polyIndex, out int node)
    {
        node = -1;
        if (!tiles.TryGetValue(Key(tileX, tileZ), out TileRegions? tile) ||
            (uint)polyIndex >= (uint)tile.RegionOfPoly.Length || tile.RegionOfPoly[polyIndex] == Unassigned)
        {
            return false;
        }

        node = tile.FirstNode + tile.RegionOfPoly[polyIndex];
        return true;
    }

    /// <summary>The regions a walk from <paramref name="from"/> to <paramref name="to"/> crosses (A*), or null when none connects them.</summary>
    public int[]? FindPath(int from, int to)
    {
        float[] cost = new float[points.Length];
        int[] parent = new int[points.Length];
        Array.Fill(cost, float.PositiveInfinity);
        Array.Fill(parent, -1);

        PriorityQueue<int, float> open = new();
        cost[from] = 0;
        open.Enqueue(from, Vector3.Distance(points[from], points[to]));
        Vector3 goal = points[to];

        while (open.TryDequeue(out int node, out float priority))
        {
            if (node == to)
            {
                List<int> path = [];
                for (int at = to; at >= 0; at = parent[at])
                {
                    path.Add(at);
                }

                path.Reverse();
                return [.. path];
            }

            // A stale entry: the node was reached more cheaply since it was queued.
            if (priority - Vector3.Distance(points[node], goal) > cost[node] + 0.01f)
            {
                continue;
            }

            for (int e = firstEdge[node]; e < firstEdge[node + 1]; e++)
            {
                int next = edgeTo[e];
                float reached = cost[node] + edgeCost[e];
                if (reached < cost[next])
                {
                    cost[next] = reached;
                    parent[next] = node;
                    open.Enqueue(next, reached + Vector3.Distance(points[next], goal));
                }
            }
        }

        return null;
    }

    /// <summary>The graph kept in <paramref name="cacheDir"/> when the tiles have not changed since, else null.</summary>
    public static NavmeshTileGraph? TryLoad(string cacheDir)
    {
        string file = Path.Combine(cacheDir, FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        TileSetStamp stamp = TileSetStamp.Of(cacheDir);
        using BinaryReader reader = new(File.OpenRead(file));
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion || TileSetStamp.Read(reader) != stamp)
        {
            return null;
        }

        int tileCount = reader.ReadInt32();
        Dictionary<long, TileRegions> tiles = new(tileCount);
        for (int i = 0; i < tileCount; i++)
        {
            int x = reader.ReadInt32();
            int z = reader.ReadInt32();
            int firstNode = reader.ReadInt32();
            ushort[] regionOfPoly = new ushort[reader.ReadInt32()];
            for (int p = 0; p < regionOfPoly.Length; p++)
            {
                regionOfPoly[p] = reader.ReadUInt16();
            }

            tiles[Key(x, z)] = new TileRegions(firstNode, regionOfPoly);
        }

        Vector3[] points = new Vector3[reader.ReadInt32()];
        for (int n = 0; n < points.Length; n++)
        {
            points[n] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        int[] firstEdge = new int[points.Length + 1];
        for (int n = 0; n < firstEdge.Length; n++)
        {
            firstEdge[n] = reader.ReadInt32();
        }

        int[] edgeTo = new int[reader.ReadInt32()];
        float[] edgeCost = new float[edgeTo.Length];
        for (int e = 0; e < edgeTo.Length; e++)
        {
            edgeTo[e] = reader.ReadInt32();
            edgeCost[e] = reader.ReadSingle();
        }

        return new NavmeshTileGraph(tiles, points, firstEdge, edgeTo, edgeCost);
    }

    /// <summary>Keeps the graph beside the tiles, written then renamed, so a reader never sees half of it.</summary>
    public void Save(string cacheDir, TileSetStamp stamp)
    {
        string file = Path.Combine(cacheDir, FileName);
        string temporary = file + ".tmp";
        using (BinaryWriter writer = new(File.Create(temporary)))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            stamp.Write(writer);

            writer.Write(tiles.Count);
            foreach ((long key, TileRegions tile) in tiles)
            {
                writer.Write((int)(key >> 32));
                writer.Write((int)key);
                writer.Write(tile.FirstNode);
                writer.Write(tile.RegionOfPoly.Length);
                foreach (ushort region in tile.RegionOfPoly)
                {
                    writer.Write(region);
                }
            }

            writer.Write(points.Length);
            foreach (Vector3 p in points)
            {
                writer.Write(p.X);
                writer.Write(p.Y);
                writer.Write(p.Z);
            }

            foreach (int first in firstEdge)
            {
                writer.Write(first);
            }

            writer.Write(edgeTo.Length);
            for (int e = 0; e < edgeTo.Length; e++)
            {
                writer.Write(edgeTo[e]);
                writer.Write(edgeCost[e]);
            }
        }

        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>
    /// Builds the graph from the tile files in <paramref name="cacheDir"/>: rows of tiles are loaded
    /// into a mesh of its own in order, so a tile's links to all four neighbours exist while it is
    /// read, and a row is dropped once the row after it has been read.
    /// </summary>
    public static NavmeshTileGraph Build(string cacheDir, ILogger logger, CancellationToken token = default)
    {
        long started = Stopwatch.GetTimestamp();
        SortedDictionary<int, List<int>> rows = [];
        foreach (string file in Directory.EnumerateFiles(cacheDir, NavmeshTileFiles.Pattern))
        {
            if (NavmeshTileFiles.TryTileOf(file, out int x, out int z))
            {
                if (!rows.TryGetValue(z, out List<int>? row))
                {
                    rows[z] = row = [];
                }

                row.Add(x);
            }
        }

        DtNavMesh mesh = new();
        DtNavMeshParams param = new()
        {
            orig = new Vector3(NavmeshCoords.Origin, 0, NavmeshCoords.Origin),
            tileWidth = NavmeshSettings.TileWorldSize,
            tileHeight = NavmeshSettings.TileWorldSize,
            maxTiles = NavmeshSettings.MaxTiles,
            maxPolys = NavmeshSettings.MaxPolysPerTile,
        };
        if (!mesh.Init(param, NavmeshSettings.VertsPerPoly).Succeeded())
        {
            throw new InvalidOperationException("DtNavMesh.Init failed");
        }

        Builder builder = new(mesh, logger);
        int[] zs = [.. rows.Keys];
        for (int i = 0; i < zs.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == 0)
            {
                builder.LoadRow(cacheDir, zs[0], rows[zs[0]]);
            }

            if (i + 1 < zs.Length)
            {
                builder.LoadRow(cacheDir, zs[i + 1], rows[zs[i + 1]]);
            }

            List<int> row = rows[zs[i]];
            row.Sort();
            foreach (int x in row)
            {
                builder.Read(x, zs[i]);
            }

            if (i > 0)
            {
                builder.UnloadRow(zs[i - 1], rows[zs[i - 1]]);
            }
        }

        NavmeshTileGraph graph = builder.Finish();
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Tile graph of {Dir}: {Tiles} tiles, {Nodes} regions, {Edges} joins in {Seconds:F1} s",
                cacheDir, graph.TileCount, graph.NodeCount, graph.EdgeCount, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }

        return graph;
    }

    private static long Key(int x, int z)
    {
        return ((long)x << 32) | (uint)z;
    }

    /// <summary>The sweep's state: the rows loaded, and the graph as it grows.</summary>
    private sealed class Builder(DtNavMesh mesh, ILogger logger)
    {
        private readonly Dictionary<long, long> loaded = [];
        private readonly Dictionary<long, TileRegions> tiles = [];
        private readonly List<Vector3> points = [];
        private readonly List<float> costs = [];
        private readonly HashSet<long> joins = [];
        private readonly DtMeshDataReader reader = new();

        public void LoadRow(string cacheDir, int z, List<int> xs)
        {
            foreach (int x in xs)
            {
                string file = NavmeshTileFiles.PathOf(cacheDir, x, z);
                try
                {
                    using BinaryReader stream = new(File.OpenRead(file));
                    DtMeshData data = reader.Read(stream, NavmeshSettings.VertsPerPoly);
                    if (mesh.AddTile(data, 0, 0, out long tileRef).Succeeded())
                    {
                        loaded[Key(x, z)] = tileRef;
                    }
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
                {
                    logger.LogWarning("Tile graph skips {File}: {Reason}", file, ex.Message);
                }
            }
        }

        public void UnloadRow(int z, List<int> xs)
        {
            foreach (int x in xs)
            {
                if (loaded.Remove(Key(x, z), out long tileRef))
                {
                    mesh.RemoveTile(tileRef);
                }
            }
        }

        /// <summary>
        /// A tile's regions, and its joins to the neighbours read before it (the row below and the
        /// tile to its left): each join is met once, from its later side.
        /// </summary>
        public void Read(int x, int z)
        {
            if (!loaded.TryGetValue(Key(x, z), out long tileRef))
            {
                return;
            }

            DtMeshTile tile = mesh.GetTileByRef(tileRef);
            int polyCount = tile.data.header.polyCount;
            ushort[] regionOf = new ushort[polyCount];
            Array.Fill(regionOf, Unassigned);

            int regions = 0;
            Stack<int> pending = new();
            for (int start = 0; start < polyCount; start++)
            {
                if (regionOf[start] != Unassigned)
                {
                    continue;
                }

                regionOf[start] = (ushort)regions;
                pending.Push(start);
                while (pending.Count > 0)
                {
                    int p = pending.Pop();
                    for (int l = tile.data.polys[p].firstLink; l != DT_NULL_LINK; l = tile.links[l].next)
                    {
                        long neighbour = tile.links[l].refs;
                        if (neighbour != 0 && DecodePolyIdTile(neighbour) == tile.index)
                        {
                            int q = DecodePolyIdPoly(neighbour);
                            if (regionOf[q] == Unassigned)
                            {
                                regionOf[q] = (ushort)regions;
                                pending.Push(q);
                            }
                        }
                    }
                }

                regions++;
            }

            int firstNode = points.Count;
            AddRegionPoints(tile, regionOf, regions);
            tiles[Key(x, z)] = new TileRegions(firstNode, regionOf);

            for (int p = 0; p < polyCount; p++)
            {
                for (int l = tile.data.polys[p].firstLink; l != DT_NULL_LINK; l = tile.links[l].next)
                {
                    long neighbour = tile.links[l].refs;
                    if (neighbour == 0 || DecodePolyIdTile(neighbour) == tile.index)
                    {
                        continue;
                    }

                    DtMeshTile other = mesh.GetTile(DecodePolyIdTile(neighbour));
                    if (other?.data?.header is null ||
                        !tiles.TryGetValue(Key(other.data.header.x, other.data.header.y), out TileRegions? read))
                    {
                        // Not read yet: the join is met again from that tile's side.
                        continue;
                    }

                    int a = firstNode + regionOf[p];
                    int b = read.FirstNode + read.RegionOfPoly[DecodePolyIdPoly(neighbour)];
                    joins.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a);
                }
            }
        }

        // Each region's point is the centre of its polygon nearest the region's middle: on the mesh by construction.
        private void AddRegionPoints(DtMeshTile tile, ushort[] regionOf, int regions)
        {
            Vector3[] sum = new Vector3[regions];
            int[] count = new int[regions];
            float[] cost = new float[regions];
            Vector3[] centers = new Vector3[regionOf.Length];
            for (int p = 0; p < regionOf.Length; p++)
            {
                DtPoly poly = tile.data.polys[p];
                Vector3 center = Vector3.Zero;
                for (int v = 0; v < poly.vertCount; v++)
                {
                    int i = poly.verts[v] * 3;
                    center += new Vector3(tile.data.verts[i], tile.data.verts[i + 1], tile.data.verts[i + 2]);
                }

                centers[p] = NavmeshCoords.ToWow(center / Math.Max(1, poly.vertCount));
                sum[regionOf[p]] += centers[p];
                count[regionOf[p]]++;
                cost[regionOf[p]] += AreaCost(poly.GetArea());
            }

            Vector3[] best = new Vector3[regions];
            float[] bestDistance = new float[regions];
            Array.Fill(bestDistance, float.PositiveInfinity);
            for (int p = 0; p < regionOf.Length; p++)
            {
                int r = regionOf[p];
                float distance = Vector3.DistanceSquared(centers[p], sum[r] / count[r]);
                if (distance < bestDistance[r])
                {
                    bestDistance[r] = distance;
                    best[r] = centers[p];
                }
            }

            for (int r = 0; r < regions; r++)
            {
                points.Add(best[r]);
                costs.Add(cost[r] / count[r]);
            }
        }

        // The search's own prices (WowQueryFilter): a region of water costs what swimming does.
        private static float AreaCost(int area)
        {
            return area switch
            {
                NavmeshSettings.AreaWater => WowQueryFilter.CostWater,
                NavmeshSettings.AreaMagmaSlime => WowQueryFilter.CostMagmaSlime,
                _ => WowQueryFilter.CostGround,
            };
        }

        public NavmeshTileGraph Finish()
        {
            int nodes = points.Count;
            int[] degree = new int[nodes + 1];
            foreach (long join in joins)
            {
                degree[(int)(join >> 32)]++;
                degree[(int)join]++;
            }

            int[] firstEdge = new int[nodes + 1];
            for (int n = 0; n < nodes; n++)
            {
                firstEdge[n + 1] = firstEdge[n] + degree[n];
            }

            int[] next = (int[])firstEdge.Clone();
            int[] edgeTo = new int[firstEdge[nodes]];
            float[] edgeCost = new float[edgeTo.Length];
            foreach (long join in joins)
            {
                int a = (int)(join >> 32);
                int b = (int)join;
                float cost = Vector3.Distance(points[a], points[b]) * ((costs[a] + costs[b]) / 2);
                edgeTo[next[a]] = b;
                edgeCost[next[a]++] = cost;
                edgeTo[next[b]] = a;
                edgeCost[next[b]++] = cost;
            }

            return new NavmeshTileGraph(tiles, [.. points], firstEdge, edgeTo, edgeCost);
        }
    }
}

/// <summary>What the tile files of a cache directory were when a graph was built from them: count, bytes and newest write.</summary>
public readonly record struct TileSetStamp(int Count, long Bytes, long NewestTicks)
{
    public static TileSetStamp Of(string cacheDir)
    {
        int count = 0;
        long bytes = 0;
        long newest = 0;
        foreach (string file in Directory.EnumerateFiles(cacheDir, NavmeshTileFiles.Pattern))
        {
            FileInfo info = new(file);
            count++;
            bytes += info.Length;
            newest = Math.Max(newest, info.LastWriteTimeUtc.Ticks);
        }

        return new TileSetStamp(count, bytes, newest);
    }

    public static TileSetStamp Read(BinaryReader reader)
    {
        return new TileSetStamp(reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt64());
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Count);
        writer.Write(Bytes);
        writer.Write(NewestTicks);
    }
}
