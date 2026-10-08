using BotManager.Agents;
using BotManager.Bots;
using BotManager.Fleet;
using ServiceHosting;
using BotManager.Registry;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceTests.Api;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// One fleet listener that stops reading holds no agent (known issue 73): every agent hub call awaited
/// the send to every listener, so one stalled Control Center froze every agent's connection, the
/// answers to Start and Stop included, until SignalR dropped it.
/// </summary>
public sealed class StalledListenerTests
{
    private const string AgentId = "3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f";
    private const string Connection = "agent-connection";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task An_agents_calls_return_while_a_listener_never_reads()
    {
        using TempDataRoot data = new();
        StalledHub<FleetHub> listeners = new();
        HubSendQueue sends = new(NullLogger<HubSendQueue>.Instance);
        await sends.StartAsync(TestContext.Current.CancellationToken);
        BotFleet fleet = new(new JsonBotRegistry(data.Root, NullLogger<JsonBotRegistry>.Instance), new StalledHub<AgentHub>(), listeners,
            sends, new BotFleetOptions(TimeSpan.FromSeconds(1)), TimeProvider.System, NullLogger<BotFleet>.Instance);
        BotRunInfo run = new("run1", BotRuntimeKind.Forever, "route trip.json", DateTimeOffset.UtcNow);

        Assert.True(await Returns(() => fleet.RegisterAsync(Connection, FakeAgent.Hello(AgentId), () => { })), "Register waited on the listener");
        Assert.True(await Returns(() => fleet.SnapshotAsync(Connection, Snapshot(run))), "Snapshot waited on the listener");
        Assert.True(await Returns(() => fleet.LogAsync(Connection, [new BotLogLine(1, 1, DateTimeOffset.UtcNow, "Information", "Test", "a line")])),
            "Log waited on the listener");
        Assert.True(await Returns(() => fleet.RunEndedAsync(Connection,
            new RunReport(run, BotRunOutcome.Stopped, DateTimeOffset.UtcNow, "stopped", null, null))), "RunEnded waited on the listener");
        Assert.True(listeners.Sends >= 1, "nothing was sent to the listeners");

        await sends.StopAsync(CancellationToken.None);
    }

    // On a thread of its own, with a deadline: the old calls never came back.
    private static async Task<bool> Returns(Func<Task> call)
    {
        Task running = Task.Run(call, TestContext.Current.CancellationToken);
        return await Task.WhenAny(running, Task.Delay(Deadline, TestContext.Current.CancellationToken)) == running;
    }

    private static BotSnapshot Snapshot(BotRunInfo run)
    {
        return new BotSnapshot(AgentId, DateTimeOffset.UtcNow, BotRunState.Running, run, null, null, null, null, null, null, null, null);
    }

    // A hub whose every send waits for a reader that never comes.
    private sealed class StalledHub<THub> : IHubContext<THub> where THub : Hub
    {
        private readonly StalledClients clients = new();

        public int Sends => clients.Sends;

        public IHubClients Clients => clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class StalledClients : IHubClients, IClientProxy
    {
        private int sends;

        public int Sends => Volatile.Read(ref sends);

        public IClientProxy All => this;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref sends);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;

        public IClientProxy Client(string connectionId) => this;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;

        public IClientProxy Group(string groupName) => this;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;

        public IClientProxy User(string userId) => this;

        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }
}
