using Contracts.Bots;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

using ServiceTests.Api;

using System;
using System.Threading.Channels;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>The fleet hub as the Control Center listens to it, every message kept for a test to wait on.</summary>
internal sealed class FleetListener : IAsyncDisposable
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly HubConnection connection;

    private FleetListener(HubConnection connection)
    {
        this.connection = connection;
        connection.On<BotInfo>(FleetHubMethods.BotChanged, bot => Changed.Writer.TryWrite(bot));
        connection.On<string>(FleetHubMethods.BotRemoved, agentId => Removed.Writer.TryWrite(agentId));
        connection.On<BotSnapshot>(FleetHubMethods.Snapshot, snapshot => Snapshots.Writer.TryWrite(snapshot));
        connection.On<string, BotLogLine[]>(FleetHubMethods.Log, (agentId, lines) => Logs.Writer.TryWrite((agentId, lines)));
        connection.On<RunRecord>(FleetHubMethods.RunEnded, run => Runs.Writer.TryWrite(run));
    }

    public Channel<BotInfo> Changed { get; } = Channel.CreateUnbounded<BotInfo>();

    public Channel<string> Removed { get; } = Channel.CreateUnbounded<string>();

    public Channel<BotSnapshot> Snapshots { get; } = Channel.CreateUnbounded<BotSnapshot>();

    public Channel<(string AgentId, BotLogLine[] Lines)> Logs { get; } = Channel.CreateUnbounded<(string, BotLogLine[])>();

    public Channel<RunRecord> Runs { get; } = Channel.CreateUnbounded<RunRecord>();

    public static async Task<FleetListener> ConnectAsync<TEntry>(WebApplicationFactory<TEntry> factory) where TEntry : class
    {
        HubConnection connection = InMemoryHub.Connect(factory.Server, BotManagerApi.FleetHub);
        FleetListener listener = new(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return listener;
    }

    /// <summary>The first message on <paramref name="channel"/> that <paramref name="match"/> accepts.</summary>
    public static async Task<T> NextAsync<T>(Channel<T> channel, Func<T, bool> match)
    {
        using System.Threading.CancellationTokenSource timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(Wait);
        while (true)
        {
            T item = await channel.Reader.ReadAsync(timeout.Token);
            if (match(item))
            {
                return item;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
    }
}
