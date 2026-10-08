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
    /// <param name="identity">The agent's identity, as the process holds it; read from its file when null.</param>
    /// <param name="linked">Whether it dials out to the Bot Manager: not while another process holds the same id.</param>
    /// <param name="startProfile">Whether the command line's profile starts: in the first host only, never in one
    /// built again after a fault (known issue 76).</param>
    /// <param name="data">The data folder the agent syncs into and runs from; <c>data_config.json</c>'s when null,
    /// read once for the host.</param>
    public static IHost Build(IConfiguration configuration, string? environment, RunOptions options, AgentLogSink sink,
        AgentIdentity? identity = null, bool linked = true, bool startProfile = true, DataConfig? data = null)
    {
        data ??= DataConfig.Load();
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
        AgentOptions agent = AgentOptions.From(builder.Configuration);
        // Long enough for a stopped run's grace, its forced release, its abandon and its last report.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = agent.ShutdownTimeout());

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(data);
        builder.Services.AddHostedService<StopOnSignals>();
        builder.Services.AddBotAgent(linked ? agent : agent with { Manager = null }, sink, identity);
        // Before a run, the files it needs from the Pathing Service; without one, this PC's copies.
        builder.Services.AddDataSync(data, PathingServiceOptions.FromIfConfigured(builder.Configuration));
        builder.Services.AddSingleton<CreateRuntime>(sp => ActivatorUtilities.CreateInstance<HeadlessRuntimeFactory>(sp).Create);
        builder.Services.AddSingleton<ProbeGame>(sp => ActivatorUtilities.CreateInstance<WowGameProbe>(sp).Probe);
        builder.Services.AddSingleton<ListClassProfiles>(() => ClassProfileFolder.List(data.Class));

        if (options.ClassConfig is not null && startProfile)
        {
            builder.Services.AddHostedService<StartFromCommandLine>();
        }

        return builder.Build();
    }
}
