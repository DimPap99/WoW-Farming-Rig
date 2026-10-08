using Contracts;

using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients;

/// <summary>
/// What a client does on a hub connection that just started: register, read what it missed, then wait
/// until <paramref name="closed"/> completes. True when it got that far, so the next connection starts
/// over at the backoff's first step; false when it could not (it says why itself), which counts as a failure.
/// </summary>
public delegate Task<bool> HubSession(HubConnection hub, Task closed, CancellationToken token);

/// <summary>
/// How every client of the rig's hubs dials one and stays on it: the agent's link to the Bot Manager, and
/// the Control Center's fleet and route feeds, which each had a copy of this loop, with jitter in one only.
/// </summary>
public static class ServiceHubs
{
    /// <summary>
    /// A connection to the hub at <paramref name="path"/> of the service at <paramref name="service"/>:
    /// MessagePack, with the service's key when it has one. <paramref name="configure"/> is for a test's
    /// in-memory server (its handler, long polling) or a header of the caller's own.
    /// </summary>
    public static HubConnection Build(Uri service, string path, string? apiKey, Action<HttpConnectionOptions>? configure = null)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(service, path), http =>
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    http.Headers[ServiceKey.Header] = apiKey;
                }

                configure?.Invoke(http);
            })
            .AddMessagePackProtocol()
            .Build();
    }

    /// <summary>
    /// Keeps <paramref name="hub"/> connected until <paramref name="stopping"/>: starts it, runs
    /// <paramref name="session"/> on it until it closes, and starts it again after <see cref="Wait"/>, for
    /// ever. <paramref name="unreachable"/> hears only the first failure of a row, so an outage is logged
    /// once. It ends only with <paramref name="stopping"/>, and never throws: a background service that
    /// faults stops its host. The caller adds the hub's handlers before and disposes it after.
    /// </summary>
    public static async Task StayConnectedAsync(HubConnection hub, TimeSpan[] backoff, HubSession session,
        Action<Exception> unreachable, CancellationToken stopping)
    {
        int failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            // Before the start: a connection may close before the session looks.
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task OnClosed(Exception? _)
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            }

            hub.Closed += OnClosed;
            try
            {
                await hub.StartAsync(stopping);
                failures = await session(hub, closed.Task, stopping) ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (failures++ == 0)
                {
                    unreachable(ex);
                }
            }
            finally
            {
                hub.Closed -= OnClosed;
            }

            try
            {
                // A session that failed may leave the connection up.
                if (hub.State != HubConnectionState.Disconnected)
                {
                    await hub.StopAsync(stopping);
                }

                await Task.Delay(Wait(backoff, failures), stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The wait before the next start: the first step after a session that got as far as it should, else
    /// the step for so many failures in a row, the last one repeated; each varied by a fifth either way, so
    /// the clients that lost a service together do not dial it in step.
    /// </summary>
    public static TimeSpan Wait(TimeSpan[] backoff, int failures)
    {
        TimeSpan step = backoff[Math.Clamp(failures - 1, 0, backoff.Length - 1)];
        return step * (0.8 + 0.4 * Random.Shared.NextDouble());
    }
}
