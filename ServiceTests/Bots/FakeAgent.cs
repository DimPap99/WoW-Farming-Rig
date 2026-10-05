using Contracts;
using Contracts.Bots;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

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
    }

    public HubConnection Connection { get; }

    public string AgentId { get; }

    /// <summary>How the agent answers a command; accepts, starting run "run-1", unless a test says otherwise.</summary>
    public Func<BotCommand, CommandAck> Answer { get; set; } = command => new CommandAck(true, null, "run-1");

    public ConcurrentQueue<BotCommand> Commands { get; } = new();

    public ConcurrentQueue<BotAssignment?> Assignments { get; } = new();

    public static async Task<FakeAgent> ConnectAsync<TEntry>(WebApplicationFactory<TEntry> factory, string agentId,
        string? key = null, string? remote = null) where TEntry : class
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, BotManagerApi.AgentHub), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (key is not null)
                {
                    options.Headers[ServiceKey.Header] = key;
                }
                if (remote is not null)
                {
                    options.Headers[Api.RemoteAddress.Header] = remote;
                }
            })
            .AddMessagePackProtocol()
            .Build();
        FakeAgent agent = new(connection, agentId);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return agent;
    }

    public static AgentHello Hello(string agentId, string name = "Test PC", BotRunInfo? run = null, int protocol = BotManagerApi.AgentProtocol)
    {
        return new AgentHello(agentId, name, "TESTBOX", protocol, Version,
            new AgentGame("forever", "forever", 4242, "1.60.1.70009"), run, ["Hunter_10.json", "Forever/Paladin_Forever_Auto.json"]);
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
