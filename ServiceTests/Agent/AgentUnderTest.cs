using BotAgent;
using BotAgent.Link;
using BotAgent.Runtime;
using BotAgent.Telemetry;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ServiceClients.Bots;

using ServiceTests.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>
/// Where an agent finds the manager: the in-memory one a test puts here, or nothing, in which case
/// every attempt fails as a refused connection would. A test takes the manager away and brings a new
/// one up on the same data, as a restart would.
/// </summary>
internal sealed class ManagerSlot
{
    public static readonly Uri Address = new("http://localhost:5030");

    public BotManagerFactory? Current { get; private set; }

    /// <summary>
    /// Puts <paramref name="manager"/> up for the agent, its server started first: a factory builds
    /// its server lazily and not thread-safely, so the agent dialling from its own thread and the test
    /// creating a client would each start one.
    /// </summary>
    public BotManagerFactory Bring(BotManagerFactory manager)
    {
        _ = manager.Server;
        Current = manager;
        return manager;
    }

    /// <summary>Takes the manager away: every attempt fails until one is brought again.</summary>
    public void Clear()
    {
        Current = null;
    }

    public HttpMessageHandler Handler()
    {
        return Current is { } manager ? manager.Server.CreateHandler() : new Unreachable();
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused (test)");
        }
    }
}

/// <summary>The agent as HeadlessServer composes it, with fake runtimes, its hosted services started.</summary>
internal sealed class AgentUnderTest : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly IHostedService[] hosted;

    private AgentUnderTest(ServiceProvider provider, IHostedService[] hosted)
    {
        this.provider = provider;
        this.hosted = hosted;
    }

    public BotRuntimeHost Host => provider.GetRequiredService<BotRuntimeHost>();

    public AgentLink Link => provider.GetRequiredService<AgentLink>();

    public TelemetryPublisher Telemetry => provider.GetRequiredService<TelemetryPublisher>();

    public AgentIdentity Identity => provider.GetRequiredService<AgentIdentity>();

    public static async Task<AgentUnderTest> StartAsync(string folder, ManagerSlot? manager, FakeRuntimeFactory factory,
        FakeProcess? process = null, int graceMs = 2000, IRunPreparation? preparation = null, IGameProbe? probe = null,
        TimeProvider? clock = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddSingleton(new AgentLinkOptions([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)], TimeSpan.FromSeconds(1))
        {
            HttpMessageHandlerFactory = manager is null ? null : manager.Handler,
            Transports = HttpTransportType.LongPolling,
        });
        AgentOptions options = new(Path.Combine(folder, "agent_identity.json"), "Test agent",
            manager is null ? null : new BotManagerOptions(ManagerSlot.Address), TimeSpan.FromMilliseconds(graceMs), "test 1.0");
        services.AddBotAgent(options, new AgentLogSink());
        services.AddSingleton<IBotRuntimeFactory>(factory);
        services.AddSingleton(probe ?? new FakeProbe());
        services.AddSingleton<IClassProfileCatalog, FakeProfiles>();
        services.AddSingleton<IAgentProcess>(process ?? new FakeProcess());
        if (preparation is not null)
        {
            services.AddSingleton(preparation);
        }

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        IHostedService[] hosted = [.. provider.GetServices<IHostedService>()];
        foreach (IHostedService service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        return new AgentUnderTest(provider, hosted);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHostedService service in hosted.Reverse())
        {
            await service.StopAsync(CancellationToken.None);
        }

        await provider.DisposeAsync();
    }
}

internal static class Eventually
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public static async Task<T> Value<T>(Func<Task<T?>> read, Func<T, bool> holds) where T : class
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            if (await read() is { } value && holds(value))
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold in time");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public static Task True(Func<bool> holds)
    {
        return Value<object>(() => Task.FromResult<object?>(holds() ? new object() : null), _ => true);
    }
}
