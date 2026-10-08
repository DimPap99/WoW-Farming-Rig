using ControlCenter.Bots;

using Contracts.Bots;

using ServiceTests.Agent;
using ServiceTests.Bots;

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>
/// The Control Center's fleet feed against the Bot Manager in memory, with fake agents dialling in:
/// what the pages read instead of polling.
/// </summary>
public sealed class FleetFeedTests
{
    private const string AgentId = "5a1c0e2b9d7f4a3c8b6e1d0f2a4c6e8b";

    private static readonly BotRunInfo Run = new("run-3", BotRuntimeKind.Forever, "route crossroads_trip.json for at most 30 min",
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_registered_agent_appears_and_its_snapshots_update_its_latest_state()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(data.Root));
        await using FeedUnderTest feed = await FeedUnderTest.StartAsync(slot);
        await Eventually.True(() => feed.Feed.Connected);

        await using FakeAgent agent = await FakeAgent.ConnectAsync(manager, AgentId);
        await agent.RegisterAsync();
        BotInfo registered = await BotAsync(feed, bot => bot.Online);

        await agent.SendSnapshotAsync(Snapshot(BotRunState.Running, kills: 4));
        BotInfo playing = await BotAsync(feed, bot => bot.Latest is not null);

        Assert.Equal("Test PC", registered.Name);
        Assert.Equal("forever", registered.Game!.Client);
        Assert.Equal(BotRunState.Running, playing.Latest!.State);
        Assert.Equal(4, playing.Latest.Progress!.Kills);
        Assert.Equal(Run.RunId, playing.Run!.RunId);
        Assert.Equal([registered.AgentId], [.. feed.Feed.Bots.Select(bot => bot.AgentId)]);
    }

    [Fact]
    public async Task Log_lines_and_ended_runs_are_raised_for_the_consoles()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(data.Root));
        await using FeedUnderTest feed = await FeedUnderTest.StartAsync(slot);
        Channel<(string AgentId, BotLogLine[] Lines)> logs = Channel.CreateUnbounded<(string, BotLogLine[])>();
        Channel<RunRecord> runs = Channel.CreateUnbounded<RunRecord>();
        feed.Feed.Log += (agentId, lines) => logs.Writer.TryWrite((agentId, lines));
        feed.Feed.RunEnded += run => runs.Writer.TryWrite(run);
        await Eventually.True(() => feed.Feed.Connected);

        await using FakeAgent agent = await FakeAgent.ConnectAsync(manager, AgentId);
        await agent.RegisterAsync();
        BotLogLine line = new(DateTimeOffset.UtcNow, "Information", "ForeverRun", "Autonomous phase: Engage");
        await agent.SendLogAsync(line);
        await agent.SendRunEndedAsync(new RunReport(Run, BotRunOutcome.Complete, DateTimeOffset.UtcNow, "status: Complete", 0,
            new BotProgress(5, 0, 4)));

        (string logged, BotLogLine[] lines) = await FleetListener.NextAsync(logs, _ => true);
        RunRecord ended = await FleetListener.NextAsync(runs, _ => true);

        Assert.Equal(AgentId, logged);
        Assert.Equal(line, Assert.Single(lines));
        Assert.Equal(AgentId, ended.AgentId);
        Assert.Equal(BotRunOutcome.Complete, ended.Report.Outcome);
    }

    // The card and the console showed the run, and kept Start off, until the next snapshot (known issue 77).
    [Fact]
    public async Task A_runs_end_takes_the_run_off_its_bot_at_once()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(data.Root));
        await using FeedUnderTest feed = await FeedUnderTest.StartAsync(slot);
        await Eventually.True(() => feed.Feed.Connected);
        await using FakeAgent agent = await FakeAgent.ConnectAsync(manager, AgentId);
        await agent.RegisterAsync(FakeAgent.Hello(AgentId, run: Run));
        await BotAsync(feed, bot => bot.Run is not null);

        await agent.SendRunEndedAsync(new RunReport(Run, BotRunOutcome.Complete, DateTimeOffset.UtcNow, "status: Complete", 0, null));
        BotInfo ended = await BotAsync(feed, bot => bot.Run is null);

        Assert.True(ended.Online);
    }

    [Fact]
    public async Task A_restarted_manager_is_read_again_and_followed()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        BotManagerFactory first = slot.Bring(new BotManagerFactory(data.Root));
        await using FeedUnderTest feed = await FeedUnderTest.StartAsync(slot);
        int resyncs = 0;
        feed.Feed.Resynced += () => Interlocked.Increment(ref resyncs);
        await Eventually.True(() => feed.Feed.Connected);

        await using (FakeAgent agent = await FakeAgent.ConnectAsync(first, AgentId))
        {
            await agent.RegisterAsync();
            await BotAsync(feed, bot => bot.Online);
        }

        // The manager goes, and the feed knows it.
        await BotAsync(feed, bot => !bot.Online);
        slot.Clear();
        first.Dispose();
        await Eventually.True(() => !feed.Feed.Connected);

        // Another comes up on the same data: the bot is read from it, then its agent dials in again.
        using BotManagerFactory second = slot.Bring(new BotManagerFactory(data.Root));
        await Eventually.True(() => feed.Feed.Connected);
        BotInfo? known = feed.Feed.Get(AgentId);
        await using FakeAgent back = await FakeAgent.ConnectAsync(second, AgentId);
        await back.RegisterAsync();
        BotInfo online = await BotAsync(feed, bot => bot.Online);

        Assert.NotNull(known);
        Assert.False(known.Online);
        Assert.Equal("Test PC", online.Name);
        Assert.True(Volatile.Read(ref resyncs) >= 2, $"read {resyncs} times");
    }

    [Fact]
    public async Task What_streams_while_the_bots_are_read_is_applied_after_the_list()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        using BotManagerFactory manager = slot.Bring(new BotManagerFactory(data.Root));

        // The feed's first read of the bots finds none, and is answered only after the bot registered.
        FirstListGate gate = new();
        await using FeedUnderTest feed = await FeedUnderTest.StartAsync(slot, gate);
        Channel<string> logs = Channel.CreateUnbounded<string>();
        feed.Feed.Log += (agentId, _) => logs.Writer.TryWrite(agentId);
        await gate.Read.Task.WaitAsync(FleetListener.Wait, TestContext.Current.CancellationToken);

        await using FakeAgent agent = await FakeAgent.ConnectAsync(manager, AgentId);
        await agent.RegisterAsync();
        // The hub delivers in order: once this line is through, the registration before it was taken.
        await agent.SendLogAsync(new BotLogLine(DateTimeOffset.UtcNow, "Information", "Test", "after the hello"));
        await FleetListener.NextAsync(logs, _ => true);
        Assert.False(feed.Feed.Connected);
        gate.Open.SetResult();

        await Eventually.True(() => feed.Feed.Connected);
        BotInfo? bot = feed.Feed.Get(AgentId);

        Assert.NotNull(bot);
        Assert.True(bot.Online);
    }

    private static BotSnapshot Snapshot(BotRunState state, int kills)
    {
        return new BotSnapshot(AgentId, DateTimeOffset.UtcNow, state, Run, null, new BotCharacter("Hunter", 12, "Orc"),
            new BotVitals(80, 45, 30, true), new BotPosition(1413, 51.9f, 29.9f), "Engage/Fight", "task 1 of 1",
            new BotProgress(kills, 0, 2), null);
    }

    private static Task<BotInfo> BotAsync(FeedUnderTest feed, Func<BotInfo, bool> holds)
    {
        return Eventually.Value(() => Task.FromResult(feed.Feed.Get(AgentId)), holds);
    }

    /// <summary>Reads the first list of the bots at once, and hands it over only when the test opens the gate.</summary>
    private sealed class FirstListGate : DelegatingHandler
    {
        private int lists;

        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath == BotManagerApi.Bots && Interlocked.Increment(ref lists) == 1)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                Read.SetResult();
                await Open.Task.WaitAsync(cancellationToken);
            }

            return response;
        }
    }
}
