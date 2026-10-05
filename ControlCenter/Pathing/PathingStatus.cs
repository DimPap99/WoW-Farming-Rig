using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>
/// The Pathing Service's state for every page and the status bar: read every
/// <see cref="Interval"/> by this one poller, rather than by each open page. A page shows
/// "offline" from <see cref="Online"/> instead of waiting on a call that cannot succeed.
/// </summary>
public sealed class PathingStatus(IServiceProvider services, ILogger<PathingStatus> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim polling = new(1, 1);

    /// <summary>Whether the last poll reached the service.</summary>
    public bool Online { get; private set; }

    /// <summary>Why the last poll failed; null while online.</summary>
    public string? Error { get; private set; } = "not asked yet";

    /// <summary>What the service serves; read when it is first reached, and again when it comes back.</summary>
    public PathingCapabilities? Capabilities { get; private set; }

    public PathingHealth? Health { get; private set; }

    /// <summary>Raised after every poll, on the poller's thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when the service is reached after being down (or for the first time).</summary>
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
        IPathingClient client = services.GetRequiredService<IPathingClient>();
        bool wasOnline = Online;

        ServiceResult<PathingHealth> health = await client.GetHealthAsync(token);
        if (health.Ok)
        {
            if (!wasOnline || Capabilities is null)
            {
                ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(token);
                Capabilities = capabilities.Value ?? Capabilities;
            }

            Health = health.Value;
            Online = true;
            Error = null;
        }
        else
        {
            Online = false;
            Error = health.Error;
        }

        if (wasOnline != Online && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Pathing Service {State}{Reason}", Online ? "online" : "offline",
                Online ? string.Empty : $": {Error}");
        }

        Changed?.Invoke();
        if (Online && !wasOnline)
        {
            Reconnected?.Invoke();
        }
    }

    public override void Dispose()
    {
        polling.Dispose();
        base.Dispose();
    }
}
