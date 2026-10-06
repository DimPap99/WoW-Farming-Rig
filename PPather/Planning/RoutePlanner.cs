#nullable enable
using DotRecast.Detour;

using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace PPather.Planning;

/// <summary>
/// Plans routes for many callers at once over one client's baked navmesh.
/// <para>
/// Each continent gets a <b>lane</b>: a disk-only <see cref="NavmeshPathfinder"/>, its cost
/// zones and a gate. A query takes only its own continent's gate, so callers on different
/// continents run in parallel and never evict each other's engine, which is what
/// <see cref="PPatherService"/> does when its single active continent changes. Every query
/// brings fresh <see cref="NavmeshQueryMemory"/> and its own <see cref="NavmeshQueryTuning"/>,
/// so one caller's floor, corridor or jitter cannot leak into another's route.
/// </para>
/// <para>
/// It plans the way the bot's <c>LocalPathingApi</c> does on a disk-only install: the same
/// pathfinder, the same height rule for endpoints given without one, and no
/// <c>startIndoors</c> (the bot's own value never reaches its pathfinder; see
/// docs/service-architecture.md, "Findings"). So a route previewed through the planner is
/// the route the bot computes. It never bakes; an endpoint on a missing tile answers
/// <see cref="RoutePlanStatus.NotBaked"/>.
/// </para>
/// <para>
/// It can be watched: <see cref="RoutePlanned"/> reports every answer in world yards, and
/// <see cref="TileLoaded"/>/<see cref="TileUnloaded"/> the tiles each lane holds. Handlers run on
/// the querying or tile-loading thread and must only hand the work on.
/// </para>
/// </summary>
public sealed class RoutePlanner : IDisposable
{
    /// <summary>How long a query waits for its continent's lane before answering <see cref="RoutePlanStatus.Busy"/>.</summary>
    public static readonly TimeSpan DefaultLaneWait = TimeSpan.FromSeconds(10);

    // A lane closed while a query waited for it is replaced by a fresh one this many times at most.
    private const int ClosedLaneRetries = 2;

    private readonly ILogger logger;
    private readonly DataConfig dataConfig;
    private readonly WorldMapAreaDB maps;
    private readonly NavmeshBakeOptions bakeOptions;
    private readonly NavmeshQueryOptions queryOptions;
    private readonly TimeSpan laneWait;

    private readonly Lock lanesLock = new();
    private readonly Dictionary<string, Lane> lanes = [];
    private readonly HashSet<string> reportedUnbaked = [];

    private readonly Lock areaGridLock = new();
    private readonly Dictionary<string, AreaGrid?> areaGrids = [];

    private readonly Lock tileGraphLock = new();
    private readonly Dictionary<string, NavmeshTileGraph> tileGraphs = [];

    private readonly CostZoneWatcher? zoneWatcher;

    public RoutePlanner(ILogger logger, DataConfig dataConfig, WorldMapAreaDB maps,
        NavmeshBakeOptions bakeOptions, NavmeshQueryOptions queryOptions, TimeSpan? laneWait = null)
    {
        this.logger = logger;
        this.dataConfig = dataConfig;
        this.maps = maps;
        this.bakeOptions = bakeOptions;
        this.queryOptions = queryOptions;
        this.laneWait = laneWait ?? DefaultLaneWait;

        ContinentDB.Init(maps.Values);
        zoneWatcher = CostZoneWatcher.TryStart(logger, dataConfig.Road, ReloadCostZonesFromDisk);
    }

    /// <summary>The client whose data this planner serves (<see cref="DataConfig.Exp"/>).</summary>
    public string Client => dataConfig.Exp;

    /// <summary>The navmesh era of <see cref="Client"/>.</summary>
    public string Era => DataConfig.ClientEra(dataConfig.Exp);

    /// <summary>Navmesh routes are smoothed and validated, so callers re-request rather than patch them.</summary>
    public const bool PathsAreSmoothed = true;

    /// <summary>Every answer of a query that reached a continent, in world yards; raised on the querying thread.</summary>
    public event Action<PlannedRoute>? RoutePlanned;

    /// <summary>A lane stitched a tile into its mesh; raised on a tile worker thread.</summary>
    public event Action<LaneTile>? TileLoaded;

    /// <summary>A lane evicted a tile; <see cref="LaneTile.Data"/> is null.</summary>
    public event Action<LaneTile>? TileUnloaded;

    public RouteOutcome Find(in RouteQuery query, CancellationToken token = default)
    {
        long start = Stopwatch.GetTimestamp();

        // A query holds its continent's lane for as long as its tuning makes it work.
        if (query.Tuning.Problem() is string refused)
        {
            return Fail(RoutePlanStatus.Refused, string.Empty, start, refused);
        }

        if (!maps.TryGet(query.UiMapId, out WorldMapArea area) ||
            !ContinentDB.IdToName.TryGetValue(area.MapID, out string? continent))
        {
            return Fail(RoutePlanStatus.UnknownMap, string.Empty, start,
                $"uiMap {query.UiMapId} is not in the {Client} map data");
        }

        Vector3 from = query.Space == RouteSpace.Map ? MapToWorld(area, query.From, query.From.Z) : query.From;
        Vector3 to = query.Space == RouteSpace.Map ? MapToWorld(area, query.To, 0) : query.To;

        for (int attempt = 0; ; attempt++)
        {
            if (GetLane(continent) is not Lane lane)
            {
                return Planned(Fail(RoutePlanStatus.NotBaked, continent, start,
                    $"{continent} has no baked navmesh in {NavmeshSettings.CacheDirectory(dataConfig, bakeOptions, continent)}"),
                    from, to, []);
            }

            if (!lane.HasTile(from))
            {
                return Planned(Fail(RoutePlanStatus.NotBaked, continent, start,
                    $"the start ({from.X:F1}, {from.Y:F1}) is on a tile of {continent} with no baked navmesh"), from, to, []);
            }

            if (!lane.HasTile(to))
            {
                return Planned(Fail(RoutePlanStatus.NotBaked, continent, start,
                    $"the destination ({to.X:F1}, {to.Y:F1}) is on a tile of {continent} with no baked navmesh"), from, to, []);
            }

            if (query.Reach == RouteReach.Continent)
            {
                return FindAcross(lane, continent, area, query, from, to, start, token);
            }

            if (!lane.Gate.Wait(laneWait, token))
            {
                return Fail(RoutePlanStatus.Busy, continent, start,
                    $"{continent} stayed busy for {laneWait.TotalSeconds:F0} s");
            }

            Graph.Path? path;
            Vector3 resolvedFrom;
            Vector3 resolvedTo;
            NavmeshPathfinder.NavmeshStats stats;
            try
            {
                // A bake or a cache clear closed this lane while the query waited for it; the
                // next GetLane opens a fresh one on what is on disk now.
                if (lane.Closed && attempt < ClosedLaneRetries)
                {
                    continue;
                }

                if (lane.Closed)
                {
                    return Fail(RoutePlanStatus.Busy, continent, start, $"{continent} kept being reopened; ask again");
                }

                // The caller may have gone while it waited (a client's attempt timed out): the lane
                // goes to the next query, not to a search nobody will read.
                token.ThrowIfCancellationRequested();

                NavmeshPathfinder nav = lane.Pathfinder;
                resolvedFrom = WithSurfaceHeight(nav, from);
                resolvedTo = WithSurfaceHeight(nav, to);

                path = nav.FindPath(resolvedFrom, resolvedTo, null, nav.CreateMemory(), query.Tuning, out stats);
                lane.Served(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            finally
            {
                lane.Gate.Release();
            }

            if (path == null || path.locations.Count == 0)
            {
                return Planned(Fail(RoutePlanStatus.NoPath, continent, start,
                    $"the {continent} navmesh connects no route between the points"), resolvedFrom, resolvedTo, []);
            }

            Vector3[] world = path.locations.ToArray();
            Vector3[] points = world;
            if (query.Space == RouteSpace.Map)
            {
                points = new Vector3[world.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = WorldToMap(world[i], area);
                }
            }

            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            // A search that found no way on still returns the part it found: say so, rather
            // than hand that part over as the route.
            if (stats.EndGapYd > ArrivalToleranceYd)
            {
                float band = stats.CorridorRadius * NavmeshSettings.TileWorldSize;
                return Planned(new(RoutePlanStatus.Partial, points, continent, elapsedMs,
                        StoppedShort(stats.EndGapYd, points[^1], query.Space,
                            string.Create(CultureInfo.InvariantCulture, $"the search found no way on within {band:F0} yd of the straight line"))),
                    resolvedFrom, resolvedTo, world);
            }

            return Planned(new(RoutePlanStatus.Found, points, continent, elapsedMs, null), resolvedFrom, resolvedTo, world);
        }
    }

    /// <summary>
    /// How far from the destination a route may end and still have reached it. A route that
    /// arrives ends on the destination the search resolved, so anything beyond this stopped short.
    /// </summary>
    public const float ArrivalToleranceYd = 5f;

    /// <summary>
    /// How far a leg of a continent route may stray from the way the tile graph found: well inside
    /// the band a leg's search loads around it, so the way stays in reach.
    /// </summary>
    public const float LegToleranceYd = 100f;

    /// <summary>The longest leg of a continent route: each leg is one ordinary band search.</summary>
    public const float MaxLegYd = 1500f;

    // Where a partial route stops, in the coordinates the caller asked in, and why it stops there.
    private static string StoppedShort(float gapYd, Vector3 last, RouteSpace space, string why)
    {
        string where = space == RouteSpace.Map
            ? string.Create(CultureInfo.InvariantCulture, $"({last.X:F1}, {last.Y:F1})")
            : string.Create(CultureInfo.InvariantCulture, $"({last.X:F1}, {last.Y:F1}, {last.Z:F1})");
        return string.Create(CultureInfo.InvariantCulture, $"stops {gapYd:F0} yd short of the destination, at {where}: {why}");
    }

    /// <summary>
    /// A route across the whole continent: the way across is found on the continent's tile graph,
    /// cut into legs that stay close to it (<see cref="Legs"/>), and each leg is an ordinary band
    /// search from where the last one ended. The lane is held per leg, not for the whole route, so
    /// other callers get their turns.
    /// </summary>
    private RouteOutcome FindAcross(Lane lane, string continent, WorldMapArea area, in RouteQuery query, Vector3 from, Vector3 to,
        long start, CancellationToken token)
    {
        NavmeshTileGraph graph = TileGraph(continent);
        NavmeshQueryMemory memory = lane.Pathfinder.CreateMemory();
        NavmeshQueryTuning tuning = query.Tuning;

        Vector3 resolvedFrom = from;
        Vector3 resolvedTo = to;
        int startNode = -1;
        int endNode = -1;
        if (!OnLane(lane, token, nav =>
            {
                resolvedFrom = WithSurfaceHeight(nav, from);
                resolvedTo = WithSurfaceHeight(nav, to);
                startNode = NodeOf(graph, nav, resolvedFrom);
                endNode = NodeOf(graph, nav, resolvedTo);
            }))
        {
            return Fail(RoutePlanStatus.Busy, continent, start, $"{continent} stayed busy for {laneWait.TotalSeconds:F0} s");
        }

        if (startNode < 0 || endNode < 0)
        {
            return Planned(Fail(RoutePlanStatus.NoPath, continent, start,
                $"the {(startNode < 0 ? "start" : "destination")} is on no walkable surface of the {continent} navmesh"),
                resolvedFrom, resolvedTo, []);
        }

        if (graph.FindPath(startNode, endNode) is not int[] regions)
        {
            return Planned(Fail(RoutePlanStatus.NoPath, continent, start,
                $"the {continent} navmesh connects no way between the points, across the whole continent"), resolvedFrom, resolvedTo, []);
        }

        List<Vector3> way = new(regions.Length + 1) { resolvedFrom };
        for (int i = 1; i < regions.Length - 1; i++)
        {
            way.Add(graph.PointOf(regions[i]));
        }

        way.Add(resolvedTo);

        List<Vector3> world = [];
        string? stopped = null;
        foreach (Vector3 legEnd in Legs(way).Skip(1))
        {
            Vector3 legStart = world.Count > 0 ? world[^1] : resolvedFrom;
            Graph.Path? leg = null;
            NavmeshPathfinder.NavmeshStats stats = default;
            if (!OnLane(lane, token, nav => leg = nav.FindPath(legStart, legEnd, null, memory, tuning, out stats)))
            {
                return Fail(RoutePlanStatus.Busy, continent, start, $"{continent} stayed busy for {laneWait.TotalSeconds:F0} s");
            }

            if (leg is not null && leg.locations.Count > 0)
            {
                world.AddRange(world.Count > 0 ? leg.locations.Skip(1) : leg.locations);
            }

            if (leg is null || leg.locations.Count == 0 || stats.EndGapYd > ArrivalToleranceYd)
            {
                stopped = "the search along the way across the continent found no way on there";
                break;
            }
        }

        lane.Served(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (world.Count == 0)
        {
            return Planned(Fail(RoutePlanStatus.NoPath, continent, start,
                $"the {continent} navmesh connects no route between the points"), resolvedFrom, resolvedTo, []);
        }

        Vector3[] worldPoints = [.. world];
        Vector3[] points = worldPoints;
        if (query.Space == RouteSpace.Map)
        {
            points = new Vector3[worldPoints.Length];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = WorldToMap(worldPoints[i], area);
            }
        }

        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        float gap = Vector3.Distance(worldPoints[^1], resolvedTo);
        return stopped is null && gap <= ArrivalToleranceYd
            ? Planned(new(RoutePlanStatus.Found, points, continent, elapsedMs, null), resolvedFrom, resolvedTo, worldPoints)
            : Planned(new(RoutePlanStatus.Partial, points, continent, elapsedMs,
                StoppedShort(gap, points[^1], query.Space, stopped ?? "the last leg ends away from the destination")),
                resolvedFrom, resolvedTo, worldPoints);
    }

    /// <summary>
    /// The way across, cut into straight legs (Douglas-Peucker): every point of the way stays within
    /// <see cref="LegToleranceYd"/> of its leg, and no leg is longer than <see cref="MaxLegYd"/>. The
    /// first and last points are kept.
    /// </summary>
    public static List<Vector3> Legs(IReadOnlyList<Vector3> way)
    {
        bool[] keep = new bool[way.Count];
        keep[0] = true;
        keep[^1] = true;
        Split(way, 0, way.Count - 1, keep);

        List<Vector3> legs = [];
        for (int i = 0; i < way.Count; i++)
        {
            if (keep[i])
            {
                legs.Add(way[i]);
            }
        }

        return legs;
    }

    private static void Split(IReadOnlyList<Vector3> way, int first, int last, bool[] keep)
    {
        if (last - first < 2)
        {
            return;
        }

        int farthest = first;
        float deviation = 0;
        for (int i = first + 1; i < last; i++)
        {
            float d = DistanceToSegment2D(way[i], way[first], way[last]);
            if (d > deviation)
            {
                deviation = d;
                farthest = i;
            }
        }

        if (deviation <= LegToleranceYd)
        {
            if (Vector2.Distance(new(way[first].X, way[first].Y), new(way[last].X, way[last].Y)) <= MaxLegYd)
            {
                return;
            }

            // Straight but long: halved, so each leg stays one ordinary search.
            farthest = (first + last) / 2;
        }

        keep[farthest] = true;
        Split(way, first, farthest, keep);
        Split(way, farthest, last, keep);
    }

    private static float DistanceToSegment2D(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector2 p = new(point.X, point.Y);
        Vector2 start = new(a.X, a.Y);
        Vector2 segment = new Vector2(b.X, b.Y) - start;
        float length = segment.LengthSquared();
        float t = length > 0 ? Math.Clamp(Vector2.Dot(p - start, segment) / length, 0, 1) : 0;
        return Vector2.Distance(p, start + (t * segment));
    }

    // The graph node of the polygon a route from or to this point would use; -1 when none.
    private static int NodeOf(NavmeshTileGraph graph, NavmeshPathfinder nav, Vector3 wow)
    {
        return nav.TryLocatePoly(wow, out int tileX, out int tileZ, out int poly) && graph.TryNode(tileX, tileZ, poly, out int node)
            ? node
            : -1;
    }

    // Work on the lane's pathfinder under its gate; false when the lane stayed busy or closed meanwhile.
    // Throws when the caller has gone, even once the gate is taken, as Find does.
    private bool OnLane(Lane lane, CancellationToken token, Action<NavmeshPathfinder> work)
    {
        if (!lane.Gate.Wait(laneWait, token))
        {
            return false;
        }

        try
        {
            if (lane.Closed)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            work(lane.Pathfinder);
            return true;
        }
        finally
        {
            lane.Gate.Release();
        }
    }

    /// <summary>
    /// The continent's tile graph: kept in memory once read, read from beside the tiles when they
    /// have not changed since it was built, else built from them (about 10 s for Kalimdor) and kept
    /// there. One build at a time; a caller that needs it meanwhile waits for it.
    /// </summary>
    public NavmeshTileGraph TileGraph(string continent)
    {
        lock (tileGraphLock)
        {
            if (tileGraphs.TryGetValue(continent, out NavmeshTileGraph? graph))
            {
                return graph;
            }

            string dir = NavmeshSettings.CacheDirectory(dataConfig, bakeOptions, continent);
            try
            {
                graph = NavmeshTileGraph.TryLoad(dir);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
            {
                logger.LogWarning("The tile graph of {Continent} could not be read ({Reason}); it is built again", continent, ex.Message);
            }

            if (graph is null)
            {
                TileSetStamp stamp = TileSetStamp.Of(dir);
                graph = NavmeshTileGraph.Build(dir, logger);
                try
                {
                    graph.Save(dir, stamp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("The tile graph of {Continent} was built but not kept ({Reason}); it is built again next start",
                        continent, ex.Message);
                }
            }

            tileGraphs[continent] = graph;
            return graph;
        }
    }

    /// <summary>
    /// Area id and walkable height at a world point, from the baked area grid and navmesh -
    /// no game files involved. Zeros for what is not baked, as
    /// <see cref="PPatherService.GetAreaIdAndZ"/> answers, and for a point beyond the edge of
    /// the world (<see cref="NavmeshCoords.InWorld"/>), which is never queried.
    /// </summary>
    public (int AreaId, float Z) GetAreaAndHeight(int mapId, float worldX, float worldY)
    {
        if (!NavmeshCoords.InWorld(worldX, worldY) ||
            !ContinentDB.IdToName.TryGetValue(mapId, out string? continent))
        {
            return (0, 0f);
        }

        int areaId = GetAreaGrid(continent)?.GetAreaId(worldX, worldY) ?? 0;

        float z = 0f;
        if (GetLane(continent) is Lane lane && lane.Gate.Wait(laneWait))
        {
            try
            {
                if (!lane.Closed && lane.Pathfinder.TryGetHeight(worldX, worldY, out float found))
                {
                    z = found;
                }
            }
            finally
            {
                lane.Gate.Release();
            }
        }

        return (areaId, z);
    }

    /// <summary>
    /// Re-reads the authored roads and danger zones of every open lane; takes effect on the
    /// next query, with no rebake. All or nothing per continent: a file caught mid-write
    /// keeps that continent's working set, and the answer is false.
    /// </summary>
    public bool ReloadCostZones()
    {
        return ReloadCostZones(null, out _);
    }

    /// <summary>
    /// Re-reads the authored roads and danger zones of the open lane of <paramref name="continent"/>,
    /// or of every open lane. All or nothing per continent: a continent whose files do not all load
    /// keeps its working set, and <paramref name="failure"/> names the first file that failed and
    /// why. A continent with no open lane has nothing to reload: it reads its files when it opens.
    /// </summary>
    public bool ReloadCostZones(string? continent, out string? failure)
    {
        failure = null;
        foreach (Lane lane in OpenLanes())
        {
            if (continent is not null && !lane.Continent.Equals(continent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // One reload at a time per lane, from its read to its swap, so the one that read
            // last applies last. The gate alone would not: it does not serve waiters in order.
            lock (lane.ReloadLock)
            {
                if (!CostZoneLoader.TryLoad(logger, dataConfig.Road, lane.Continent, out CostZones zones, out string? failed,
                        roadCoreHalfWidth: queryOptions.RoadCore))
                {
                    logger.LogWarning(
                        "Cost zones for {Continent} were not reloaded: {Failure}. Previous zones stay in effect.",
                        lane.Continent, failed);
                    failure ??= failed;
                    continue;
                }

                // Swapped between queries, never under one.
                lane.Gate.Wait();
                try
                {
                    if (!lane.Closed)
                    {
                        lane.Pathfinder.Zones = zones;
                    }
                }
                finally
                {
                    lane.Gate.Release();
                }
            }
        }

        return failure is null;
    }

    /// <summary>
    /// Closes the lane of <paramref name="continent"/>, or every lane, so the next query opens a
    /// fresh one on what is on disk then. A lane remembers a tile that was missing when it looked
    /// as empty for its whole life, so this is what makes tiles baked, downloaded or deleted while
    /// the planner runs count. A query in flight finishes first; one waiting for the closed lane
    /// moves to the new one.
    /// </summary>
    public void CloseLanes(string? continent = null)
    {
        List<Lane> closing = [];
        lock (lanesLock)
        {
            foreach (Lane lane in lanes.Values)
            {
                if (continent is null || lane.Continent.Equals(continent, StringComparison.OrdinalIgnoreCase))
                {
                    closing.Add(lane);
                }
            }

            foreach (Lane lane in closing)
            {
                lanes.Remove(lane.Continent);
                reportedUnbaked.Remove(lane.Continent);
            }
        }

        // The tiles may have changed: the next continent route reads the graph again, and its
        // stamp tells whether it must be rebuilt.
        lock (tileGraphLock)
        {
            if (continent is null)
            {
                tileGraphs.Clear();
            }
            else
            {
                tileGraphs.Remove(continent);
            }
        }

        foreach (Lane lane in closing)
        {
            lane.Gate.Wait();
            try
            {
                lane.Close();
            }
            finally
            {
                lane.Gate.Release();
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Route planner lane closed for {Continent}; the next query reopens it", lane.Continent);
            }
        }
    }

    /// <summary>
    /// Forgets the area grid of <paramref name="continent"/>, or every one, including one that was
    /// missing, so a grid extracted since is read on the next lookup.
    /// </summary>
    public void ForgetAreaGrids(string? continent = null)
    {
        lock (areaGridLock)
        {
            if (continent is null)
            {
                areaGrids.Clear();
            }
            else
            {
                areaGrids.Remove(continent);
            }
        }
    }

    /// <summary>
    /// The tiles the lane of <paramref name="continent"/> holds right now, with their mesh data, for
    /// a viewer that opens late. Empty when the continent has no open lane or it stays busy.
    /// </summary>
    public IReadOnlyList<LaneTile> ResidentTiles(string continent)
    {
        Lane? lane;
        lock (lanesLock)
        {
            lanes.TryGetValue(continent, out lane);
        }

        if (lane is null || !lane.Gate.Wait(laneWait))
        {
            return [];
        }

        List<LaneTile> result = [];
        try
        {
            if (lane.Closed)
            {
                return result;
            }

            // The gate keeps queries and closing out; the cache's read lock keeps its
            // background workers from adding or evicting a tile mid-walk.
            NavmeshTileCache tiles = lane.Pathfinder.Tiles;
            tiles.Lock.EnterReadLock();
            try
            {
                foreach ((int x, int z) in tiles.ResidentTiles)
                {
                    if (tiles.NavMesh.GetTileByRef(tiles.NavMesh.GetTileRefAt(x, z, 0))?.data is DtMeshData data)
                    {
                        result.Add(new LaneTile(lane.Continent, x, z, data));
                    }
                }
            }
            finally
            {
                tiles.Lock.ExitReadLock();
            }
        }
        finally
        {
            lane.Gate.Release();
        }

        return result;
    }

    /// <summary>The pathfinder of the open lane of <paramref name="continent"/>; null without one.</summary>
    internal NavmeshPathfinder? LanePathfinder(string continent)
    {
        lock (lanesLock)
        {
            return lanes.TryGetValue(continent, out Lane? lane) && !lane.Closed ? lane.Pathfinder : null;
        }
    }

    /// <summary>The tiles the lane of <paramref name="continent"/> holds, by index; empty without a lane.</summary>
    public IReadOnlySet<(int X, int Z)> ResidentTileSet(string continent)
    {
        lock (lanesLock)
        {
            return lanes.TryGetValue(continent, out Lane? lane) && !lane.Closed
                ? new HashSet<(int X, int Z)>(lane.Pathfinder.Tiles.ResidentTiles)
                : new HashSet<(int X, int Z)>();
        }
    }

    /// <summary>Baked tiles per continent on disk for this client's era. Counts files only.</summary>
    public PPatherService.NavmeshCoverage[] Coverage()
    {
        return PPatherService.GetNavmeshCoverage(dataConfig, bakeOptions);
    }

    /// <summary>One open lane, for a status page.</summary>
    public readonly record struct LaneStatus(string Continent, int ResidentTiles, long Queries, double LastMs);

    public IReadOnlyList<LaneStatus> Lanes()
    {
        List<LaneStatus> result = [];
        foreach (Lane lane in OpenLanes())
        {
            result.Add(new(lane.Continent, lane.Pathfinder.Tiles.ResidentTileCount, lane.Queries, lane.LastMs));
        }

        return result;
    }

    public void Dispose()
    {
        zoneWatcher?.Dispose();

        lock (lanesLock)
        {
            foreach (Lane lane in lanes.Values)
            {
                lane.Dispose();
            }
            lanes.Clear();
        }
    }

    // A continent with nothing baked gets no lane, and is looked at again on the next
    // query: tiles downloaded or baked while the planner runs are picked up without a
    // restart.
    private Lane? GetLane(string continent)
    {
        lock (lanesLock)
        {
            if (lanes.TryGetValue(continent, out Lane? lane))
            {
                return lane;
            }

            string cacheDir = NavmeshSettings.CacheDirectory(dataConfig, bakeOptions, continent);
            if (!Directory.Exists(cacheDir))
            {
                if (reportedUnbaked.Add(continent))
                {
                    logger.LogWarning(
                        "No baked navmesh for {Continent} in {CacheDir}; routes there answer NotBaked until tiles are downloaded or baked.",
                        continent, cacheDir);
                }
                return null;
            }

            NavmeshPathfinder nav = new(logger, null, cacheDir, bakeOptions, queryOptions,
                bakeOptions.ResolveMinWorldZ(continent));
            nav.Zones = CostZoneLoader.Load(logger, dataConfig.Road, continent,
                roadCoreHalfWidth: queryOptions.RoadCore);
            nav.Tiles.NotifyTileAdded = (x, z, data) => Raise(TileLoaded, new LaneTile(continent, x, z, data));
            nav.Tiles.NotifyTileRemoved = (x, z) => Raise(TileUnloaded, new LaneTile(continent, x, z, null));

            lane = new Lane(continent, nav);
            lanes[continent] = lane;
            reportedUnbaked.Remove(continent);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Route planner lane open for {Continent} ({Era}) on {CacheDir}",
                    continent, Era, cacheDir);
            }

            return lane;
        }
    }

    private List<Lane> OpenLanes()
    {
        lock (lanesLock)
        {
            return [.. lanes.Values];
        }
    }

    private AreaGrid? GetAreaGrid(string continent)
    {
        lock (areaGridLock)
        {
            if (areaGrids.TryGetValue(continent, out AreaGrid? grid))
            {
                return grid;
            }

            string path = System.IO.Path.Combine(dataConfig.AreaGrid, continent + ".grid");
            grid = AreaGrid.Load(path);
            areaGrids[continent] = grid;

            if (grid == null)
            {
                logger.LogWarning("Area grid missing for {Continent} ({Path}) - area ids unavailable.", continent, path);
            }

            return grid;
        }
    }

    private void ReloadCostZonesFromDisk()
    {
        if (OpenLanes().Count > 0 && ReloadCostZones())
        {
            logger.LogInformation("Cost zones reloaded from disk.");
        }
    }

    // A height of 0 means none was given: take the walkable surface under the point, as
    // PPatherService.NavmeshWorldLocation does for a continent without game files. A point
    // the mesh does not cover keeps 0 and the resolver deals with it.
    private static Vector3 WithSurfaceHeight(NavmeshPathfinder nav, Vector3 point)
    {
        if (point.Z != 0f)
        {
            return point;
        }

        return nav.TryGetHeight(point.X, point.Y, out float z)
            ? new Vector3(point.X, point.Y, z)
            : point;
    }

    // The same flip as PPatherService.ToWorld: map x is world y and map y is world x.
    private static Vector3 MapToWorld(in WorldMapArea area, Vector3 map, float z)
    {
        return new Vector3(area.ToWorldX(map.Y), area.ToWorldY(map.X), z);
    }

    // Every point in the query's own zone, the frame its start and destination were given in, so a
    // route that strays into a neighbouring zone, or into a gap only the continent's map holds,
    // keeps one frame and can be turned back into world yards (docs/known-issues.md, item 2). The
    // bot's map route still takes each point in the zone that holds it (PPatherService.ToLocal);
    // the two agree while a route stays in its zone.
    private static Vector3 WorldToMap(Vector3 world, in WorldMapArea area)
    {
        return WorldMapAreaDB.ToMap_FlipXY(world, area);
    }

    private static RouteOutcome Fail(RoutePlanStatus status, string continent, long start, string detail)
    {
        return new(status, [], continent, Stopwatch.GetElapsedTime(start).TotalMilliseconds, detail);
    }

    // Tells the watchers, then hands the outcome back unchanged.
    private RouteOutcome Planned(RouteOutcome outcome, Vector3 from, Vector3 to, Vector3[] world)
    {
        Raise(RoutePlanned, new PlannedRoute(outcome.Continent, from, to, world, outcome.Status, outcome.ElapsedMs, outcome.Detail));
        return outcome;
    }

    // A watcher must never break a query or a tile load.
    private void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A route planner watcher failed");
        }
    }

    private sealed class Lane(string continent, NavmeshPathfinder pathfinder) : IDisposable
    {
        private long queries;
        private long lastMsBits;
        private volatile bool closed;

        public string Continent { get; } = continent;
        public NavmeshPathfinder Pathfinder { get; } = pathfinder;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Held by a cost-zone reload from its read to its swap.</summary>
        public Lock ReloadLock { get; } = new();

        /// <summary>Taken out of service; a query that still holds it moves to a fresh lane.</summary>
        public bool Closed => closed;

        public long Queries => Interlocked.Read(ref queries);
        public double LastMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref lastMsBits));

        /// <summary>Releases the mesh. The caller holds the gate; the gate itself stays usable for waiters.</summary>
        public void Close()
        {
            closed = true;
            Pathfinder.Tiles.NotifyTileAdded = null;
            Pathfinder.Tiles.NotifyTileRemoved = null;
            Pathfinder.Dispose();
        }

        public bool HasTile(Vector3 wow)
        {
            NavmeshCoords.GetTileIndex(wow.X, wow.Y, out int tx, out int tz);
            return Pathfinder.Tiles.IsTileOnDisk(tx, tz);
        }

        public void Served(double elapsedMs)
        {
            Interlocked.Increment(ref queries);
            Interlocked.Exchange(ref lastMsBits, BitConverter.DoubleToInt64Bits(elapsedMs));
        }

        public void Dispose()
        {
            Pathfinder.Dispose();
            Gate.Dispose();
        }
    }
}
