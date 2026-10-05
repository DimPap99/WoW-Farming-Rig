using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using PathingService.Watch;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The watch hub as the 3D view uses it: MessagePack, points as [x, y, z] in world yards.</summary>
public sealed class WatchHubTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // One navmesh tile of Azeroth, as x, z.
    private static readonly int[] AzerothTile = [106, 146];

    private static readonly Point3 ElwynnVendor = new(-8898.32f, -117.35608f, 81.840546f);
    private static readonly Point3 ElwynnPath = new(-8779.58f, -106.12012f, 0);

    [Fact]
    public async Task Viewers_get_routes_drawings_and_on_replay_what_came_before_them()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        await using HubConnection watching = Connect(factory);
        TaskCompletionSource<float[][]> route = Completion<float[][]>();
        TaskCompletionSource<float[][]> drawing = Completion<float[][]>();
        watching.On<float[][], int, string>(WatchHubMethods.DrawPath, (points, _, name) =>
        {
            if (name == WatchBroadcaster.RouteName)
            {
                route.TrySetResult(points);
            }
            else if (name == "drawn")
            {
                drawing.TrySetResult(points);
            }
        });
        await watching.StartAsync(TestContext.Current.CancellationToken);

        RouteResponse found = await client.FindRouteAsync(
            new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);
        float[][] streamed = await route.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // [x, y, z] per point: what the 3D view and the map read.
        Assert.Equal(RouteStatus.Found, found.Status);
        Assert.Equal(found.Points.Length, streamed.Length);
        Assert.All(streamed, p => Assert.Equal(3, p.Length));
        Assert.Equal(found.Points[^1].X, streamed[^1][0]);
        Assert.Equal(found.Points[^1].Y, streamed[^1][1]);

        // A view opened later is sent the lanes' tiles and the route it missed.
        await using HubConnection late = Connect(factory);
        TaskCompletionSource<string> tile = Completion<string>();
        TaskCompletionSource<float[][]> missed = Completion<float[][]>();
        late.On<string, float[], int[], int[]>(WatchHubMethods.AddNavmeshTile, (name, _, _, _) => tile.TrySetResult(name));
        late.On<float[][], int, string>(WatchHubMethods.DrawPath, (points, _, name) =>
        {
            if (name == WatchBroadcaster.RouteName)
            {
                missed.TrySetResult(points);
            }
        });
        await late.StartAsync(TestContext.Current.CancellationToken);
        await late.InvokeAsync(WatchHubMethods.Replay, TestContext.Current.CancellationToken);

        Assert.StartsWith("navmesh_Azeroth_", await tile.Task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(streamed.Length, (await missed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken)).Length);

        // Map coordinates are drawn in world yards, on the navmesh's height.
        ServiceResult drawn = await client.DrawPathAsync(
            new WatchPath("drawn", 1429, CoordinateSpace.Map, [new(48.5f, 59.1f, 0), new(48.6f, 58.5f, 0)]),
            TestContext.Current.CancellationToken);
        float[][] drawnPoints = await drawing.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(drawn.Ok, drawn.Error);
        Assert.All(drawnPoints, p => Assert.NotEqual(0f, p[2]));

        string? terrain = await watching.InvokeAsync<string?>(WatchHubMethods.Terrain, "Azeroth", AzerothTile,
            TestContext.Current.CancellationToken);
        Assert.Contains("no game files", terrain);
    }

    [Fact]
    public async Task Drawings_are_checked()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult noName = await client.DrawPathAsync(new WatchPath("", 1429, CoordinateSpace.Map, [new(1, 2, 0)]),
            TestContext.Current.CancellationToken);
        ServiceResult noPoints = await client.DrawPathAsync(new WatchPath("x", 1429, CoordinateSpace.Map, []),
            TestContext.Current.CancellationToken);
        ServiceResult unknownMap = await client.ShowMarkerAsync(new WatchMarker("x", 999999, CoordinateSpace.World, new(1, 2, 3)),
            TestContext.Current.CancellationToken);
        ServiceResult cleared = await client.ClearWatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(400, noName.Status);
        Assert.Equal(400, noPoints.Status);
        Assert.Equal(400, unknownMap.Status);
        Assert.True(cleared.Ok, cleared.Error);
    }

    private static HubConnection Connect(PathingServiceFactory factory)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, PathingApi.WatchHub), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddMessagePackProtocol()
            .Build();
    }

    private static TaskCompletionSource<T> Completion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
