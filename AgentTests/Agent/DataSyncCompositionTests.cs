using BotAgent.Runtime;
using BotAgent.Sync;
using BotAgent.Telemetry;

using Core;

using HeadlessServer;
using HeadlessServer.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ServiceClients.Pathing;

using System.Collections.Generic;

using Xunit;

namespace AgentTests.Agent;

/// <summary>The data sync as HeadlessServer composes it, and the profile names it reads, which are Core's.</summary>
public sealed class DataSyncCompositionTests
{
    [Fact]
    public void The_names_the_sync_reads_a_profile_by_are_Cores()
    {
        Assert.Equal(nameof(ClassConfiguration.PathFilename), ProfilePaths.PathFilename);
        Assert.Equal(nameof(ClassConfiguration.OverridePathFilename), ProfilePaths.OverridePathFilename);
        Assert.Equal(nameof(ClassConfiguration.PathsFilenames), ProfilePaths.PathsFilenames);
        Assert.Equal(nameof(PathSettings.PathFilename), ProfilePaths.PathFilename);
        Assert.Equal(nameof(PathSettings.OverridePathFilename), ProfilePaths.OverridePathFilename);
        Assert.Equal(nameof(KeyAction.PathFilename), ProfilePaths.PathFilename);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://botbox:5010")]
    public void Every_run_is_synced_first_with_or_without_a_Pathing_Service(string? pathing)
    {
        Dictionary<string, string?> settings = pathing is null ? [] : new() { [PathingServiceOptions.UrlKey] = pathing };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        using IHost host = HeadlessHost.Build(configuration, null, new RunOptions { Agent = true }, new AgentLogSink());

        Assert.IsType<DataSync>(Assert.Single(host.Services.GetServices<IRunPreparation>()));
        Assert.Equal(pathing is not null, host.Services.GetService<IPathingClient>() is not null);
    }

    // A host built again after a fault started the command line's profile again, unasked (known issue 76).
    [Fact]
    public void Only_the_first_host_starts_the_command_lines_profile()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        RunOptions options = new() { Agent = true, ClassConfig = "Warrior_1.json" };

        using IHost first = HeadlessHost.Build(configuration, null, options, new AgentLogSink());
        using IHost again = HeadlessHost.Build(configuration, null, options, new AgentLogSink(), startProfile: false);

        Assert.Contains(first.Services.GetServices<IHostedService>(), service => service is StartFromCommandLine);
        Assert.DoesNotContain(again.Services.GetServices<IHostedService>(), service => service is StartFromCommandLine);
    }
}
