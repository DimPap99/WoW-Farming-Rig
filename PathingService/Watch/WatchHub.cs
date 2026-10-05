using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR;

using System;
using System.Threading.Tasks;

namespace PathingService.Watch;

/// <summary>
/// Streams routes and navmesh tiles to the 3D view and the map (<see cref="PathingApi.WatchHub"/>).
/// <see cref="WatchBroadcaster"/> does the sending; connections are counted so it does nothing
/// while nobody watches.
/// </summary>
public sealed class WatchHub(WatchBroadcaster broadcaster) : Hub
{
    public override Task OnConnectedAsync()
    {
        broadcaster.ViewerJoined();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.ViewerLeft(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Sends this viewer the tiles the planner holds and the last routes.</summary>
    [HubMethodName(WatchHubMethods.Replay)]
    public void Replay()
    {
        broadcaster.Replay(Context.ConnectionId);
    }

    /// <summary>Makes this connection a watcher of routes only: each route as it is answered, no scene.</summary>
    [HubMethodName(WatchHubMethods.WatchRoutes)]
    public async Task WatchRoutes()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, WatchBroadcaster.RouteWatchers);
        broadcaster.WatchRoutes(Context.ConnectionId);
    }

    /// <summary>Starts sending this viewer the game geometry under the tiles; null, or why not.</summary>
    [HubMethodName(WatchHubMethods.Terrain)]
    public string? Terrain(string continent, int[] tiles)
    {
        return broadcaster.RequestTerrain(Context.ConnectionId, continent, tiles);
    }
}
