using ControlCenter.Hosting;

using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using static System.Diagnostics.Stopwatch;

namespace ControlCenter.Pathing;

/// <summary>
/// The Pathing Service's state for every page and the status bar (see <see cref="ServiceStatus"/>),
/// with what it serves and its health.
/// <para>
/// Only the Pathing Service's own health counts: a body without its members (another service, the
/// Bot Manager has the same route; an older build) or of another API version is the service offline,
/// with why, and never reaches a page. Pages read again (<see cref="ServiceStatus.Reconnected"/>) when
/// the service restarted, however quickly (<see cref="PathingHealth.Instance"/>), and when the
/// continents with tiles changed under it: a bake, a download, a cache clear.
/// </para>
/// </summary>
public sealed class PathingStatus(IServiceProvider services, PathingServiceOptions options, ILogger<PathingStatus> logger)
    : ServiceStatus(Describe(options), logger)
{
    /// <summary>How long what the service serves is taken as read: game files can arrive while it runs.</summary>
    public static readonly TimeSpan CapabilitiesRecheck = TimeSpan.FromSeconds(30);

    private long capabilitiesReadAt;

    /// <summary>What the service serves; read when it is first reached, when it changed, and every <see cref="CapabilitiesRecheck"/>.</summary>
    public PathingCapabilities? Capabilities { get; private set; }

    /// <summary>The service's last health; null while it is offline.</summary>
    public PathingHealth? Health { get; private set; }

    protected override async Task<string?> ReadAsync(bool wasOnline, CancellationToken token)
    {
        IPathingClient client = services.GetRequiredService<IPathingClient>();

        ServiceResult<PathingHealth> health = await client.GetHealthAsync(token);
        string? wrong = health.Ok ? NotThisServicesHealth(health.Value) : health.Error ?? "it answered without its health";
        if (wrong is not null)
        {
            Health = null;
            return wrong;
        }

        PathingHealth now = health.Value!;
        bool changed = Health is PathingHealth before &&
                       (before.Instance != now.Instance || !before.Coverage.Where(c => c.Tiles > 0).Select(c => c.Continent)
                           .SequenceEqual(now.Coverage.Where(c => c.Tiles > 0).Select(c => c.Continent)));

        if (!wasOnline || changed || Capabilities is null || GetElapsedTime(capabilitiesReadAt) >= CapabilitiesRecheck)
        {
            ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(token);
            if (capabilities.Value is PathingCapabilities served)
            {
                if (served.ApiVersion != PathingApi.Version)
                {
                    Health = null;
                    return $"it speaks version {served.ApiVersion} of the pathing API, and this Control Center version {PathingApi.Version}: " +
                           "run the two from the same build";
                }

                Capabilities = served;
                capabilitiesReadAt = GetTimestamp();
            }
        }

        Health = now;
        if (changed)
        {
            ReadAgain();
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="health"/> is not this version's Pathing Service's health, or null. The client
    /// reads a member the body lacks as null rather than failing, so this is where a body from
    /// elsewhere is caught, before a page draws it.
    /// </summary>
    public static string? NotThisServicesHealth(PathingHealth? health)
    {
        string? missing = health switch
        {
            null => "the body",
            { Client: null } => nameof(PathingHealth.Client),
            { Coverage: null } => nameof(PathingHealth.Coverage),
            { Lanes: null } => nameof(PathingHealth.Lanes),
            { Bake: null } => nameof(PathingHealth.Bake),
            { Instance: null } => nameof(PathingHealth.Instance),
            _ => null,
        };

        return missing is null
            ? null
            : $"what answers at {PathingApi.Health} is not the Pathing Service of this version ({missing} is missing): " +
              $"does {PathingServiceOptions.UrlKey} name another service, or is the service an older build?";
    }

    private static ServiceDescription Describe(PathingServiceOptions options)
    {
        return new ServiceDescription("Pathing Service", options.Url, PathingServiceOptions.UrlKey,
            "PathingService/run.sh", "add --exp=forever for WoW: Forever");
    }
}
