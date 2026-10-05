using DotRecast.Detour;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PPather;
using PPather.Data;
using PPather.Graph;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using WowTriangles;

using static System.Diagnostics.Stopwatch;

namespace PathingAPI.Watch;

/// <summary>
/// Streams what the engine does - search progress, finished paths, geometry and
/// navmesh tiles - to every watch-hub viewer. It subscribes to the engine once for
/// the whole server. That used to be done by each open page's watch component,
/// and since each of them broadcast to all viewers, N open tabs sent every message
/// N times to N clients, each with its own 50 ms polling thread.
/// <para>
/// Engine events fire on the searching thread. They only queue work here; one loop
/// runs it in order, so the search never waits on a socket and a finished path is
/// never drawn before the progress that preceded it. With no viewer connected,
/// nothing is queued at all, so the API pays nothing for the visualiser.
/// </para>
/// </summary>
public sealed class WatchBroadcaster(
    PPatherService service,
    IHubContext<WatchHub> hub,
    WatchSettings settings,
    ILogger<WatchBroadcaster> logger) : BackgroundService
{
    private const int ProgressIntervalMs = 50;
    private const float WalkableSearchRadius = 20f;
    private const float ExploredAreaExpand = 10f;
    private const float ChunkSpotPadding = 5f;
    private const float ToonSize = 0.5f;

    private readonly Channel<Func<CancellationToken, Task>> work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentQueue<ChunkEventArgs> chunks = new();
    private readonly ConcurrentQueue<(int x, int z, DtMeshData data)> tileAdds = new();
    private readonly ConcurrentQueue<(int x, int z)> tileRemovals = new();

    // Set by RouteSearch on the searching thread just before it searches or draws,
    // and read back on that same thread when the engine reports the path.
    private PathStyle nextStyle = PathStyle.Api;

    // Touched only by the work loop.
    private Vector4 lastFrom;
    private Vector4 lastTo;
    private Vector3 lastClosest;
    private Vector3 lastPeek;
    private int testPointsSent;
    private int blockedPointsSent;
    private bool reportedNoGeometry;

    private bool Watched => settings.Viewers > 0;

    public void NextPathStyle(PathStyle style) => nextStyle = style;

    /// <summary>Shows a message in every viewer's status line.</summary>
    public void Log(string message)
    {
        if (Watched)
            Enqueue(token => SendAsync("log", token, message));
    }

    /// <summary>Draws <paramref name="points"/> as a finished path, for a draw the engine cannot do.</summary>
    public void ShowPath(Vector3[] points, PathStyle style)
    {
        if (Watched)
            Enqueue(token => FinishAsync([.. points], style, token));
    }

    /// <summary>Marks a point with a sphere, e.g. a generated start position.</summary>
    public void ShowMarker(string name, Vector3 at, WatchColor color)
    {
        if (Watched)
            Enqueue(token => SendAsync("drawSphere", token, at, (int)color, name));
    }

    /// <summary>Wipes every viewer's scene and forgets what was already sent.</summary>
    public void Clear()
    {
        chunks.Clear();
        tileAdds.Clear();
        tileRemovals.Clear();

        Enqueue(token =>
        {
            lastFrom = default;
            lastTo = default;
            lastClosest = default;
            lastPeek = default;
            testPointsSent = 0;
            blockedPointsSent = 0;
            reportedNoGeometry = false;
            return SendAsync("clear", token);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        service.SearchBegin += OnSearchBegin;
        service.OnPathCreated += OnPathCreated;
        service.OnChunkAdded += OnChunkAdded;
        service.OnNavmeshTileAdded += OnNavmeshTileAdded;
        service.OnNavmeshTileRemoved += OnNavmeshTileRemoved;
        service.OnLinesAdded += OnLinesAdded;
        service.OnSphereAdded += OnSphereAdded;

        try
        {
            await foreach (Func<CancellationToken, Task> item in work.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await item(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Watch broadcast failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            service.SearchBegin -= OnSearchBegin;
            service.OnPathCreated -= OnPathCreated;
            service.OnChunkAdded -= OnChunkAdded;
            service.OnNavmeshTileAdded -= OnNavmeshTileAdded;
            service.OnNavmeshTileRemoved -= OnNavmeshTileRemoved;
            service.OnLinesAdded -= OnLinesAdded;
            service.OnSphereAdded -= OnSphereAdded;
        }
    }

    private void Enqueue(Func<CancellationToken, Task> item) => work.Writer.TryWrite(item);

    // --- engine events (searching thread) ---------------------------------------

    private void OnSearchBegin()
    {
        if (Watched)
            Enqueue(ProgressAsync);
    }

    private void OnPathCreated(Path? path)
    {
        if (!Watched)
            return;

        PathStyle style = nextStyle;
        List<Vector3>? points = path?.locations.ToList();
        Enqueue(token => FinishAsync(points, style, token));
    }

    private void OnChunkAdded(ChunkEventArgs e)
    {
        if (Watched && settings.Geometry != GeometryMode.Off)
            chunks.Enqueue(e);
    }

    private void OnNavmeshTileAdded(int x, int z, DtMeshData data)
    {
        if (Watched && settings.NavmeshTiles)
            tileAdds.Enqueue((x, z, data));
    }

    private void OnNavmeshTileRemoved(int x, int z)
    {
        if (Watched)
            tileRemovals.Enqueue((x, z));
    }

    private void OnLinesAdded(LinesEventArgs e)
    {
        if (!Watched)
            return;

        Vector3[] points = Array.ConvertAll(e.Locations, l => l.AsVector3());
        Enqueue(async token =>
        {
            await DrawGeometryAsync(token);
            await SendAsync("drawPath", token, points, e.Colour, e.Name);
        });
    }

    private void OnSphereAdded(SphereEventArgs e)
    {
        if (Watched)
            Enqueue(token => SendAsync("drawSphere", token, e.Location.AsVector3(), e.Colour, e.Name));
    }

    // --- work (single loop) -----------------------------------------------------

    private async Task ProgressAsync(CancellationToken token)
    {
        long start = GetTimestamp();

        while (!token.IsCancellationRequested && service.IsSearching)
        {
            await SendAsync("drawPath", token, service.GetCurrentSearchPath(), (int)WatchColor.White, "search");
            await SendMarkersAsync(token);

#pragma warning disable CS0162 // DelayMs is a const debug knob; 0 in a normal build.
            if (PathGraph.DelayMs > 0)
            {
                await SendTestPointsAsync(onlyChanged: true, token);
                await DrawGeometryAsync(token);
            }
#pragma warning restore CS0162

            await DrawNavmeshTilesAsync(token);
            await Task.Delay(ProgressIntervalMs, token);
        }

        double ms = GetElapsedTime(start).TotalMilliseconds;
        if (ms >= ProgressIntervalMs)
            await SendAsync("log", token, $"Search took {ms:F0} ms");
    }

    private async Task FinishAsync(List<Vector3>? points, PathStyle style, CancellationToken token)
    {
        await DrawGeometryAsync(token);
        await DrawNavmeshTilesAsync(token);
        await SendTestPointsAsync(onlyChanged: false, token);

        lastFrom = service.SearchFrom;
        lastTo = service.SearchTo;
        await SendAsync("drawLine", token, lastFrom.AsVector3(), PathGraph.toonHeight, (int)WatchColor.Green, "start");
        await SendAsync("drawLine", token, lastTo.AsVector3(), PathGraph.toonHeight, (int)WatchColor.Blue, "end");

        await SendAsync("removeMesh", token, "search");

        if (points is not { Count: > 0 })
        {
            await SendAsync("log", token, "No path found");
            return;
        }

        await SendAsync("drawPath", token, points, (int)style.Color, style.Name);
        await SendAsync("log", token, $"Path found: {points.Count} points");
    }

    private async Task SendMarkersAsync(CancellationToken token)
    {
        if (lastFrom != service.SearchFrom)
        {
            lastFrom = service.SearchFrom;
            await SendAsync("drawLine", token, lastFrom.AsVector3(), PathGraph.toonHeight, (int)WatchColor.Green, "start");
        }

        if (lastTo != service.SearchTo)
        {
            lastTo = service.SearchTo;
            await SendAsync("drawLine", token, lastTo.AsVector3(), PathGraph.toonHeight, (int)WatchColor.Blue, "end");
        }

        if (lastClosest != service.ClosestLocation)
        {
            lastClosest = service.ClosestLocation;
            await SendAsync("drawLine", token, lastClosest, PathGraph.toonHeight, (int)WatchColor.Black, "closest");
        }

        if (lastPeek != service.PeekLocation)
        {
            lastPeek = service.PeekLocation;
            await SendAsync("drawLine", token, lastPeek, PathGraph.toonHeight, (int)WatchColor.Orange, "peek");
        }
    }

    /// <summary>SpotAStar's explored and blocked points, when the toolbar asks for them.</summary>
    private async Task SendTestPointsAsync(bool onlyChanged, CancellationToken token)
    {
        if (!settings.TestPoints)
            return;

        try
        {
            // Copied first: during a slowed search the engine is still adding to them.
            Vector4[] tested = service.TestPoints.ToArray();
            if (!onlyChanged || tested.Length != testPointsSent)
            {
                testPointsSent = tested.Length;
                await SendAsync("drawWeightedLines", token, tested, "debug");
            }

            Vector3[] blocked = service.BlockedPoints.ToArray();
            if (!onlyChanged || blocked.Length != blockedPointsSent)
            {
                blockedPointsSent = blocked.Length;
                await SendAsync("drawLines", token, blocked, (int)WatchColor.Red, "debug2");
            }
        }
        catch (InvalidOperationException)
        {
            // Modified mid-copy; the next tick or the final draw sends it.
        }
    }

    private async Task DrawNavmeshTilesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && tileRemovals.TryDequeue(out (int x, int z) removed))
        {
            string name = NavmeshTileMesh.Name(removed.x, removed.z);
            await SendAsync("removeMesh", token, name + "_g");
            await SendAsync("removeMesh", token, name + "_w");
        }

        while (!token.IsCancellationRequested && tileAdds.TryDequeue(out (int x, int z, DtMeshData data) added))
        {
            NavmeshTileMesh mesh = NavmeshTileMesh.From(added.data);
            await SendAsync("addNavmeshTile", token,
                NavmeshTileMesh.Name(added.x, added.z), mesh.Positions, mesh.Ground, mesh.Liquid);
        }
    }

    private async Task DrawGeometryAsync(CancellationToken token)
    {
        if (settings.Geometry == GeometryMode.Off)
        {
            chunks.Clear();
            return;
        }

        // No triangle world = a disk-only install: the navmesh tiles were downloaded
        // and there are no game archives to build a mesh from. The navmesh overlay
        // still renders; the terrain cannot.
        if (service.TriangleWorld == null)
        {
            chunks.Clear();
            if (!reportedNoGeometry)
            {
                reportedNoGeometry = true;
                await SendAsync("log", token,
                    "No game archives for this continent: terrain is unavailable, navmesh tiles and routes still render.");
            }
            return;
        }

        while (settings.Geometry != GeometryMode.Off &&
            !token.IsCancellationRequested &&
            chunks.TryDequeue(out ChunkEventArgs? e))
        {
            TriangleCollection chunk = service.GetChunkAt(e.GridX, e.GridY);

            if (settings.Geometry == GeometryMode.All)
                await SendFullMeshAsync(chunk, token);
            else
                await SendSparseMeshAsync(chunk, settings.Geometry, token);
        }
    }

    private async Task SendFullMeshAsync(TriangleCollection chunk, CancellationToken token)
    {
        TriangleType[] layers = [TriangleType.Terrain, TriangleType.Water, TriangleType.Object, TriangleType.Model];
        ArrayPool<int> pool = ArrayPool<int>.Shared;
        int count = chunk.TriangleCount * 3;
        int[][] rented = new int[layers.Length][];

        try
        {
            Memory<int>[] models = new Memory<int>[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                rented[i] = pool.Rent(count);
                int used = MeshFactory.CreateTriangles(layers[i], chunk, rented[i]);
                models[i] = rented[i].AsMemory(0, used);
            }

            // Awaited before the arrays go back: the send serialises them.
            await SendAsync("addModels", token, models, MeshFactory.CreatePoints(chunk));
        }
        finally
        {
            foreach (int[] array in rented)
            {
                if (array != null)
                    pool.Return(array);
            }
        }
    }

    private async Task SendSparseMeshAsync(TriangleCollection chunk, GeometryMode mode, CancellationToken token)
    {
        List<Spot> spots = SpotsNear(chunk);
        if (spots.Count == 0 && mode is GeometryMode.SparseFromSpots or GeometryMode.ExploredArea)
        {
            return;
        }

        (List<Vector3> vertices, List<int> indices) = mode switch
        {
            GeometryMode.SparseFromSpots => MeshFactory.CreateSparseFromSpots(
                chunk, spots, PathGraph.toonHeight, ToonSize),
            GeometryMode.WalkableOnly => MeshFactory.CreateWalkableOnly(
                chunk, service.TriangleWorld, PathGraph.toonHeight, ToonSize, spots, WalkableSearchRadius),
            _ => MeshFactory.CreateExploredArea(chunk, spots, ExploredAreaExpand),
        };

        // Everything as terrain; the other three layers stay empty.
        Memory<int>[] models = [indices.ToArray(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()];
        await SendAsync("addModels", token, models, vertices);
    }

    /// <summary>Explored, unblocked spots inside (or just outside) the chunk's bounds.</summary>
    private List<Spot> SpotsNear(TriangleCollection chunk)
    {
        float minX = chunk.Min.X - ChunkSpotPadding;
        float minY = chunk.Min.Y - ChunkSpotPadding;
        float maxX = chunk.Max.X + ChunkSpotPadding;
        float maxY = chunk.Max.Y + ChunkSpotPadding;

        List<Spot> near = [];
        foreach (Spot spot in service.GetSpots())
        {
            Vector3 loc = spot.Loc;
            if (!spot.IsBlocked() && loc.X >= minX && loc.X <= maxX && loc.Y >= minY && loc.Y <= maxY)
                near.Add(spot);
        }

        return near;
    }

    private async Task SendAsync(string method, CancellationToken token, params object?[] args)
    {
        try
        {
            await hub.Clients.All.SendCoreAsync(method, args, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(e, "Watch hub send {Method} failed", method);
        }
    }
}
