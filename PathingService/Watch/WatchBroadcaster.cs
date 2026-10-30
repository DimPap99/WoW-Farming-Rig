using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PathingService.Routes;

using PPather.Planning;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PathingService.Watch;

/// <summary>
/// Streams every route the planner answers to the watch hub's route watchers. Planner events fire on
/// query threads and only queue work here; one loop sends it in order, so a query never waits on a
/// socket. With no watcher connected nothing is queued; the last routes are still remembered for a
/// watcher who joins later.
/// </summary>
public sealed class WatchBroadcaster(
    RoutePlanner planner,
    IHubContext<WatchHub> hub,
    ILogger<WatchBroadcaster> logger) : BackgroundService
{
    /// <summary>Routes with points (found or partial) sent to a watcher as it joins.</summary>
    public const int RememberedRoutes = 5;

    /// <summary>The hub group of the route watchers (<see cref="WatchHubMethods.WatchRoutes"/>).</summary>
    public const string RouteWatchers = "routes";

    private readonly Channel<Func<CancellationToken, Task>> work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock recentLock = new();
    private readonly Queue<WatchRoute> recent = new();
    private readonly ConcurrentDictionary<string, bool> watchers = new();

    /// <summary>Connections watching routes now.</summary>
    public int Watchers => watchers.Count;

    /// <summary>Makes a connection a route watcher, and sends it the remembered routes.</summary>
    public void WatchRoutes(string connectionId)
    {
        watchers[connectionId] = true;
        WatchRoute[] routes;
        lock (recentLock)
        {
            routes = [.. recent];
        }

        IClientProxy watcher = hub.Clients.Client(connectionId);
        Enqueue(async token =>
        {
            foreach (WatchRoute route in routes)
            {
                await SendAsync(watcher, WatchHubMethods.RoutePlanned, token, route);
            }
        });
    }

    public void WatcherLeft(string connectionId)
    {
        watchers.TryRemove(connectionId, out _);
    }

    /// <summary>Forgets the remembered routes and tells every route watcher to.</summary>
    public void Clear()
    {
        lock (recentLock)
        {
            recent.Clear();
        }

        Enqueue(token => SendAsync(hub.Clients.Group(RouteWatchers), WatchHubMethods.Clear, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        planner.RoutePlanned += OnRoutePlanned;

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
        }
    }

    private void Enqueue(Func<CancellationToken, Task> item)
    {
        work.Writer.TryWrite(item);
    }

    // On the querying thread.
    private void OnRoutePlanned(PlannedRoute planned)
    {
        WatchRoute route = WatchRouteOf(planned, DateTimeOffset.UtcNow);
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

        if (!watchers.IsEmpty)
        {
            Enqueue(token => SendAsync(hub.Clients.Group(RouteWatchers), WatchHubMethods.RoutePlanned, token, route));
        }
    }

    // A route as watchers get it: the contract's types, the service's statuses.
    private static WatchRoute WatchRouteOf(PlannedRoute route, DateTimeOffset at)
    {
        return new WatchRoute(route.Continent, Point(route.From), Point(route.To), [.. route.Points.Select(Point)],
            RouteHandler.ToStatus(route.Status), route.ElapsedMs, route.Detail, at);
    }

    private static Point3 Point(Vector3 v)
    {
        return new Point3(v.X, v.Y, v.Z);
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
