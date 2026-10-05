using ControlCenter.Hosting;

using Contracts.Bots;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Bots;

/// <summary>
/// The Bot Manager's state for the fleet pages and the status bar (see <see cref="ServiceStatus"/>):
/// whether it answers, and how many bots it knows and has online.
/// </summary>
public sealed class BotManagerStatus(IServiceProvider services, BotManagerOptions options, ILogger<BotManagerStatus> logger)
    : ServiceStatus(Describe(options), logger)
{
    /// <summary>The manager's last answer; kept while it is down.</summary>
    public ManagerHealth? Health { get; private set; }

    protected override async Task<string?> ReadAsync(bool wasOnline, CancellationToken token)
    {
        ServiceResult<ManagerHealth> health = await services.GetRequiredService<IBotManagerClient>().GetHealthAsync(token);
        if (!health.Ok)
        {
            return health.Error ?? "it answered without its health";
        }

        Health = health.Value;
        return null;
    }

    private static ServiceDescription Describe(BotManagerOptions options)
    {
        return new ServiceDescription("Bot Manager", options.Url, BotManagerOptions.UrlKey, "BotManager/run.sh");
    }
}
