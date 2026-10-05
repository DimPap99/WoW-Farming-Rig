using Contracts.Pathing;

using DotRecast.Detour;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PathingService.Navmesh;

using PPather.Graph;
using PPather.Planning;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PathingService.Watch;

/// <summary>
/// Streams what the planner does - every route it answers and every tile a lane loads or drops -
/// to the watch hub's viewers, and draws what callers ask it to. Planner events fire on query and
/// tile threads and only queue work here; one loop sends it in order, so a query never waits on
/// a socket. With no viewer connected nothing is queued; the last routes are still remembered
/// for a viewer who opens later.
/// <para>
/// Opening a view never resets anything: <see cref="Replay"/> sends that viewer the tiles the
/// lanes hold. PathingAPI reset its one engine for every new 3D page, which a service planning
/// for others must never do.
/// </para>
/// </summary>
public sealed class WatchBroadcaster(
    RoutePlanner planner,
    BakeAdmin admin,
    IHubContext<WatchHub> hub,
    ILogger<WatchBroadcaster> logger) : BackgroundService
{
    /// <summary>Routes with points (found or partial) kept for <see cref="Replay"/>.</summary>
    public const int RememberedRoutes = 5;

    /// <summary>A planned route's drawing; the next route replaces it.</summary>
    public const string RouteName = "path";

    public const string StartName = "start";

    public const string EndName = "end";

    private readonly Channel<Func<CancellationToken, Task>> work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock recentLock = new();
    private readonly Queue<PlannedRoute> recent = new();
    private int viewers;

    public int Viewers => Volatile.Read(ref viewers);

    private bool Watched => Viewers > 0;

    public void ViewerJoined()
    {
        Interlocked.Increment(ref viewers);
    }

    public void ViewerLeft()
    {
        Interlocked.Decrement(ref viewers);
    }

    /// <summary>Sends one viewer the tiles every lane holds and the remembered routes.</summary>
    public void Replay(string connectionId)
    {
        // Collected off the hub's thread: a lane busy with a query keeps the snapshot waiting.
        _ = Task.Run(() =>
        {
            List<LaneTile> tiles = [];
            foreach (RoutePlanner.LaneStatus lane in planner.Lanes())
            {
                tiles.AddRange(planner.ResidentTiles(lane.Continent));
            }

            PlannedRoute[] routes;
            lock (recentLock)
            {
                routes = [.. recent];
            }

            IClientProxy viewer = hub.Clients.Client(connectionId);
            Enqueue(async token =>
            {
                foreach (LaneTile tile in tiles)
                {
                    await SendTileAsync(viewer, tile, token);
                }

                foreach (PlannedRoute route in routes)
                {
                    await SendRouteAsync(viewer, route, token);
                }

                await SendAsync(viewer, WatchHubMethods.Log, token,
                    $"{tiles.Count} navmesh tiles and {routes.Length} recent routes");
            });
        });
    }

    /// <summary>
    /// Starts extracting the game geometry under <paramref name="tiles"/> (x, z pairs) for one
    /// viewer. Null when it started - the meshes and a closing log line follow - or why it cannot.
    /// </summary>
    public string? RequestTerrain(string connectionId, string continent, int[] tiles)
    {
        if (tiles is null || tiles.Length == 0 || tiles.Length % 2 != 0)
        {
            return "tiles come as x, z pairs";
        }

        if (tiles.Length / 2 > BakeAdmin.MaxTerrainTiles)
        {
            return $"at most {BakeAdmin.MaxTerrainTiles} tiles at a time";
        }

        if (!admin.HasGameFiles)
        {
            return BakeAdmin.NoGameFiles;
        }

        (int X, int Z)[] wanted = new (int, int)[tiles.Length / 2];
        for (int i = 0; i < wanted.Length; i++)
        {
            wanted[i] = (tiles[i * 2], tiles[(i * 2) + 1]);
        }

        IClientProxy viewer = hub.Clients.Client(connectionId);
        _ = Task.Run(() =>
        {
            int sent = 0;
            int triangles = 0;
            string message;
            try
            {
                bool done = admin.TryExtractTerrain(continent, wanted, mesh =>
                {
                    sent++;
                    triangles += mesh.TriangleCount;
                    Enqueue(token => SendTerrainAsync(viewer, mesh, token));
                }, CancellationToken.None, out string reason);

                message = done ? $"Terrain: {sent} tiles, {triangles:N0} triangles" : $"No terrain: {reason}";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Terrain for {Continent} failed", continent);
                message = $"Terrain failed: {ex.Message}";
            }

            Enqueue(token => SendAsync(viewer, WatchHubMethods.Log, token, message));
        });

        return null;
    }

    /// <summary>Shows <paramref name="points"/> (world yards) as a path in every viewer.</summary>
    public void Draw(string name, Vector3[] points, WatchColor color)
    {
        if (Watched)
        {
            Enqueue(token => SendAsync(hub.Clients.All, WatchHubMethods.DrawPath, token, Wire(points), (int)color, name));
        }
    }

    /// <summary>Marks a point (world yards) in every viewer.</summary>
    public void Mark(string name, Vector3 at, WatchColor color)
    {
        if (Watched)
        {
            Enqueue(token => SendAsync(hub.Clients.All, WatchHubMethods.DrawSphere, token, Wire(at), (int)color, name));
        }
    }

    /// <summary>Wipes every viewer's scene and forgets the remembered routes.</summary>
    public void Clear()
    {
        lock (recentLock)
        {
            recent.Clear();
        }

        Enqueue(token => SendAsync(hub.Clients.All, WatchHubMethods.Clear, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        planner.RoutePlanned += OnRoutePlanned;
        planner.TileLoaded += OnTileLoaded;
        planner.TileUnloaded += OnTileUnloaded;

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
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Watch broadcast failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            planner.RoutePlanned -= OnRoutePlanned;
            planner.TileLoaded -= OnTileLoaded;
            planner.TileUnloaded -= OnTileUnloaded;
        }
    }

    private void Enqueue(Func<CancellationToken, Task> item)
    {
        work.Writer.TryWrite(item);
    }

    // --- planner events (query and tile threads) ------------------------------------

    private void OnRoutePlanned(PlannedRoute route)
    {
        if (route.Points.Length > 0)
        {
            lock (recentLock)
            {
                recent.Enqueue(route);
                while (recent.Count > RememberedRoutes)
                {
                    recent.Dequeue();
                }
            }
        }

        if (Watched)
        {
            Enqueue(token => SendRouteAsync(hub.Clients.All, route, token));
        }
    }

    private void OnTileLoaded(LaneTile tile)
    {
        if (Watched)
        {
            Enqueue(token => SendTileAsync(hub.Clients.All, tile, token));
        }
    }

    private void OnTileUnloaded(LaneTile tile)
    {
        if (!Watched)
        {
            return;
        }

        string name = NavmeshTileMesh.Name(tile.Continent, tile.X, tile.Z);
        Enqueue(async token =>
        {
            await SendAsync(hub.Clients.All, WatchHubMethods.RemoveMesh, token, name + NavmeshTileMesh.GroundSuffix);
            await SendAsync(hub.Clients.All, WatchHubMethods.RemoveMesh, token, name + NavmeshTileMesh.LiquidSuffix);
        });
    }

    // --- sends (the loop) ------------------------------------------------------------

    private async Task SendRouteAsync(IClientProxy clients, PlannedRoute route, CancellationToken token)
    {
        await SendAsync(clients, WatchHubMethods.DrawLine, token, Wire(route.From), PathGraph.toonHeight, (int)WatchColor.Green, StartName);
        await SendAsync(clients, WatchHubMethods.DrawLine, token, Wire(route.To), PathGraph.toonHeight, (int)WatchColor.Blue, EndName);

        if (route.Status == RoutePlanStatus.Found)
        {
            await SendAsync(clients, WatchHubMethods.DrawPath, token, Wire(route.Points), (int)WatchColor.Red, RouteName);
            await SendAsync(clients, WatchHubMethods.Log, token,
                $"{route.Continent}: {route.Points.Length} points in {route.ElapsedMs:F0} ms");
            return;
        }

        // A partial route is drawn as far as it goes, in another colour, and says where it stops.
        if (route.Points.Length > 0)
        {
            await SendAsync(clients, WatchHubMethods.DrawPath, token, Wire(route.Points), (int)WatchColor.Orange, RouteName);
        }

        await SendAsync(clients, WatchHubMethods.Log, token, $"{route.Status}: {route.Detail}");
    }

    private Task SendTileAsync(IClientProxy clients, LaneTile tile, CancellationToken token)
    {
        if (tile.Data is not DtMeshData data)
        {
            return Task.CompletedTask;
        }

        NavmeshTileMesh mesh = NavmeshTileMesh.From(data);
        return SendAsync(clients, WatchHubMethods.AddNavmeshTile, token,
            NavmeshTileMesh.Name(tile.Continent, tile.X, tile.Z), mesh.Positions, mesh.Ground, mesh.Liquid);
    }

    private Task SendTerrainAsync(IClientProxy viewer, TerrainMesh mesh, CancellationToken token)
    {
        return SendAsync(viewer, WatchHubMethods.AddTerrainTile, token,
            mesh.Name, mesh.Positions, mesh.Terrain, mesh.Liquid, mesh.Objects, mesh.Models);
    }

    // A point travels as [x, y, z], which is how both views read it. MessagePack writes a
    // Vector3 as a map of its fields, which neither does.
    private static float[] Wire(Vector3 point)
    {
        return [point.X, point.Y, point.Z];
    }

    private static float[][] Wire(Vector3[] points)
    {
        float[][] wire = new float[points.Length][];
        for (int i = 0; i < points.Length; i++)
        {
            wire[i] = Wire(points[i]);
        }

        return wire;
    }

    private async Task SendAsync(IClientProxy clients, string method, CancellationToken token, params object?[] args)
    {
        try
        {
            await clients.SendCoreAsync(method, args, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Watch hub send {Method} failed", method);
            }
        }
    }
}
