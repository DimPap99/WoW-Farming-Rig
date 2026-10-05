using Contracts.Bots;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// The Bot Manager end to end, in memory: fake agents dial in over the agent hub, the Control
/// Center's client drives it over REST, and a listener on the fleet hub sees what the Control
/// Center would.
/// </summary>
public sealed class BotManagerTests
{
    private const string AgentId = "3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f";

    private static readonly BotAssignment Trip = BotAssignment.ForForever("crossroads_trip.json", 30);

    [Fact]
    public async Task A_registered_agent_is_an_online_bot_with_what_it_reported()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FleetListener listener = await FleetListener.ConnectAsync(factory);
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);

        AgentWelcome welcome = await agent.RegisterAsync();
        BotInfo changed = await FleetListener.NextAsync(listener.Changed, bot => bot.AgentId == AgentId);
        ServiceResult<BotInfo[]> listed = await client.ListBotsAsync(TestContext.Current.CancellationToken);
        ServiceResult<ManagerHealth> health = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(welcome.Accepted, welcome.Reason);
        Assert.Null(welcome.Assignment);
        Assert.True(changed.Online);
        BotInfo bot = Assert.Single(listed.Value!);
        Assert.Equal("Test PC", bot.Name);
        Assert.Equal("TESTBOX", bot.Machine);
        Assert.Equal(FakeAgent.Version, bot.AgentVersion);
        Assert.Equal("forever", bot.Game!.Client);
        Assert.Equal(["Hunter_10.json", "Forever/Paladin_Forever_Auto.json"], bot.ClassProfiles);
        Assert.True(bot.Online);
        Assert.Equal(new ManagerHealth(BotManagerApi.Version, BotManagerApi.AgentProtocol, 1, 1), health.Value);
    }

    [Fact]
    public async Task An_agent_speaking_another_protocol_or_with_a_bad_id_is_refused_with_why()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);

        AgentWelcome older = await agent.RegisterAsync(FakeAgent.Hello(AgentId, protocol: BotManagerApi.AgentProtocol + 1));
        AgentWelcome badId = await agent.RegisterAsync(FakeAgent.Hello("../escape"));
        ServiceResult<BotInfo[]> listed = await client.ListBotsAsync(TestContext.Current.CancellationToken);

        Assert.False(older.Accepted);
        Assert.Contains("protocol", older.Reason);
        Assert.False(badId.Accepted);
        Assert.Empty(listed.Value!);
    }

    [Fact]
    public async Task An_assignment_is_kept_sent_to_the_online_agent_and_given_back_at_register()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using (FakeAgent first = await FakeAgent.ConnectAsync(factory, AgentId))
        {
            await first.RegisterAsync();

            ServiceResult assigned = await client.SetAssignmentAsync(AgentId, Trip, TestContext.Current.CancellationToken);

            Assert.True(assigned.Ok, assigned.Error);
            await WaitUntil(() => !first.Assignments.IsEmpty);
            Assert.True(first.Assignments.TryPeek(out BotAssignment? pushed));
            Assert.Equal(Trip, pushed);
        }

        await using FakeAgent again = await FakeAgent.ConnectAsync(factory, AgentId);
        AgentWelcome welcome = await again.RegisterAsync();
        ServiceResult<BotInfo> bot = await client.GetBotAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.Equal(Trip, welcome.Assignment);
        Assert.Equal(Trip, bot.Value!.Assignment);
    }

    [Fact]
    public async Task An_incomplete_assignment_is_refused_with_why()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);
        await agent.RegisterAsync();

        ServiceResult tooLong = await client.SetAssignmentAsync(AgentId, BotAssignment.ForForever("trip.json", 601), TestContext.Current.CancellationToken);
        ServiceResult escape = await client.SetAssignmentAsync(AgentId, BotAssignment.ForClassic("../Hunter.json"), TestContext.Current.CancellationToken);
        ServiceResult unknown = await client.SetAssignmentAsync("nobody", Trip, TestContext.Current.CancellationToken);

        Assert.Equal(400, tooLong.Status);
        Assert.Contains("minutes", tooLong.Error);
        Assert.Equal(400, escape.Status);
        Assert.Equal(404, unknown.Status);
    }

    [Fact]
    public async Task Start_and_stop_reach_the_agent_over_its_connection_and_its_answers_come_back()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);
        await agent.RegisterAsync();

        ServiceResult<CommandAck> unassigned = await client.StartAsync(AgentId, TestContext.Current.CancellationToken);
        await client.SetAssignmentAsync(AgentId, Trip, TestContext.Current.CancellationToken);
        ServiceResult<CommandAck> started = await client.StartAsync(AgentId, TestContext.Current.CancellationToken);
        agent.Answer = command => new CommandAck(false, "a run is already under way", null);
        ServiceResult<CommandAck> busy = await client.StartAsync(AgentId, TestContext.Current.CancellationToken);
        agent.Answer = command => new CommandAck(true, null, null);
        ServiceResult<CommandAck> stopped = await client.StopAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.Equal(409, unassigned.Status);
        Assert.Contains("no assignment", unassigned.Error);
        Assert.True(started.Ok, started.Error);
        Assert.Equal("run-1", started.Value.RunId);
        Assert.Equal(409, busy.Status);
        Assert.Equal("a run is already under way", busy.Error);
        Assert.True(stopped.Ok, stopped.Error);
        BotCommand[] commands = [.. agent.Commands];
        Assert.Equal([BotCommandKind.Start, BotCommandKind.Start, BotCommandKind.Stop], commands.Select(c => c.Kind));
        Assert.Equal(Trip, commands[0].Assignment);
        Assert.Null(commands[2].Assignment);
    }

    [Fact]
    public async Task A_command_to_an_offline_bot_or_one_that_does_not_answer_says_so()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root, commandTimeoutSeconds: 1);
        BotManagerClient client = new(factory.CreateClient());
        await using (FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId))
        {
            await agent.RegisterAsync();
            await client.SetAssignmentAsync(AgentId, Trip, TestContext.Current.CancellationToken);
            agent.Answer = command =>
            {
                Task.Delay(TimeSpan.FromSeconds(3)).Wait();
                return new CommandAck(true, null, "late");
            };

            ServiceResult<CommandAck> silent = await client.StartAsync(AgentId, TestContext.Current.CancellationToken);

            Assert.Equal(504, silent.Status);
            Assert.Contains("did not answer", silent.Error);
        }

        await WaitUntil(async () => (await client.GetBotAsync(AgentId, TestContext.Current.CancellationToken)).Value is { Online: false });
        ServiceResult<CommandAck> offline = await client.StopAsync(AgentId, TestContext.Current.CancellationToken);
        ServiceResult<CommandAck> unknown = await client.StopAsync("nobody", TestContext.Current.CancellationToken);

        Assert.Equal(409, offline.Status);
        Assert.Contains("offline", offline.Error);
        Assert.Equal(404, unknown.Status);
    }

    [Fact]
    public async Task Snapshots_logs_and_ended_runs_reach_the_fleet_and_are_kept()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FleetListener listener = await FleetListener.ConnectAsync(factory);
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);
        await agent.RegisterAsync();
        BotRunInfo run = new("run-7", BotRuntimeKind.Forever, "route crossroads_trip.json", DateTimeOffset.UtcNow);

        // An agent claiming another's id in its snapshot is still itself.
        await agent.SendSnapshotAsync(new BotSnapshot("someone-else", DateTimeOffset.UtcNow, BotRunState.Running, run, null,
            new BotCharacter("Hunter", 12, "Orc"), new BotVitals(80, 45, 30, true), new BotPosition(1413, 51.9f, 29.9f),
            "Engage/Fight", "task 1 of 1", new BotProgress(3, 0, 2), null));
        await agent.SendLogAsync(new BotLogLine(DateTimeOffset.UtcNow, "Information", "ForeverRun", "Autonomous phase: Engage"));
        BotSnapshot streamed = await FleetListener.NextAsync(listener.Snapshots, _ => true);
        (string logAgent, BotLogLine[] lines) = await FleetListener.NextAsync(listener.Logs, _ => true);
        ServiceResult<BotInfo> live = await client.GetBotAsync(AgentId, TestContext.Current.CancellationToken);

        await agent.SendRunEndedAsync(new RunReport(run, BotRunOutcome.Complete, DateTimeOffset.UtcNow, "status: Complete", 0,
            new BotProgress(5, 0, 4)));
        RunRecord ended = await FleetListener.NextAsync(listener.Runs, _ => true);
        ServiceResult<RunRecord[]> runs = await client.GetRunsAsync(AgentId, TestContext.Current.CancellationToken);
        ServiceResult<BotLogLine[]> log = await client.GetLogAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.Equal(AgentId, streamed.AgentId);
        Assert.Equal(new BotVitals(80, 45, 30, true), streamed.Vitals);
        Assert.Equal(AgentId, logAgent);
        Assert.Equal("Autonomous phase: Engage", Assert.Single(lines).Message);
        Assert.Equal(BotRunState.Running, live.Value!.Latest!.State);
        Assert.Equal("run-7", live.Value.Run!.RunId);
        Assert.Equal(AgentId, ended.AgentId);
        Assert.Equal(BotRunOutcome.Complete, Assert.Single(runs.Value!).Report.Outcome);
        Assert.Equal("ForeverRun", Assert.Single(log.Value!).Source);
    }

    [Fact]
    public async Task A_disconnect_takes_the_bot_offline_and_the_registry_outlives_the_manager()
    {
        using TempDataRoot data = new();
        BotRunInfo run = new("run-1", BotRuntimeKind.Forever, "route crossroads_trip.json", DateTimeOffset.UtcNow);
        using (BotManagerFactory factory = new(data.Root))
        {
            BotManagerClient client = new(factory.CreateClient());
            await using FleetListener listener = await FleetListener.ConnectAsync(factory);
            await using (FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId))
            {
                await agent.RegisterAsync();
                await client.SetAssignmentAsync(AgentId, Trip, TestContext.Current.CancellationToken);
                await agent.SendRunEndedAsync(new RunReport(run, BotRunOutcome.Stopped, DateTimeOffset.UtcNow, "stopped", 3, null));
                await FleetListener.NextAsync(listener.Runs, _ => true);
            }

            BotInfo offline = await FleetListener.NextAsync(listener.Changed, bot => !bot.Online);
            Assert.Equal(AgentId, offline.AgentId);
        }

        using BotManagerFactory restarted = new(data.Root);
        BotManagerClient after = new(restarted.CreateClient());
        BotInfo kept = Assert.Single((await after.ListBotsAsync(TestContext.Current.CancellationToken)).Value!);
        ServiceResult<RunRecord[]> runs = await after.GetRunsAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.False(kept.Online);
        Assert.Equal(Trip, kept.Assignment);
        Assert.Equal("Test PC", kept.Name);
        Assert.Equal(BotRunOutcome.Stopped, Assert.Single(runs.Value!).Report.Outcome);

        // An agent reconnecting mid-run reports the run it is in.
        await using FakeAgent back = await FakeAgent.ConnectAsync(restarted, AgentId);
        await back.RegisterAsync(FakeAgent.Hello(AgentId, run: run));
        ServiceResult<BotInfo> online = await after.GetBotAsync(AgentId, TestContext.Current.CancellationToken);
        Assert.True(online.Value!.Online);
        Assert.Equal("run-1", online.Value.Run!.RunId);
    }

    [Fact]
    public async Task Only_an_offline_bot_can_be_forgotten()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FleetListener listener = await FleetListener.ConnectAsync(factory);
        await using (FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId))
        {
            await agent.RegisterAsync();
            ServiceResult whileOnline = await client.ForgetBotAsync(AgentId, TestContext.Current.CancellationToken);
            Assert.Equal(409, whileOnline.Status);
        }

        await FleetListener.NextAsync(listener.Changed, bot => !bot.Online);
        ServiceResult forgotten = await client.ForgetBotAsync(AgentId, TestContext.Current.CancellationToken);
        string removed = await FleetListener.NextAsync(listener.Removed, _ => true);
        ServiceResult again = await client.ForgetBotAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.True(forgotten.Ok, forgotten.Error);
        Assert.Equal(AgentId, removed);
        Assert.Equal(404, again.Status);
        Assert.Empty((await client.ListBotsAsync(TestContext.Current.CancellationToken)).Value!);
    }

    private static Task WaitUntil(Func<bool> condition)
    {
        return WaitUntil(() => Task.FromResult(condition()));
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + FleetListener.Wait;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold in time");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
