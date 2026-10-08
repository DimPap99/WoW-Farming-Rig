using BotAgent;
using BotAgent.Telemetry;

using Contracts.Bots;

using Microsoft.Extensions.Configuration;

using ServiceClients;
using ServiceClients.Bots;

using ServiceTests.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>
/// The agent's run host and what stops with it (known issue 76): a report that lost its kills to a
/// runtime read while disposed, a report lost when the host stopped, a start taken while it stopped, a
/// grace the shutdown did not wait for, and log lines sent again by every host built again.
/// </summary>
public sealed class RunHostRacesTests
{
    private static readonly BotAssignment Trip = BotAssignment.ForForever("crossroads_trip.json", 30);

    [Fact]
    public async Task A_report_keeps_what_the_run_did_when_its_teardown_is_slow()
    {
        using TempDataRoot data = new();
        FakeRuntimeFactory runtimes = new() { DisposeTakes = TimeSpan.FromSeconds(1.5) };
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, null, runtimes);

        agent.Host.Start(Trip);
        await Eventually.True(() => !runtimes.Built.IsEmpty && runtimes.Last.Calls.Contains(FakeRuntime.Ran));
        agent.Host.Stop();
        await Eventually.True(() => agent.Host.LastReport is not null);

        // The publisher read the runtime once a second while it was disposed, as one attaching.
        Assert.Equal(new BotProgress(3, 0, 3), agent.Host.LastReport!.Progress);
    }

    // A run whose result does not say what it did (a runtime that threw) has what a read of it last showed:
    // not the reads while it is torn down, which show nothing done.
    [Fact]
    public async Task A_report_without_the_runs_own_progress_has_what_a_read_last_showed()
    {
        using TempDataRoot data = new();
        FakeRuntimeFactory runtimes = new() { ReportsProgress = false, DisposeTakes = TimeSpan.FromSeconds(1.5) };
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, null, runtimes);

        agent.Host.Start(Trip);
        await Eventually.True(() => !runtimes.Built.IsEmpty && runtimes.Last.Calls.Contains(FakeRuntime.Ran));
        agent.Host.Read();
        agent.Host.Stop();
        await Eventually.True(() => agent.Host.LastReport is not null);

        Assert.Equal(new BotProgress(3, 0, 3), agent.Host.LastReport!.Progress);
    }

    [Fact]
    public async Task The_report_of_a_run_the_stop_of_the_host_ended_reaches_the_manager()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(Path.Combine(data.Root, "manager")));
        BotManagerClient client = new(manager.CreateClient());
        FakeRuntimeFactory runtimes = new();
        string agentId;
        await using (AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, slot, runtimes))
        {
            agentId = agent.Identity.AgentId;
            await Eventually.True(() => agent.Link.Connected);
            agent.Host.Start(Trip);
            await Eventually.True(() => !runtimes.Built.IsEmpty && runtimes.Last.Calls.Contains(FakeRuntime.Ran));
        }

        ServiceResult<RunRecord[]> runs = await client.GetRunsAsync(agentId, TestContext.Current.CancellationToken);

        Assert.Equal(BotRunOutcome.Stopped, Assert.Single(runs.Value!).Report.Outcome);
    }

    [Fact]
    public async Task A_start_while_the_host_stops_is_refused()
    {
        using TempDataRoot data = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, null, new FakeRuntimeFactory());

        await agent.Host.StopAsync(TestContext.Current.CancellationToken);
        CommandAck started = agent.Host.Start(Trip);

        Assert.False(started.Accepted);
        Assert.Equal("the agent is stopping", started.Reason);
    }

    [Theory]
    [InlineData(10000, 10000, 25)]
    [InlineData(120000, 60000, 100)]
    public void The_grace_is_bounded_and_the_shutdown_waits_for_it(int configured, int graceMs, int shutdownSeconds)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentOptions.StopGraceMsKey] = configured.ToString() })
            .Build();

        AgentOptions options = AgentOptions.From(configuration);

        Assert.Equal(TimeSpan.FromMilliseconds(graceMs), options.StopGrace);
        Assert.Equal(TimeSpan.FromSeconds(shutdownSeconds), options.ShutdownTimeout());
    }

    [Fact]
    public void Lines_sent_once_are_not_sent_again_by_a_host_built_again()
    {
        AgentLogSink sink = new();
        Serilog.ILogger log = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        log.Information("one");
        log.Information("two");

        BotLogLine[] first = sink.ReadUnsent(10, out long last);
        sink.MarkSent(last);
        log.Information("three");
        // A new host's publisher starts with nothing of its own: the sink says what went.
        BotLogLine[] next = sink.ReadUnsent(10, out long end);
        sink.MarkSent(1);

        Assert.Equal(["one", "two"], first.Select(line => line.Message));
        Assert.Equal(["three"], next.Select(line => line.Message));
        Assert.Equal(["three"], sink.ReadUnsent(10, out _).Select(line => line.Message));
        Assert.True(end > last);
    }

    [Fact]
    public void An_identity_file_that_cannot_be_read_is_not_replaced()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Skip("needs Unix file rights that bind the user running the tests");
        }

        using TempDataRoot data = new();
        string file = Path.Combine(data.Root, "agent_identity.json");
        AgentIdentity.LoadOrCreate(file, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        byte[] before = File.ReadAllBytes(file);
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                AgentIdentity.LoadOrCreate(file, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(before, File.ReadAllBytes(file));
    }
}
