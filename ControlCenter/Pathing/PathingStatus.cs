using ControlCenter.Hosting;

using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>
/// The Pathing Service's state for every page and the status bar (see <see cref="ServiceStatus"/>),
/// with what it serves and its health.
/// </summary>
public sealed class PathingStatus(IServiceProvider services, PathingServiceOptions options, ILogger<PathingStatus> logger)
    : ServiceStatus(Describe(options), logger)
{
    /// <summary>What the service serves; read when it is first reached, and again when it comes back.</summary>
    public PathingCapabilities? Capabilities { get; private set; }

    public PathingHealth? Health { get; private set; }

    protected override async Task<string?> ReadAsync(bool wasOnline, CancellationToken token)
    {
        IPathingClient client = services.GetRequiredService<IPathingClient>();

        ServiceResult<PathingHealth> health = await client.GetHealthAsync(token);
        if (!health.Ok)
        {
            return health.Error ?? "it answered without its health";
        }

        if (!wasOnline || Capabilities is null)
        {
            ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(token);
            Capabilities = capabilities.Value ?? Capabilities;
        }

        Health = health.Value;
        return null;
    }

    private static ServiceDescription Describe(PathingServiceOptions options)
    {
        return new ServiceDescription("Pathing Service", options.Url, PathingServiceOptions.UrlKey,
            "PathingService/run.sh", "add --exp=forever for WoW: Forever");
    }
}
