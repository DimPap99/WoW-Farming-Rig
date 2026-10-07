using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Hosting;

/// <summary>A service the Control Center is a client of, as its pages name it.</summary>
/// <param name="Name">What a person calls it: "Pathing Service", "Bot Manager".</param>
/// <param name="Url">Where the Control Center expects it.</param>
/// <param name="UrlKey">The setting that points the Control Center elsewhere.</param>
/// <param name="Start">The command that starts it.</param>
/// <param name="StartNote">What else to know when starting it, or null.</param>
public sealed record ServiceDescription(string Name, Uri Url, string UrlKey, string Start, string? StartNote = null);

/// <summary>
/// One service's state for every page and the status bar, read every <see cref="Interval"/> by this
/// one poller rather than by each open page. A page shows "offline" from <see cref="Online"/>
/// instead of waiting on a call that cannot succeed, and reads again on <see cref="Reconnected"/>.
/// </summary>
public abstract class ServiceStatus(ServiceDescription service, ILogger logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim polling = new(1, 1);
    private int readAgain;

    public ServiceDescription Service => service;

    /// <summary>Whether the last poll reached the service.</summary>
    public bool Online { get; private set; }

    /// <summary>Why the last poll failed; null while online.</summary>
    public string? Error { get; private set; } = "not asked yet";

    /// <summary>Raised after every poll, on the poller's thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when the service is reached after being down (or for the first time), and at the next poll
    /// that reaches it after <see cref="ReadAgain"/>: what pages read from it may have changed while it
    /// stayed up, or a page's read of it failed. Pages read again.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>Polls now, for a page that just changed something (a bake, a cache clear).</summary>
    public async Task RefreshAsync(CancellationToken token = default)
    {
        await polling.WaitAsync(token);
        try
        {
            await PollAsync(token);
        }
        finally
        {
            polling.Release();
        }
    }

    /// <summary>Reads the service once: null when it answered, else why not.</summary>
    /// <param name="wasOnline">Whether the poll before this one reached it.</param>
    protected abstract Task<string?> ReadAsync(bool wasOnline, CancellationToken token);

    /// <summary>
    /// Has pages read the service again (<see cref="Reconnected"/>) at the next poll that reaches it:
    /// what they read may have changed (a restart quicker than a poll, new data), or a read of theirs
    /// failed while the service stayed up and was not kept.
    /// </summary>
    public void ReadAgain()
    {
        Volatile.Write(ref readAgain, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        try
        {
            do
            {
                await RefreshAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        bool wasOnline = Online;
        string? error = await ReadAsync(wasOnline, token);
        Online = error is null;
        Error = error;

        if (wasOnline != Online && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("{Service} {State}{Reason}", service.Name, Online ? "online" : "offline",
                Online ? string.Empty : $": {Error}");
        }

        Subscribers.Raise(Changed, handler => handler(), logger);
        bool again = Online && Interlocked.Exchange(ref readAgain, 0) == 1;
        if (Online && (!wasOnline || again))
        {
            Subscribers.Raise(Reconnected, handler => handler(), logger);
        }
    }

    public override void Dispose()
    {
        polling.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Raises an event so that a subscriber that throws stops neither the others nor the raiser: a
/// poller's loop or the fleet feed's connection would end with it.
/// </summary>
internal static class Subscribers
{
    public static void Raise<THandler>(THandler? handlers, Action<THandler> invoke, ILogger logger) where THandler : Delegate
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((THandler)handler);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An event subscriber failed: {Subscriber}", handler.Method.Name);
            }
        }
    }
}
