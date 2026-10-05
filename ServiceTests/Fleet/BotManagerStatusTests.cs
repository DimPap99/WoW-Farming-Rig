using ControlCenter.Bots;

using Contracts.Bots;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients.Bots;

using ServiceTests.Api;
using ServiceTests.Bots;

using System;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>The Control Center's poller of the Bot Manager's health, as the status bar and the fleet pages read it.</summary>
public sealed class BotManagerStatusTests
{
    [Fact]
    public async Task A_manager_that_answers_is_online_with_its_counts()
    {
        using TempDataRoot data = new();
        using BotManagerFactory manager = new(data.Root);
        int reconnected = 0;
        BotManagerStatus status = Status(new BotManagerClient(manager.CreateClient()), manager.Server.BaseAddress);
        status.Reconnected += () => reconnected++;

        await status.RefreshAsync(TestContext.Current.CancellationToken);
        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Online);
        Assert.Null(status.Error);
        Assert.Equal(new ManagerHealth(BotManagerApi.Version, BotManagerApi.AgentProtocol, 0, 0), status.Health);
        Assert.Equal(1, reconnected);
        Assert.Equal("Bot Manager", status.Service.Name);
    }

    [Fact]
    public async Task A_manager_that_is_down_is_offline_with_why()
    {
        Uri down = ClosedPort.NewUrl();
        BotManagerStatus status = Status(new BotManagerClient(new HttpClient { BaseAddress = down, Timeout = TimeSpan.FromSeconds(5) }), down);

        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Online);
        Assert.Contains("could not be reached", status.Error);
        Assert.Null(status.Health);
        Assert.Equal(down, status.Service.Url);
    }

    private static BotManagerStatus Status(IBotManagerClient client, Uri url)
    {
        ServiceCollection services = new();
        services.AddSingleton(client);
        return new BotManagerStatus(services.BuildServiceProvider(), new BotManagerOptions(url), NullLogger<BotManagerStatus>.Instance);
    }
}
