using BotAgent.Link;
using BotAgent.Runtime;
using BotAgent.Sync;
using BotAgent.Telemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using ServiceClients.Pathing;

using System;

namespace BotAgent;

public static class AgentRegistration
{
    /// <summary>
    /// The agent: its identity, the run host, the link to the manager (idle without one) and
    /// telemetry. The host adds what plays: <see cref="IBotRuntimeFactory"/>, <see cref="IGameProbe"/>,
    /// <see cref="IClassProfileCatalog"/> and <see cref="IAgentProcess"/>, and any <see cref="IRunPreparation"/>.
    /// <paramref name="sink"/> is the instance its logger writes to.
    /// </summary>
    public static IServiceCollection AddBotAgent(this IServiceCollection services, AgentOptions options, AgentLogSink sink)
    {
        services.AddSingleton(options);
        services.AddSingleton(sink);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(AgentLinkOptions.Default);
        services.AddSingleton(sp => AgentIdentity.LoadOrCreate(options.IdentityFile, options.Name,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentIdentity>()));
        services.AddSingleton<BotRuntimeHost>();
        services.AddSingleton<AgentLink>();
        services.AddSingleton<TelemetryPublisher>();

        services.AddHostedService(sp => sp.GetRequiredService<AgentLink>());
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryPublisher>());
        // Last, so it stops first: the run releases input and reports before telemetry and the link go.
        services.AddHostedService(sp => sp.GetRequiredService<BotRuntimeHost>());
        return services;
    }

    /// <summary>
    /// The data sync as a run's preparation (<see cref="DataSync"/>): before each run, the files it
    /// needs come from the Pathing Service <paramref name="pathing"/> names into <paramref name="data"/>'s
    /// folders. With no service, every run uses this PC's copies and says so. Needs an <see cref="IGameProbe"/>.
    /// </summary>
    public static IServiceCollection AddDataSync(this IServiceCollection services, DataConfig data, PathingServiceOptions? pathing)
    {
        if (pathing is not null)
        {
            services.AddPathingClient(pathing.Url, pathing.ApiKey);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRunPreparation>(sp => new DataSync(sp.GetService<IPathingClient>(), data,
            sp.GetRequiredService<IGameProbe>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DataSync>>()));
        return services;
    }
}
