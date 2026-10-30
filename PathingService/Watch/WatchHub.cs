using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR;

using System;
using System.Threading.Tasks;

namespace PathingService.Watch;

/// <summary>
/// Streams the routes the planner answers to route watchers (<see cref="PathingApi.WatchHub"/>), such
/// as the Control Center's Live view. <see cref="WatchBroadcaster"/> does the sending.
/// </summary>
public sealed class WatchHub(WatchBroadcaster broadcaster) : Hub
{
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.WatcherLeft(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Makes this connection a route watcher: each route as it is answered, the remembered ones first.</summary>
    [HubMethodName(WatchHubMethods.WatchRoutes)]
    public async Task WatchRoutes()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, WatchBroadcaster.RouteWatchers);
        broadcaster.WatchRoutes(Context.ConnectionId);
    }
}
