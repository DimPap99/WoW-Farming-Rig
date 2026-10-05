using BotAgent;
using BotAgent.Telemetry;

using Contracts.Bots;

using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Events;
using Serilog.Parsing;

using ServiceClients;
using ServiceClients.Bots;

using ServiceTests.Bots;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>
/// The agent (BotAgent) against the in-memory Bot Manager, with runtimes that play nothing: it dials
/// out and registers, obeys start and stop over its own connection, stops a run by letting it
/// release first, forces one that does not end, and outlives the manager.
/// </summary>
public sealed class AgentLinkTests
{
    private static readonly BotAssignment Trip = BotAssignment.ForForever("crossroads_trip.json", 30);

    [Fact]
    public async Task An_agent_registers_streams_and_obeys_start_and_stop_releasing_before_teardown()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(Path.Combine(data.Root, "manager")));
        BotManagerClient client = new(manager.CreateClient());
        await using FleetListener fleet = await FleetListener.ConnectAsync(manager);
        FakeRuntimeFactory runtimes = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, slot, runtimes);

        BotInfo online = await FleetListener.NextAsync(fleet.Changed, bot => bot.Online);
        await client.SetAssignmentAsync(online.AgentId, Trip, TestContext.Current.CancellationToken);
        ServiceResult<CommandAck> started = await client.StartAsync(online.AgentId, TestContext.Current.CancellationToken);
        BotSnapshot running = await FleetListener.NextAsync(fleet.Snapshots, s => s.State == BotRunState.Running);
        ServiceResult<CommandAck> again = await client.StartAsync(online.AgentId, TestContext.Current.CancellationToken);
        ServiceResult<CommandAck> stopped = await client.StopAsync(online.AgentId, TestContext.Current.CancellationToken);
        RunRecord ended = await FleetListener.NextAsync(fleet.Runs, _ => true);
        BotSnapshot idle = await FleetListener.NextAsync(fleet.Snapshots, s => s.State == BotRunState.Idle);

        Assert.Equal(agent.Identity.AgentId, online.AgentId);
        Assert.Equal("Test agent", online.Name);
        Assert.Equal("forever", online.Game!.Client);
        Assert.True(started.Ok, started.Error);
        Assert.Equal(started.Value.RunId, running.Run!.RunId);
        Assert.Equal(new BotVitals(90, 50, null, false), running.Vitals);
        Assert.Equal(409, again.Status);
        Assert.Contains("already under way", again.Error);
        Assert.True(stopped.Ok, stopped.Error);
        Assert.Equal(BotRunOutcome.Stopped, ended.Report.Outcome);
        Assert.Equal(3, ended.Report.Progress!.Kills);
        Assert.Contains("ended Stopped", idle.Message);
        Assert.Equal([FakeRuntime.Ran, FakeRuntime.Released, FakeRuntime.Disposed], runtimes.Last.Calls);
    }

    [Fact]
    public async Task A_refused_run_is_reported_and_the_agent_stays_ready()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(Path.Combine(data.Root, "manager")));
        await using FleetListener fleet = await FleetListener.ConnectAsync(manager);
        FakeRuntimeFactory runtimes = new() { Refusal = "no WoW client is running on this PC" };
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, slot, runtimes);
        await FleetListener.NextAsync(fleet.Changed, bot => bot.Online);

        Assert.True(agent.Host.Start(Trip).Accepted);
        RunRecord refused = await FleetListener.NextAsync(fleet.Runs, _ => true);
        runtimes.Refusal = null;
        CommandAck next = agent.Host.Start(Trip);

        Assert.Equal(BotRunOutcome.Refused, refused.Report.Outcome);
        Assert.Equal("no WoW client is running on this PC", refused.Report.Message);
        Assert.True(next.Accepted, next.Reason);
        agent.Host.Stop();
    }

    [Fact]
    public async Task A_run_that_does_not_end_is_released_from_outside_then_abandoned_and_the_agent_restarts()
    {
        using TempDataRoot data = new();
        FakeRuntimeFactory runtimes = new() { IgnoreStop = true };
        FakeProcess process = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, null, runtimes, process, graceMs: 300);
        RunReport? report = null;
        agent.Host.RunEnded += ended => report = ended;

        agent.Host.Start(Trip);
        await Eventually.True(() => !runtimes.Built.IsEmpty && runtimes.Last.Calls.Contains(FakeRuntime.Ran));
        agent.Host.Stop();
        await Eventually.True(() => report is not null);

        Assert.Equal(BotRunOutcome.Abandoned, report!.Outcome);
        Assert.Contains(FakeRuntime.Forced, runtimes.Last.Calls);
        Assert.Single(process.Restarts);
        // Let it go: the run that finally ends is not reported a second time.
        runtimes.Last.LetGo();
        await Eventually.True(() => runtimes.Last.Calls.Contains(FakeRuntime.Disposed));
        Assert.Equal(BotRunOutcome.Abandoned, agent.Host.LastReport!.Outcome);
    }

    [Fact]
    public async Task The_agent_outlives_the_manager_and_reports_what_ended_while_it_was_down()
    {
        using TempDataRoot data = new();
        string managerData = Path.Combine(data.Root, "manager");
        ManagerSlot slot = new();
        FakeRuntimeFactory runtimes = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, slot, runtimes);

        // The manager comes up after the agent.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(agent.Link.Connected);
        using (BotManagerFactory first = slot.Bring(new BotManagerFactory(managerData)))
        {
            BotManagerClient client = new(first.CreateClient());
            await Eventually.True(() => agent.Link.Connected);
            await client.SetAssignmentAsync(agent.Identity.AgentId, Trip, TestContext.Current.CancellationToken);
            Assert.True((await client.StartAsync(agent.Identity.AgentId, TestContext.Current.CancellationToken)).Ok);
            slot.Clear();
        }

        // While it is down the run goes on, then ends; the bot keeps no line open.
        await Eventually.True(() => !agent.Link.Connected);
        Assert.NotNull(agent.Host.CurrentRun);
        agent.Host.Stop();
        await Eventually.True(() => agent.Host.CurrentRun is null);

        using BotManagerFactory second = slot.Bring(new BotManagerFactory(managerData));
        BotManagerClient after = new(second.CreateClient());
        RunRecord[] runs = await Eventually.Value(async () => (await after.GetRunsAsync(agent.Identity.AgentId,
            TestContext.Current.CancellationToken)).Value, found => found.Length == 1);
        BotInfo bot = (await after.GetBotAsync(agent.Identity.AgentId, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(BotRunOutcome.Stopped, runs[0].Report.Outcome);
        Assert.True(bot.Online);
        Assert.Equal(Trip, bot.Assignment);
    }

    [Fact]
    public async Task Without_a_manager_the_agent_runs_on_its_own()
    {
        using TempDataRoot data = new();
        FakeRuntimeFactory runtimes = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(data.Root, null, runtimes);

        CommandAck started = agent.Host.Start(Trip);
        await Eventually.True(() => agent.Telemetry.Take().State == BotRunState.Running);
        BotSnapshot snapshot = agent.Telemetry.Take();
        agent.Host.Stop();
        await Eventually.True(() => agent.Host.CurrentRun is null);

        Assert.True(started.Accepted);
        Assert.False(agent.Link.Connected);
        Assert.Equal(agent.Identity.AgentId, snapshot.AgentId);
        Assert.Equal("forever", snapshot.Game!.Client);
        Assert.Equal(BotRunOutcome.Stopped, agent.Host.LastReport!.Outcome);
    }

    [Fact]
    public void An_agent_keeps_its_id_and_takes_a_new_one_only_when_it_has_none()
    {
        using TempDataRoot data = new();
        string file = Path.Combine(data.Root, "agent_identity.json");

        AgentIdentity first = AgentIdentity.LoadOrCreate(file, null, NullLogger.Instance);
        AgentIdentity again = AgentIdentity.LoadOrCreate(file, "Named", NullLogger.Instance);
        File.WriteAllText(file, "not json");
        AgentIdentity fresh = AgentIdentity.LoadOrCreate(file, null, NullLogger.Instance);

        Assert.True(AssignmentRules.IsAgentId(first.AgentId));
        Assert.Equal(first.AgentId, again.AgentId);
        Assert.Equal(Environment.MachineName, first.Name);
        Assert.Equal("Named", again.Name);
        Assert.NotEqual(first.AgentId, fresh.AgentId);
    }

    [Fact]
    public void The_log_stream_numbers_its_lines_keeps_what_a_person_reads_and_shows_text_as_the_file_does()
    {
        AgentLogSink sink = new();
        MessageTemplate template = new MessageTemplateParser().Parse("{Run} ended {Outcome}");

        sink.Emit(Event(LogEventLevel.Debug, template, "ignored", 1));
        sink.Emit(Event(LogEventLevel.Information, template, "route trip.json", 2));
        sink.Emit(Event(LogEventLevel.Warning, template, "route loop.json", 3));
        BotLogLine[] first = sink.ReadAfter(0, 1, out long last);
        BotLogLine[] rest = sink.ReadAfter(last, 10, out long end);

        Assert.Equal("route trip.json ended 2", Assert.Single(first).Message);
        Assert.Equal("BotRuntimeHost", first[0].Source);
        Assert.Equal("Warning", Assert.Single(rest).Level);
        Assert.Empty(sink.ReadAfter(end, 10, out _));
    }

    private static LogEvent Event(LogEventLevel level, MessageTemplate template, string run, int outcome)
    {
        return new LogEvent(DateTimeOffset.UtcNow, level, null, template,
        [
            new LogEventProperty("Run", new ScalarValue(run)),
            new LogEventProperty("Outcome", new ScalarValue(outcome)),
            new LogEventProperty("SourceContext", new ScalarValue("BotAgent.Runtime.BotRuntimeHost")),
        ]);
    }
}
