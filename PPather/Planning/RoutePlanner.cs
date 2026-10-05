#nullable enable
using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
/// </summary>
public sealed class RoutePlanner : IDisposable
{
    /// <summary>How long a query waits for its continent's lane before answering <see cref="RoutePlanStatus.Busy"/>.</summary>
    public static readonly TimeSpan DefaultLaneWait = TimeSpan.FromSeconds(10);

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

    public RouteOutcome Find(in RouteQuery query, CancellationToken token = default)
    {
        long start = Stopwatch.GetTimestamp();

        if (!maps.TryGet(query.UiMapId, out WorldMapArea area) ||
            !ContinentDB.IdToName.TryGetValue(area.MapID, out string? continent))
        {
            return Fail(RoutePlanStatus.UnknownMap, string.Empty, start,
                $"uiMap {query.UiMapId} is not in the {Client} map data");
        }

        if (GetLane(continent) is not Lane lane)
        {
            return Fail(RoutePlanStatus.NotBaked, continent, start,
                $"{continent} has no baked navmesh in {NavmeshSettings.CacheDirectory(dataConfig, bakeOptions, continent)}");
        }

        Vector3 from = query.Space == RouteSpace.Map ? MapToWorld(area, query.From, query.From.Z) : query.From;
        Vector3 to = query.Space == RouteSpace.Map ? MapToWorld(area, query.To, 0) : query.To;

        if (!lane.HasTile(from))
        {
            return Fail(RoutePlanStatus.NotBaked, continent, start,
                $"the start ({from.X:F1}, {from.Y:F1}) is on a tile of {continent} with no baked navmesh");
        }

        if (!lane.HasTile(to))
        {
            return Fail(RoutePlanStatus.NotBaked, continent, start,
                $"the destination ({to.X:F1}, {to.Y:F1}) is on a tile of {continent} with no baked navmesh");
        }

        if (!lane.Gate.Wait(laneWait, token))
        {
            return Fail(RoutePlanStatus.Busy, continent, start,
                $"{continent} stayed busy for {laneWait.TotalSeconds:F0} s");
        }

        Graph.Path? path;
        try
        {
            NavmeshPathfinder nav = lane.Pathfinder;
            from = WithSurfaceHeight(nav, from);
            to = WithSurfaceHeight(nav, to);

            path = nav.FindPath(from, to, null, nav.CreateMemory(), query.Tuning, out _);
            lane.Served(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        finally
        {
            lane.Gate.Release();
        }

        if (path == null || path.locations.Count == 0)
        {
            return Fail(RoutePlanStatus.NoPath, continent, start, $"the {continent} navmesh connects no route between the points");
        }

        Vector3[] points = path.locations.ToArray();
        if (query.Space == RouteSpace.Map)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = WorldToMap(points[i], area);
            }
        }

        return new(RoutePlanStatus.Found, points, continent, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null);
    }

    /// <summary>
    /// Area id and walkable height at a world point, from the baked area grid and navmesh -
    /// no game files involved. Zeros for what is not baked, as
    /// <see cref="PPatherService.GetAreaIdAndZ"/> answers.
    /// </summary>
    public (int AreaId, float Z) GetAreaAndHeight(int mapId, float worldX, float worldY)
    {
        if (!ContinentDB.IdToName.TryGetValue(mapId, out string? continent))
        {
            return (0, 0f);
        }

        int areaId = GetAreaGrid(continent)?.GetAreaId(worldX, worldY) ?? 0;

        float z = 0f;
        if (GetLane(continent) is Lane lane && lane.Gate.Wait(laneWait))
        {
            try
            {
                if (lane.Pathfinder.TryGetHeight(worldX, worldY, out float found))
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
        bool complete = true;
        foreach (Lane lane in OpenLanes())
        {
            if (!CostZoneLoader.TryLoad(logger, dataConfig.Road, lane.Continent, out CostZones zones,
                    roadCoreHalfWidth: queryOptions.RoadCore))
            {
                logger.LogWarning(
                    "Cost zones for {Continent} were not reloaded: at least one file is unreadable or invalid. Previous zones stay in effect.",
                    lane.Continent);
                complete = false;
                continue;
            }

            // Swapped between queries, never under one.
            lane.Gate.Wait();
            try
            {
                lane.Pathfinder.Zones = zones;
            }
            finally
            {
                lane.Gate.Release();
            }
        }

        return complete;
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

    // As PPatherService.ToLocal: each point in the zone that holds it, preferring the
    // query's own zone where zones overlap. A point outside every zone of the continent
    // stays in the query's zone rather than failing the whole route.
    private Vector3 WorldToMap(Vector3 world, in WorldMapArea area)
    {
        try
        {
            return maps.ToMap_FlipXY(world, area.MapID, area.UIMapId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return WorldMapAreaDB.ToMap_FlipXY(world, area);
        }
    }

    private static RouteOutcome Fail(RoutePlanStatus status, string continent, long start, string detail)
    {
        return new(status, [], continent, Stopwatch.GetElapsedTime(start).TotalMilliseconds, detail);
    }

    private sealed class Lane(string continent, NavmeshPathfinder pathfinder) : IDisposable
    {
        private long queries;
        private long lastMsBits;

        public string Continent { get; } = continent;
        public NavmeshPathfinder Pathfinder { get; } = pathfinder;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long Queries => Interlocked.Read(ref queries);
        public double LastMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref lastMsBits));

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
