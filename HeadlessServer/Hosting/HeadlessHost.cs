using BotAgent;
using BotAgent.Runtime;
using BotAgent.Telemetry;

using HeadlessServer.Runtimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

using ServiceClients.Pathing;

namespace HeadlessServer.Hosting;

/// <summary>
/// HeadlessServer on the Generic Host: the bot agent (BotAgent) with Core's runtimes and the data
/// sync. Ctrl+C, closing the window or a logoff (<see cref="StopOnSignals"/>) stops the host, which
/// stops a run the way a stop command does: input released first, then the run torn down. Task
/// Scheduler's End terminates the process.
/// </summary>
public static class HeadlessHost
{
    /// <summary>Long enough for a stopped run's grace period, its forced release and its last report.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    public static IHost Build(IConfiguration configuration, string? environment, RunOptions options, AgentLogSink sink)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = Directory.GetCurrentDirectory(),
            EnvironmentName = environment ?? Environments.Production,
        });

        builder.Configuration.AddConfiguration(configuration);
        builder.Configuration.AddInMemoryCollection(options.ConfigurationValues());
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: false);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);

        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<StopOnSignals>();
        builder.Services.AddBotAgent(AgentOptions.From(builder.Configuration), sink);
        // Before a run, the files it needs from the Pathing Service; without one, this PC's copies.
        builder.Services.AddDataSync(DataConfig.Load(), PathingServiceOptions.FromIfConfigured(builder.Configuration));
        builder.Services.AddSingleton<IBotRuntimeFactory, HeadlessRuntimeFactory>();
        builder.Services.AddSingleton<IGameProbe, WowGameProbe>();
        builder.Services.AddSingleton<IClassProfileCatalog, ClassProfileFolder>();
        builder.Services.AddSingleton<IAgentProcess, AgentProcess>();

        if (options.ClassConfig is not null)
        {
            builder.Services.AddHostedService<StartFromCommandLine>();
        }

        return builder.Build();
    }
}
