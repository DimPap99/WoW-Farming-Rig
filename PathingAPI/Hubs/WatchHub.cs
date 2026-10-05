using Microsoft.AspNetCore.SignalR;

using PathingAPI.Watch;

using System;
using System.Threading.Tasks;

namespace PathingAPI;

/// <summary>
/// Pushes searches, paths and geometry to the 3D view and the map. Clients only
/// listen; <see cref="WatchBroadcaster"/> does the sending. Connections are counted
/// so the broadcaster does no work while nobody is watching.
/// </summary>
public sealed class WatchHub(WatchSettings settings) : Hub
{
    public const string Url = "/watchHub";

    public override Task OnConnectedAsync()
    {
        settings.ViewerJoined();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        settings.ViewerLeft();
        return base.OnDisconnectedAsync(exception);
    }
}
