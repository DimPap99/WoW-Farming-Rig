using Contracts.Pathing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PathingService.Hosting;
using PathingService.Status;

using PPather;
using PPather.Planning;
using PPather.Storage;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using WowTriangles;

namespace PathingService.Navmesh;

/// <summary>
/// The jobs that read game files: bakes, area-grid extraction and terrain for the 3D view, plus
/// deleting baked tiles. They run on one <see cref="PPatherService"/> kept for exactly that - it
/// never answers a route, the planner's lanes do - created on first use, because it opens the
/// client's archives. One job at a time: they share that engine's continent and geometry.
/// <para>
/// A lane remembers a tile that was missing as empty for its whole life, so whatever changes the
/// tiles on disk closes the lanes it touched (docs/known-issues.md, item 12).
/// </para>
/// </summary>
public sealed class BakeAdmin : IDisposable
{
    /// <summary>The most terrain tiles one request extracts; a 5 by 5 block around the view.</summary>
    public const int MaxTerrainTiles = 25;

    /// <summary>The ADT grid is 64 by 64.</summary>
    public const int AdtGridSize = 64;

    public const string NoGameFiles =
        "no game files on the Pathing Service's host: baking, area grids and terrain need the client's archives " +
        "(Json/MPQ, or ForeverCasc:Install for WoW: Forever)";

    private enum Job
    {
        None,
        Bake,
        AreaGrid,
        Terrain,
        Clear
    }

    private readonly DataConfig data;
    private readonly WorldMapAreaDB maps;
    private readonly IOptions<NavmeshBakeOptions> bakeOptions;
    private readonly IOptions<NavmeshQueryOptions> queryOptions;
    private readonly RoutePlanner planner;
    private readonly CoverageCache coverage;
    private readonly ILogger<PPatherService> engineLogger;
    private readonly ILogger<BakeAdmin> logger;
    private readonly Func<IGameFileSource>? gameFiles;

    private readonly Lock gate = new();
    private PPatherService? engine;
    private Job job;
    private string? bakingContinent;

    public BakeAdmin(DataConfig data, WorldMapAreaDB maps, IOptions<NavmeshBakeOptions> bakeOptions,
        IOptions<NavmeshQueryOptions> queryOptions, RoutePlanner planner, CoverageCache coverage,
        IConfiguration configuration, ILogger<PPatherService> engineLogger, ILogger<BakeAdmin> logger)
    {
        this.data = data;
        this.maps = maps;
        this.bakeOptions = bakeOptions;
        this.queryOptions = queryOptions;
        this.planner = planner;
        this.coverage = coverage;
        this.engineLogger = engineLogger;
        this.logger = logger;
        gameFiles = ForeverGameFiles.From(configuration, data.Exp, logger);
    }

    /// <summary>Whether this host can read game geometry: archives in <c>Json/MPQ</c>, or a configured Forever install.</summary>
    public bool HasGameFiles => gameFiles is not null || MPQTriangleSupplier.GetArchiveNames(data).Length > 0;

    public BakeStatus Status
    {
        get
        {
            PPatherService? current = engine;
            return current is null ? BakeStatus.Idle : ToContract(current.BakeStatus);
        }
    }

    /// <summary>Starts a bake in the background.</summary>
    public StoreResult<BakeStatus> Start(BakeRequest request)
    {
        if (BakeProblem(request) is string problem)
        {
            return StoreResult<BakeStatus>.Invalid(problem);
        }

        if (!HasGameFiles)
        {
            return StoreResult<BakeStatus>.Conflict(NoGameFiles);
        }

        lock (gate)
        {
            if (job != Job.None)
            {
                return StoreResult<BakeStatus>.Conflict(Busy());
            }

            (int x, int y)? adt = request.AdtX is int x && request.AdtY is int y ? (x, y) : null;
            if (!EngineLocked().StartBake(request.Continent, adt))
            {
                return StoreResult<BakeStatus>.Conflict("a bake is already running");
            }

            job = Job.Bake;
            bakingContinent = request.Continent;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Bake started: {Scope}", request.Continent ?? "every continent");
        }

        return StoreResult<BakeStatus>.Done(Status);
    }

    public StoreResult<BakeStatus> Cancel()
    {
        return engine?.CancelBake() == true
            ? StoreResult<BakeStatus>.Done(Status)
            : StoreResult<BakeStatus>.Conflict("no bake is running");
    }

    /// <summary>Deletes every baked tile of <paramref name="continent"/>, stale settings hashes included.</summary>
    public StoreResult<CacheCleared> Clear(string continent)
    {
        if (ContinentProblem(continent) is string problem)
        {
            return StoreResult<CacheCleared>.Invalid(problem);
        }

        PPatherService work;
        lock (gate)
        {
            if (job != Job.None)
            {
                return StoreResult<CacheCleared>.Conflict(Busy());
            }

            job = Job.Clear;
            work = EngineLocked();
        }

        try
        {
            long freed = work.ClearNavmeshCache(continent);

            // After the delete: a lane opened while it ran may hold tiles that are gone now.
            planner.CloseLanes(continent);
            coverage.Invalidate();

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Navmesh tiles of {Continent} deleted: {Bytes} bytes", continent, freed);
            }

            return StoreResult<CacheCleared>.Done(new CacheCleared(continent, freed));
        }
        catch (InvalidOperationException ex)
        {
            return StoreResult<CacheCleared>.Conflict(ex.Message);
        }
        finally
        {
            lock (gate)
            {
                job = Job.None;
            }
        }
    }

    /// <summary>Extracts the area-id grid of <paramref name="continent"/>, or of every continent, in the background.</summary>
    public StoreResult StartAreaGrid(string? continent)
    {
        if (continent is not null && ContinentProblem(continent) is string problem)
        {
            return StoreResult.Invalid(problem);
        }

        if (!HasGameFiles)
        {
            return StoreResult.Conflict(NoGameFiles);
        }

        PPatherService work;
        lock (gate)
        {
            if (job != Job.None)
            {
                return StoreResult.Conflict(Busy());
            }

            job = Job.AreaGrid;
            work = EngineLocked();
        }

        _ = Task.Run(() =>
        {
            try
            {
                work.BuildAreaGrid(continent);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Area grid extracted: {Scope}", continent ?? "every continent");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Area grid extraction failed: {Scope}", continent ?? "every continent");
            }
            finally
            {
                planner.ForgetAreaGrids(continent);
                work.Reset();
                lock (gate)
                {
                    job = Job.None;
                }
            }
        });

        return StoreResult.Done;
    }

    /// <summary>
    /// Reads the game geometry under navmesh tiles of <paramref name="continent"/> and hands each
    /// tile's mesh to <paramref name="send"/>, then lets the geometry go: a continent's triangle
    /// cache runs to gigabytes. Runs on the caller's thread; false with the reason when it cannot.
    /// </summary>
    public bool TryExtractTerrain(string continent, IReadOnlyList<(int X, int Z)> tiles, Action<Watch.TerrainMesh> send,
        CancellationToken token, out string reason)
    {
        if (!HasGameFiles)
        {
            reason = NoGameFiles;
            return false;
        }

        if (ContinentProblem(continent) is string problem)
        {
            reason = problem;
            return false;
        }

        PPatherService work;
        lock (gate)
        {
            if (job != Job.None)
            {
                reason = Busy();
                return false;
            }

            job = Job.Terrain;
            work = EngineLocked();
        }

        try
        {
            work.Initialise(ContinentDB.NameToId[continent]);
            if (work.TriangleWorld is not ChunkedTriangleCollection world)
            {
                reason = $"the game files have no geometry for {continent}";
                return false;
            }

            foreach ((int x, int z) in tiles)
            {
                token.ThrowIfCancellationRequested();
                send(Watch.TerrainMesh.Extract(world, continent, x, z));
            }

            reason = string.Empty;
            return true;
        }
        finally
        {
            work.Reset();
            lock (gate)
            {
                job = Job.None;
            }
        }
    }

    public void Dispose()
    {
        PPatherService? current = engine;
        if (current is null)
        {
            return;
        }

        current.CancelBake();
        current.BakeEnded -= OnBakeEnded;
        current.Dispose();
    }

    // The caller holds the gate.
    private PPatherService EngineLocked()
    {
        if (engine is null)
        {
            engine = new PPatherService(engineLogger, data, maps, bakeOptions, queryOptions, gameFiles);
            engine.BakeEnded += OnBakeEnded;
        }

        return engine;
    }

    // On the bake's thread, once it is over.
    private void OnBakeEnded(PPatherService.NavmeshBakeStatus status)
    {
        string? continent;
        lock (gate)
        {
            continent = bakingContinent;
        }

        planner.CloseLanes(continent);
        coverage.Invalidate();
        engine?.Reset();

        lock (gate)
        {
            job = Job.None;
            bakingContinent = null;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Bake ended ({Message}): {Done} tiles baked, {OnDisk} already present; lanes of {Scope} reopen on the next route",
                status.Message, status.TilesDone, status.TilesAlreadyOnDisk, continent ?? "every continent");
        }
    }

    private string Busy()
    {
        return job switch
        {
            Job.Bake => Status is { } s ? $"a bake is running: {s.Continent} {s.AdtDone}/{s.AdtTotal} ADT ({s.Message})" : "a bake is running",
            Job.AreaGrid => "an area grid is being extracted",
            Job.Terrain => "terrain is being extracted for a viewer",
            Job.Clear => "baked tiles are being deleted",
            _ => "the admin engine is idle",
        };
    }

    private string? BakeProblem(BakeRequest request)
    {
        if (request.Continent is not null && ContinentProblem(request.Continent) is string problem)
        {
            return problem;
        }

        if (request.AdtX.HasValue != request.AdtY.HasValue)
        {
            return "an ADT needs both adtX and adtY";
        }

        if (request.AdtX is int x && request.AdtY is int y)
        {
            if (request.Continent is null)
            {
                return "a continent is required when baking a single ADT";
            }

            if (x is < 0 or >= AdtGridSize || y is < 0 or >= AdtGridSize)
            {
                return $"an ADT is on the {AdtGridSize} by {AdtGridSize} grid: 0 to {AdtGridSize - 1}";
            }
        }

        return null;
    }

    // Continents are folder names, so only the client's own are taken.
    private string? ContinentProblem(string continent)
    {
        IEnumerable<string> known = coverage.Read().Select(c => c.Continent);
        return known.Contains(continent, StringComparer.Ordinal)
            ? null
            : $"'{continent}' is not a continent of {data.Exp}; use one of {string.Join(", ", known)}";
    }

    private static BakeStatus ToContract(PPatherService.NavmeshBakeStatus s)
    {
        return new BakeStatus(s.Running, s.Scope, s.Continent, s.AdtTotal, s.AdtDone, s.TilesTotal, s.TilesDone,
            s.TilesAlreadyOnDisk, s.ElapsedSeconds, s.Message);
    }
}
