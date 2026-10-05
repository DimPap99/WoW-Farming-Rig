using ControlCenter.Hosting;

using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>Builds the <see cref="WatchFeed"/>'s connection to the service's watch hub; a test builds one over its in-memory server.</summary>
public interface IWatchConnectionFactory
{
    HubConnection Create();
}

/// <summary>The watch hub of the service at <c>PathingService:Url</c>: MessagePack, with the service's key.</summary>
public sealed class WatchConnectionFactory(PathingServiceOptions options) : IWatchConnectionFactory
{
    public HubConnection Create()
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(options.Url, PathingApi.WatchHub), http =>
            {
                if (!string.IsNullOrEmpty(options.ApiKey))
                {
                    http.Headers[ServiceKey.Header] = options.ApiKey;
                }
            })
            .AddMessagePackProtocol()
            .Build();
    }
}

/// <summary>
/// The routes the Pathing Service plans, for any caller, as it answers them: one connection to its
/// watch hub as a route watcher (<see cref="WatchHubMethods.WatchRoutes"/>), which is sent no
/// scene - no tiles, no drawings - so the service builds none for it. The connection is open only
/// while a page watches (<see cref="Watch"/>). The hub replays its last routes on every join; the
/// feed keeps the newest <see cref="Kept"/> across connections, each once. Its events come on
/// SignalR's threads.
/// </summary>
public sealed partial class WatchFeed(IWatchConnectionFactory connections, ILogger<WatchFeed> logger) : IAsyncDisposable
{
    /// <summary>Routes kept, the newest first.</summary>
    public const int Kept = 30;

    /// <summary>The waits between attempts to connect, the last one repeated: the feed never gives up while watched.</summary>
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
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                failures = await StayConnectedAsync(failures, token) ? 0 : failures + 1;
                await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // Connects as a route watcher and follows the hub until the connection closes: true when it got
    // that far. A failure is logged only when it follows a success, so an outage logs once.
    private async Task<bool> StayConnectedAsync(int failures, CancellationToken token)
    {
        await using HubConnection hub = connections.Create();
        hub.On<WatchRoute>(WatchHubMethods.RoutePlanned, Add);
        hub.On(WatchHubMethods.Clear, Forget);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await hub.StartAsync(token);
            await hub.InvokeAsync(WatchHubMethods.WatchRoutes, token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            if (failures == 0)
            {
                LogUnreachable(logger, ex.Message);
            }

            return false;
        }

        SetLive(true);
        try
        {
            await closed.Task.WaitAsync(token);
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
