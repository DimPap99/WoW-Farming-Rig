using BotAgent;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients;
using ServiceClients.Bots;

using ServiceTests.Api;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// One agent per id (known issue 74): a second connection registering an id took the bot over
/// silently, the first played on unseen and could not be stopped, and its reports were taken and lost.
/// </summary>
public sealed class OneAgentPerIdTests
{
    private const string AgentId = "3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f";
    private const string Other = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_id_online_from_another_machine_is_refused_naming_it()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using FakeAgent first = await FakeAgent.ConnectAsync(factory, AgentId);
        await using FakeAgent copy = await FakeAgent.ConnectAsync(factory, AgentId);

        AgentWelcome firstWelcome = await first.RegisterAsync(FakeAgent.Hello(AgentId, machine: "PC-ONE"));
        AgentWelcome copied = await copy.RegisterAsync(FakeAgent.Hello(AgentId, machine: "PC-TWO"));
        ServiceResult<BotInfo> bot = await client.GetBotAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.True(firstWelcome.Accepted, firstWelcome.Reason);
        Assert.False(copied.Accepted);
        Assert.Contains("online from PC-ONE", copied.Reason);
        Assert.Contains(BotManagerApi.AgentIdentityFile, copied.Reason);
        Assert.Equal("PC-ONE", bot.Value!.Machine);
        Assert.True(bot.Value.Online);
    }

    [Fact]
    public async Task A_newer_connection_from_the_same_machine_takes_over_and_the_older_is_closed()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        await using FakeAgent older = await FakeAgent.ConnectAsync(factory, AgentId);
        await using FakeAgent newer = await FakeAgent.ConnectAsync(factory, AgentId);
        await older.RegisterAsync();

        AgentWelcome welcome = await newer.RegisterAsync();
        await older.Closed.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        Assert.True(welcome.Accepted, welcome.Reason);
    }

    [Fact]
    public async Task A_report_on_a_connection_that_is_not_the_bots_is_refused_so_the_agent_keeps_it()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        await using FakeAgent unregistered = await FakeAgent.ConnectAsync(factory, AgentId);
        BotRunInfo run = new("run1", BotRuntimeKind.Forever, "route trip.json", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<HubException>(() =>
            unregistered.SendRunEndedAsync(new RunReport(run, BotRunOutcome.Stopped, DateTimeOffset.UtcNow, "stopped", null, null)));
    }

    [Fact]
    public async Task One_connection_is_one_agent()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        await using FakeAgent agent = await FakeAgent.ConnectAsync(factory, AgentId);
        await agent.RegisterAsync();

        AgentWelcome second = await agent.RegisterAsync(FakeAgent.Hello(Other));

        Assert.False(second.Accepted);
        Assert.Contains($"agent {AgentId} already", second.Reason);
    }

    [Fact]
    public async Task A_hello_without_a_client_keeps_the_one_last_seen()
    {
        using TempDataRoot data = new();
        using BotManagerFactory factory = new(data.Root);
        BotManagerClient client = new(factory.CreateClient());
        await using (FakeAgent before = await FakeAgent.ConnectAsync(factory, AgentId))
        {
            await before.RegisterAsync();
        }

        await using FakeAgent after = await FakeAgent.ConnectAsync(factory, AgentId);
        await after.RegisterAsync(FakeAgent.Hello(AgentId, withGame: false));
        ServiceResult<BotInfo> bot = await client.GetBotAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.Equal(FakeAgent.Forever, bot.Value!.Game);
    }

    [Fact]
    public void An_id_is_held_by_one_process_at_a_time()
    {
        AgentIdentity identity = new(Guid.NewGuid().ToString("N"), "Test", "BOX");

        using IDisposable? first = identity.Claim();
        using IDisposable? second = identity.Claim();

        Assert.NotNull(first);
        Assert.Null(second);
    }
}
