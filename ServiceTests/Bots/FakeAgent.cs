using Contracts.Bots;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

using ServiceTests.Api;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// An agent as the manager sees one: a SignalR client over the in-memory server (long polling,
/// MessagePack) that registers, streams, and answers commands with <see cref="Answer"/>.
/// </summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    public const string Version = "test-agent 1.0";

    private FakeAgent(HubConnection connection, string agentId)
    {
        Connection = connection;
        AgentId = agentId;
        connection.On<BotCommand, CommandAck>(AgentHubMethods.Command, command =>
        {
            Commands.Enqueue(command);
            return Answer(command);
        });
        connection.On<BotAssignment?>(AgentHubMethods.Assign, assignment => Assignments.Enqueue(assignment));
        connection.Closed += _ =>
        {
            Closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    /// <summary>Done once the connection closed, as the manager closes a connection a newer one took over from.</summary>
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HubConnection Connection { get; }

    public string AgentId { get; }

    /// <summary>How the agent answers a command; accepts, starting run "run-1", unless a test says otherwise.</summary>
    public Func<BotCommand, CommandAck> Answer { get; set; } = command => new CommandAck(true, null, "run-1");

    public ConcurrentQueue<BotCommand> Commands { get; } = new();

    public ConcurrentQueue<BotAssignment?> Assignments { get; } = new();

    public static async Task<FakeAgent> ConnectAsync<TEntry>(WebApplicationFactory<TEntry> factory, string agentId,
        string? key = null, string? remote = null) where TEntry : class
    {
        HubConnection connection = InMemoryHub.Connect(factory.Server, BotManagerApi.AgentHub, key, remote);
        FakeAgent agent = new(connection, agentId);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return agent;
    }

    public static readonly AgentGame Forever = new("forever", "forever", 4242, "1.60.1.70009");

    public static AgentHello Hello(string agentId, string name = "Test PC", BotRunInfo? run = null, int protocol = BotManagerApi.AgentProtocol,
        string machine = "TESTBOX", bool withGame = true)
    {
        return new AgentHello(agentId, name, machine, protocol, Version, withGame ? Forever : null, run,
            ["Hunter_10.json", "Forever/Paladin_Forever_Auto.json"]);
    }

    public Task<AgentWelcome> RegisterAsync(AgentHello? hello = null)
    {
        return Connection.InvokeAsync<AgentWelcome>(AgentHubMethods.Register, hello ?? Hello(AgentId), TestContext.Current.CancellationToken);
    }

    public Task SendSnapshotAsync(BotSnapshot snapshot)
    {
        return Connection.InvokeAsync(AgentHubMethods.Snapshot, snapshot, TestContext.Current.CancellationToken);
    }

    public Task SendLogAsync(params BotLogLine[] lines)
    {
        return Connection.InvokeAsync(AgentHubMethods.Log, lines, TestContext.Current.CancellationToken);
    }

    public Task SendRunEndedAsync(RunReport report)
    {
        return Connection.InvokeAsync(AgentHubMethods.RunEnded, report, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
    }
}
