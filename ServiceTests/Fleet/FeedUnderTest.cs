using ControlCenter.Bots;

using Contracts.Bots;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients.Bots;

using ServiceTests.Agent;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>
/// The Control Center's <see cref="FleetFeed"/>, started, against whatever manager a
/// <see cref="ManagerSlot"/> holds: its hub over the in-memory server (long polling, MessagePack)
/// and its REST client through the same slot, so a test can take the manager away and bring a new one.
/// </summary>
internal sealed class FeedUnderTest : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private FeedUnderTest(ServiceProvider provider, FleetFeed feed)
    {
        this.provider = provider;
        Feed = feed;
    }

    public FleetFeed Feed { get; }

    /// <summary>The feed, its REST calls going to the slot's manager, through <paramref name="around"/> when given.</summary>
    public static async Task<FeedUnderTest> StartAsync(ManagerSlot slot, DelegatingHandler? around = null)
    {
        HttpMessageHandler handler = new SlotHandler(slot);
        if (around is not null)
        {
            around.InnerHandler = handler;
            handler = around;
        }

        ServiceCollection services = new();
        services.AddSingleton<IBotManagerClient>(new BotManagerClient(new HttpClient(handler) { BaseAddress = ManagerSlot.Address }));
        ServiceProvider provider = services.BuildServiceProvider();

        FleetFeed feed = new(provider, new SlotConnections(slot), NullLogger<FleetFeed>.Instance);
        await feed.StartAsync(TestContext.Current.CancellationToken);
        return new FeedUnderTest(provider, feed);
    }

    public async ValueTask DisposeAsync()
    {
        await Feed.StopAsync(CancellationToken.None);
        Feed.Dispose();
        await provider.DisposeAsync();
    }

    /// <summary>The fleet hub of the slot's manager, as each connect finds it.</summary>
    private sealed class SlotConnections(ManagerSlot slot) : IFleetConnectionFactory
    {
        public HubConnection Create()
        {
            return new HubConnectionBuilder()
                .WithUrl(new Uri(ManagerSlot.Address, BotManagerApi.FleetHub), options =>
                {
                    options.HttpMessageHandlerFactory = _ => slot.Handler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .AddMessagePackProtocol()
                .Build();
        }
    }

    /// <summary>Each request to the slot's manager of the moment, or refused when there is none.</summary>
    private sealed class SlotHandler(ManagerSlot slot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpMessageInvoker(slot.Handler()).SendAsync(request, cancellationToken);
        }
    }
}
