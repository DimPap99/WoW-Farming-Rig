using Contracts;
using Contracts.Pathing;

using ControlCenter.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients.Pathing;

using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// The Control Center's live view feed against the Pathing Service in memory: connected only while
/// a page watches, each route with its continent, each kept once across the replay of a reconnect.
/// </summary>
public sealed class WatchFeedTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static readonly Point3 ElwynnVendor = new(-8898.32f, -117.35608f, 81.840546f);
    private static readonly Point3 ElwynnPath = new(-8779.58f, -106.12012f, 0);

    private static async Task Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_watched_feed_gets_each_route_once_and_lets_go_when_nobody_watches()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());
        await using WatchFeed feed = new(new PathingServiceOptions(factory.Server.BaseAddress), NullLogger<WatchFeed>.Instance,
            InMemoryHub.Over(factory.Server.CreateHandler));

        Assert.False(feed.Live);
        IDisposable lease = feed.Watch();
        await Until(() => feed.Live);

        RouteResponse found = await client.FindRouteAsync(
            new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);
        await Until(() => feed.Routes.Count == 1);

        WatchRoute route = feed.Routes[0];
        Assert.Equal("Azeroth", route.Continent);
        Assert.Equal(found.Points, route.Points);

        lease.Dispose();
        await Until(() => !feed.Live);

        // Watched again: the hub replays the route, which is kept once; a new one goes first.
        using IDisposable again = feed.Watch();
        await Until(() => feed.Live);
        await client.FindRouteAsync(new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnPath, ElwynnVendor),
            TestContext.Current.CancellationToken);
        await Until(() => feed.Routes.Count == 2);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(2, feed.Routes.Count);
        Assert.Equal(route.At, feed.Routes[1].At);
        Assert.True(feed.Routes[0].At >= route.At);
        Assert.Equal(ElwynnPath.X, feed.Routes[0].From.X, 1);
    }

    [Fact]
    public void The_feed_is_registered_with_the_Control_Center()
    {
        using ControlCenterFactory factory = new();

        Assert.NotNull(factory.Services.GetService<WatchFeed>());
    }
}
