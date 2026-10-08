using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.SignalR.Client;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The watch hub as the Control Center uses it: a route watcher, over MessagePack.</summary>
public sealed class WatchHubTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static readonly Point3 ElwynnVendor = new(-8898.32f, -117.35608f, 81.840546f);
    private static readonly Point3 ElwynnPath = new(-8779.58f, -106.12012f, 0);

    [Fact]
    public async Task A_route_watcher_gets_each_route_with_its_continent_and_those_before_it_joined()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        await using HubConnection watcher = Connect(factory);
        TaskCompletionSource<WatchRoute> planned = Completion<WatchRoute>();
        watcher.On<WatchRoute>(WatchHubMethods.RoutePlanned, route => planned.TrySetResult(route));
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await watcher.InvokeAsync(WatchHubMethods.WatchRoutes, TestContext.Current.CancellationToken);

        RouteResponse found = await client.FindRouteAsync(
            new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);
        WatchRoute route = await planned.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal("Azeroth", route.Continent);
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Equal(found.Points, route.Points);
        Assert.Equal(ElwynnVendor.X, route.From.X, 1);

        // A watcher joining later is sent the routes it missed.
        await using HubConnection late = Connect(factory);
        TaskCompletionSource<WatchRoute> missed = Completion<WatchRoute>();
        late.On<WatchRoute>(WatchHubMethods.RoutePlanned, r => missed.TrySetResult(r));
        await late.StartAsync(TestContext.Current.CancellationToken);
        await late.InvokeAsync(WatchHubMethods.WatchRoutes, TestContext.Current.CancellationToken);

        Assert.Equal(route.At, (await missed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken)).At);
    }

    [Fact]
    public async Task A_clear_reaches_every_watcher_and_forgets_the_routes()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        await client.FindRouteAsync(new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath),
            TestContext.Current.CancellationToken);
        await using HubConnection watcher = Connect(factory);
        TaskCompletionSource cleared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.On(WatchHubMethods.Clear, () => cleared.TrySetResult());
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await watcher.InvokeAsync(WatchHubMethods.WatchRoutes, TestContext.Current.CancellationToken);

        ServiceResult clear = await client.ClearWatchAsync(TestContext.Current.CancellationToken);
        await cleared.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // A watcher joining after the clear is sent nothing from before it.
        await using HubConnection late = Connect(factory);
        int remembered = 0;
        late.On<WatchRoute>(WatchHubMethods.RoutePlanned, _ => Interlocked.Increment(ref remembered));
        await late.StartAsync(TestContext.Current.CancellationToken);
        await late.InvokeAsync(WatchHubMethods.WatchRoutes, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(clear.Ok, clear.Error);
        Assert.Equal(0, Volatile.Read(ref remembered));
    }

    private static HubConnection Connect(PathingServiceFactory factory)
    {
        return InMemoryHub.Connect(factory.Server, PathingApi.WatchHub);
    }

    private static TaskCompletionSource<T> Completion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
