using ControlCenter.Hosting;

using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using ServiceClients;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>
/// The routes the Pathing Service plans, for any caller, as it answers them: one connection to its
/// watch hub as a route watcher (<see cref="WatchHubMethods.WatchRoutes"/>), which is sent no
/// scene - no tiles, no drawings - so the service builds none for it. The connection is open only
/// while a page watches (<see cref="Watch"/>). The hub replays its last routes on every join; the
/// feed keeps the newest <see cref="Kept"/> across connections, each once. Its events come on
/// SignalR's threads.
/// </summary>
/// <param name="connect">A connection to the watch hub: the service's at <c>PathingService:Url</c>
/// (<see cref="ServiceHubs.Build"/>), or a test's in-memory one.</param>
public sealed partial class WatchFeed(Func<HubConnection> connect, ILogger<WatchFeed> logger) : IAsyncDisposable
{
    /// <summary>Routes kept, the newest first.</summary>
    public const int Kept = 30;

    /// <summary>
    /// The waits between attempts to connect (<see cref="ServiceHubs.Wait"/>), the last one repeated: the
    /// feed never gives up while watched.
    /// </summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly Lock sync = new();
    private readonly List<WatchRoute> routes = [];
    private int watchers;
    // The connection loop while watched, and what stops it.
    private (CancellationTokenSource Stop, Task Loop)? running;
    private volatile bool live;

    /// <summary>Connected and watching: new routes arrive as they are planned.</summary>
    public bool Live => live;

    /// <summary>The routes kept, the newest first.</summary>
    public IReadOnlyList<WatchRoute> Routes
    {
        get
        {
            lock (sync)
            {
                return [.. routes];
            }
        }
    }

    /// <summary>A route arrived, the routes were cleared, or the connection came or went.</summary>
    public event Action? Changed;

    /// <summary>Keeps the feed connected until the lease is disposed; the first lease connects, the last disconnects.</summary>
    public IDisposable Watch()
    {
        lock (sync)
        {
            if (watchers++ == 0)
            {
                CancellationTokenSource stop = new();
                running = (stop, RunAsync(stop.Token));
            }
        }

        return new Lease(this);
    }

    public async ValueTask DisposeAsync()
    {
        (CancellationTokenSource Stop, Task Loop)? run;
        lock (sync)
        {
            run = running;
            running = null;
            watchers = 0;
        }

        await StopAsync(run);
    }

    private void Unwatch()
    {
        (CancellationTokenSource Stop, Task Loop)? run = null;
        lock (sync)
        {
            if (--watchers == 0)
            {
                run = running;
                running = null;
            }
        }

        _ = StopAsync(run);
    }

    private static async Task StopAsync((CancellationTokenSource Stop, Task Loop)? run)
    {
        if (run is not (CancellationTokenSource stop, Task loop))
        {
            return;
        }

        await stop.CancelAsync();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        await using HubConnection hub = connect();
        hub.On<WatchRoute>(WatchHubMethods.RoutePlanned, Add);
        hub.On(WatchHubMethods.Clear, Forget);
        await ServiceHubs.StayConnectedAsync(hub, Backoff, WatchAsync, ex => LogUnreachable(logger, ex.Message), token);
    }

    // Joins as a route watcher and follows the hub until the connection closes.
    private async Task<bool> WatchAsync(HubConnection hub, Task closed, CancellationToken token)
    {
        await hub.InvokeAsync(WatchHubMethods.WatchRoutes, token);
        SetLive(true);
        try
        {
            await closed.WaitAsync(token);
            LogLost(logger);
            return true;
        }
        finally
        {
            SetLive(false);
        }
    }

    // A route, newest first; one the hub replays on a join is kept once.
    private void Add(WatchRoute route)
    {
        lock (sync)
        {
            if (routes.Any(r => r.At == route.At && r.Continent == route.Continent && r.From == route.From && r.To == route.To))
            {
                return;
            }

            int index = routes.FindIndex(r => r.At < route.At);
            routes.Insert(index < 0 ? routes.Count : index, route);
            if (routes.Count > Kept)
            {
                routes.RemoveRange(Kept, routes.Count - Kept);
            }
        }

        Subscribers.Raise(Changed, handler => handler(), logger);
    }

    private void Forget()
    {
        lock (sync)
        {
            routes.Clear();
        }

        Subscribers.Raise(Changed, handler => handler(), logger);
    }

    private void SetLive(bool value)
    {
        if (live != value)
        {
            live = value;
            Subscribers.Raise(Changed, handler => handler(), logger);
        }
    }

    private sealed class Lease(WatchFeed feed) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                feed.Unwatch();
            }
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch feed lost its connection; reconnecting")]
    static partial void LogLost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watch feed cannot connect ({Reason}); it keeps trying while a page watches")]
    static partial void LogUnreachable(ILogger logger, string reason);

    #endregion
}
