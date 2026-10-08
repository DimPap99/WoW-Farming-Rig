using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PathingService.Routes;

using PPather.Planning;

using ServiceHosting;

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
/// query threads and only post sends here; one loop sends them in order (<see cref="HubSendQueue"/>,
/// the fleet hub's too), so a query never waits on a socket. With no watcher connected nothing is
/// posted; the last routes are still remembered for a watcher who joins later.
/// </summary>
public sealed class WatchBroadcaster(
    RoutePlanner planner,
    IHubContext<WatchHub> hub,
    HubSendQueue sends) : IHostedService
{
    /// <summary>Routes with points (found or partial) sent to a watcher as it joins.</summary>
    public const int RememberedRoutes = 5;

    /// <summary>The hub group of the route watchers (<see cref="WatchHubMethods.WatchRoutes"/>).</summary>
    public const string RouteWatchers = "routes";

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
        foreach (WatchRoute route in routes)
        {
            sends.Post(watcher, WatchHubMethods.RoutePlanned, route);
        }
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

        sends.Post(hub.Clients.Group(RouteWatchers), WatchHubMethods.Clear);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        planner.RoutePlanned += OnRoutePlanned;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        planner.RoutePlanned -= OnRoutePlanned;
        return Task.CompletedTask;
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
            sends.Post(hub.Clients.Group(RouteWatchers), WatchHubMethods.RoutePlanned, route);
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
}
