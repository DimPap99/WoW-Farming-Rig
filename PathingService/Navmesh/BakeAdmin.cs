using Contracts.Pathing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PathingService.Hosting;
using PathingService.Status;
using PathingService.World;

using PPather;
using PPather.Navmesh;
using PPather.Planning;
using PPather.Storage;

using ServiceHosting;

using SharedLib;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using WowTriangles;

namespace PathingService.Navmesh;

/// <summary>
/// The jobs that change what is baked: bakes and area-grid extraction, which read game files, and
/// deleting baked tiles. One job at a time, and a job is under way until its task has ended, however
/// it ended. Each job that reads game files opens its own <see cref="PPatherService"/> - one that
/// never answers a route; the planner's lanes do - and lets it go at its end, so nothing one job
/// learned (a continent whose archives failed to open, a continent's triangle cache of gigabytes)
/// outlives it.
/// <para>
/// A job tells the planner what changed once its tile workers have stopped
/// (<see cref="RoutePlanner.TilesChanged"/>, <see cref="RoutePlanner.ForgetAreaGrids"/>;
/// docs/known-issues.md, items 12, 52 and 53).
/// </para>
/// </summary>
public sealed class BakeAdmin : IDisposable
{
    /// <summary>The ADT grid is 64 by 64.</summary>
    public const int AdtGridSize = 64;

    public const string NoGameFiles =
        "no game files on the Pathing Service's host: baking and area grids need the client's archives " +
        "(Json/MPQ, or ForeverCasc:Install for WoW: Forever)";

    /// <summary>How long a delete that found a tile in use waits before it tries once more.</summary>
    public static readonly TimeSpan ClearRetryDelay = TimeSpan.FromMilliseconds(250);

    private enum JobKind
    {
        Bake,
        AreaGrid,
        Clear
    }

    // One job, and a task that ends when the job has, however it ended.
    private sealed class Job(JobKind kind, string? continent, PPatherService? engine)
    {
        public JobKind Kind { get; } = kind;

        /// <summary>Its continent; null for every continent.</summary>
        public string? Continent { get; } = continent;

        /// <summary>Its own engine; a clear needs none.</summary>
        public PPatherService? Engine { get; } = engine;

        public Task Ended { get; set; } = Task.CompletedTask;
    }

    private readonly DataConfig data;
    private readonly WorldMapAreaDB maps;
    private readonly IOptions<NavmeshBakeOptions> bakeOptions;
    private readonly IOptions<NavmeshQueryOptions> queryOptions;
    private readonly RoutePlanner planner;
    private readonly CoverageCache coverage;
    private readonly ClientContinents continents;
    private readonly ILogger<PPatherService> engineLogger;
    private readonly ILogger<BakeAdmin> logger;
    private readonly Func<IGameFileSource>? gameFiles;

    private readonly Lock gate = new();
    private Job? job;
    private BakeStatus lastBake = BakeStatus.Idle;

    public BakeAdmin(DataConfig data, WorldMapAreaDB maps, IOptions<NavmeshBakeOptions> bakeOptions,
        IOptions<NavmeshQueryOptions> queryOptions, RoutePlanner planner, CoverageCache coverage, ClientContinents continents,
        IConfiguration configuration, ILogger<PPatherService> engineLogger, ILogger<BakeAdmin> logger)
    {
        this.data = data;
        this.maps = maps;
        this.bakeOptions = bakeOptions;
        this.queryOptions = queryOptions;
        this.planner = planner;
        this.coverage = coverage;
        this.continents = continents;
        this.engineLogger = engineLogger;
        this.logger = logger;
        gameFiles = ForeverGameFiles.From(configuration, data.Exp, logger);
    }

    /// <summary>
    /// Whether this host can read game geometry: archives in <c>Json/MPQ</c>, or for WoW: Forever only
    /// a configured install (<c>ForeverCasc</c>). <c>Json/MPQ</c> is one folder for every client, and
    /// Forever's continents open from another client's archives too, so they would bake that client's
    /// geometry into Forever's tiles.
    /// </summary>
    public bool HasGameFiles => gameFiles is not null ||
                                (!ForeverGameFiles.IsForever(data.Exp) && MPQTriangleSupplier.GetArchiveNames(data).Length > 0);

    /// <summary>The running bake, or the last one.</summary>
    public BakeStatus Status
    {
        get
        {
            Job? current;
            BakeStatus last;
            lock (gate)
            {
                current = job;
                last = lastBake;
            }

            return current is { Kind: JobKind.Bake, Engine: PPatherService engine } && !current.Ended.IsCompleted
                ? ToContract(engine.BakeStatus)
                : last;
        }
    }

    /// <summary>Completes once no job is under way: the current job has ended, however it ended.</summary>
    public Task Idle
    {
        get
        {
            lock (gate)
            {
                return job?.Ended ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Starts a bake in the background.</summary>
    public ApiResult<BakeStatus> Start(BakeRequest request)
    {
        if (BakeProblem(request, out string? continent) is string problem)
        {
            return ApiResult<BakeStatus>.Invalid(problem);
        }

        if (!HasGameFiles)
        {
            return ApiResult<BakeStatus>.Conflict(NoGameFiles);
        }

        lock (gate)
        {
            if (Busy() is string busy)
            {
                return ApiResult<BakeStatus>.Conflict(busy);
            }

            Job bake = new(JobKind.Bake, continent, NewEngine());
            (int x, int y)? adt = request.AdtX is int x && request.AdtY is int y ? (x, y) : null;
            bake.Engine!.StartBake(continent, adt);
            bake.Ended = bake.Engine.BakeTask.ContinueWith(_ => EndBake(bake), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            job = bake;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Bake started: {Scope}", continent ?? "every continent");
        }

        return ApiResult<BakeStatus>.Done(Status);
    }

    public ApiResult<BakeStatus> Cancel()
    {
        Job? current;
        lock (gate)
        {
            current = job;
        }

        return current is { Kind: JobKind.Bake, Engine: PPatherService engine } && !current.Ended.IsCompleted && engine.CancelBake()
            ? ApiResult<BakeStatus>.Done(Status)
            : ApiResult<BakeStatus>.Conflict("no bake is running");
    }

    /// <summary>
    /// Deletes every baked tile of <paramref name="continent"/>, stale settings hashes included. The
    /// planner lets go of the continent's tiles afterwards, whatever the delete managed.
    /// </summary>
    public ApiResult<CacheCleared> Clear(string continent)
    {
        if (continents.Resolve(continent) is not string resolved)
        {
            return ApiResult<CacheCleared>.Invalid(continents.Refusal(continent));
        }

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (Busy() is string busy)
            {
                return ApiResult<CacheCleared>.Conflict(busy);
            }

            job = new Job(JobKind.Clear, resolved, null) { Ended = ended.Task };
        }

        try
        {
            long freed;
            try
            {
                freed = PPatherService.ClearNavmeshCache(data, resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A tile a lane is reading cannot be deleted on Windows; it is read in milliseconds.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Deleting the tiles of {Continent} met a file in use ({Reason}); trying once more",
                        resolved, ex.Message);
                }

                Thread.Sleep(ClearRetryDelay);
                freed = PPatherService.ClearNavmeshCache(data, resolved);
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Navmesh tiles of {Continent} deleted: {Bytes} bytes", resolved, freed);
            }

            return ApiResult<CacheCleared>.Done(new CacheCleared(resolved, freed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Not every tile of {Continent} could be deleted: {Reason}", resolved, ex.Message);
            return ApiResult<CacheCleared>.Conflict($"not every tile of {resolved} could be deleted ({ex.Message}); ask again");
        }
        finally
        {
            // Whatever the delete managed: a lane may hold tiles that are gone now.
            TilesChanged(resolved);
            ended.SetResult();
        }
    }

    /// <summary>Extracts the area-id grid of <paramref name="continent"/>, or of every continent, in the background.</summary>
    public ApiResult StartAreaGrid(string? continent)
    {
        string? resolved = null;
        if (continent is not null && (resolved = continents.Resolve(continent)) is null)
        {
            return ApiResult.Invalid(continents.Refusal(continent));
        }

        if (!HasGameFiles)
        {
            return ApiResult.Conflict(NoGameFiles);
        }

        lock (gate)
        {
            if (Busy() is string busy)
            {
                return ApiResult.Conflict(busy);
            }

            Job grid = new(JobKind.AreaGrid, resolved, NewEngine());
            grid.Ended = Task.Run(() => RunAreaGrid(grid));
            job = grid;
        }

        return ApiResult.Done;
    }

    /// <summary>Cancels a running bake and waits a while for its job to end, so its last tiles are whole.</summary>
    public void Dispose()
    {
        Job? current;
        lock (gate)
        {
            current = job;
        }

        if (current is null || current.Ended.IsCompleted)
        {
            return;
        }

        current.Engine?.CancelBake();
        try
        {
            current.Ended.Wait(NavmeshTileCache.WorkerShutdownTimeout + TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Logged where it failed.
        }
    }

    private PPatherService NewEngine()
    {
        return new PPatherService(engineLogger, data, maps, bakeOptions, queryOptions, gameFiles);
    }

    // On the bake's own thread, once it is over.
    private void EndBake(Job bake)
    {
        PPatherService engine = bake.Engine!;
        BakeStatus final = ToContract(engine.BakeStatus);
        try
        {
            // Lets go of the bake's tile cache, which waits for its workers to finish the tile each
            // has in hand: after this nothing writes tiles any more.
            Close(engine);
        }
        finally
        {
            TilesChanged(bake.Continent);
            lock (gate)
            {
                lastBake = final;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Bake ended ({Message}): {Done} tiles baked, {OnDisk} already present",
                final.Message, final.TilesDone, final.TilesAlreadyOnDisk);
        }
    }

    private void RunAreaGrid(Job grid)
    {
        string scope = grid.Continent ?? "every continent";
        try
        {
            if (grid.Engine!.BuildAreaGrid(grid.Continent))
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Area grid extracted: {Scope}", scope);
                }
            }
            else
            {
                logger.LogWarning("Area grid extraction skipped part of {Scope}; the lines above say why", scope);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Area grid extraction failed: {Scope}", scope);
        }
        finally
        {
            planner.ForgetAreaGrids(grid.Continent);
            Close(grid.Engine!);
        }
    }

    private void Close(PPatherService engine)
    {
        try
        {
            engine.Reset();
            engine.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The admin engine did not close cleanly");
        }
    }

    // Tells the planner and the coverage what changed; a failure here must not keep the job going.
    private void TilesChanged(string? continent)
    {
        try
        {
            planner.TilesChanged(continent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The planner could not let go of the tiles of {Scope}", continent ?? "every continent");
        }

        coverage.Invalidate();
    }

    // Why a new job must wait, or null. The caller holds the gate.
    private string? Busy()
    {
        if (job is null || job.Ended.IsCompleted)
        {
            return null;
        }

        if (job.Kind == JobKind.Bake)
        {
            BakeStatus s = ToContract(job.Engine!.BakeStatus);
            return $"a bake is running: {s.Continent} {s.AdtDone}/{s.AdtTotal} ADT ({s.Message})";
        }

        return job.Kind == JobKind.AreaGrid ? "an area grid is being extracted" : "baked tiles are being deleted";
    }

    private string? BakeProblem(BakeRequest request, out string? continent)
    {
        continent = null;
        if (request.Continent is not null && (continent = continents.Resolve(request.Continent)) is null)
        {
            return continents.Refusal(request.Continent);
        }

        if (request.AdtX.HasValue != request.AdtY.HasValue)
        {
            return "an ADT needs both adtX and adtY";
        }

        if (request.AdtX is int x && request.AdtY is int y)
        {
            if (continent is null)
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

    private static BakeStatus ToContract(PPatherService.NavmeshBakeStatus s)
    {
        return new BakeStatus(s.Running, s.Scope, s.Continent, s.AdtTotal, s.AdtDone, s.TilesTotal, s.TilesDone,
            s.TilesAlreadyOnDisk, s.ElapsedSeconds, s.Message);
    }
}
