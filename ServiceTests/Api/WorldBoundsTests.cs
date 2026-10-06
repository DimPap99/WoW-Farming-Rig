using Contracts;
using Contracts.Pathing;

using PPather.Navmesh;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// Requests that held a continent's lane for good (known issue 32): a point beyond the edge of the
/// world, which Detour's tile loops never come back from, and tuning that sends a query that far
/// or floods the mesh. Each is refused before it reaches a query. Every call has a deadline, so a
/// regression fails the test instead of wedging the run.
/// </summary>
public sealed class WorldBoundsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly Point3 ElwynnVendor = new(-8898.32f, -117.35608f, 81.840546f);
    private static readonly Point3 ElwynnPath = new(-8779.58f, -106.12012f, 0);

    [Fact]
    public async Task An_area_beyond_the_edge_of_the_world_is_refused()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        using CancellationTokenSource deadline = WithDeadline();

        ServiceResult<AreaAtPoint> beyond = await client.GetAreaAsync(0, 0, 3e11f, deadline.Token);
        ServiceResult<AreaAtPoint> atTheEdge = await client.GetAreaAsync(0, NavmeshCoords.WorldEdge, -NavmeshCoords.WorldEdge, deadline.Token);

        Assert.Equal(400, beyond.Status);
        Assert.Contains("edge of the world", beyond.Error);
        Assert.True(atTheEdge.Ok, atTheEdge.Error);
    }

    [Theory]
    [InlineData(0f, 1e12f, "edge margin")]
    [InlineData(1e5f, null, "jitter")]
    [InlineData(NavmeshQueryTuning.MaxJitterYards + 1, null, "jitter")]
    [InlineData(0f, NavmeshQueryTuning.MaxEdgeMarginYards + 1, "edge margin")]
    [InlineData(-1f, null, "jitter")]
    public async Task Tuning_past_its_caps_is_refused(float jitterYards, float? edgeMarginYards, string named)
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        using CancellationTokenSource deadline = WithDeadline();

        RouteResponse route = await client.FindRouteAsync(new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath,
            new RouteTuning(jitterYards, 7, edgeMarginYards)), deadline.Token);

        Assert.Equal(RouteStatus.BadRequest, route.Status);
        Assert.Contains(named, route.Detail);
    }

    [Fact]
    public async Task Tuning_within_its_caps_is_planned()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());
        using CancellationTokenSource deadline = WithDeadline();

        RouteResponse route = await client.FindRouteAsync(new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath,
            new RouteTuning(NavmeshQueryTuning.MaxJitterYards, 7, NavmeshQueryTuning.MaxEdgeMarginYards)), deadline.Token);

        Assert.Equal(RouteStatus.Found, route.Status);
    }

    [Fact]
    public async Task A_drawing_beyond_the_edge_of_the_world_is_refused()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        using CancellationTokenSource deadline = WithDeadline();

        ServiceResult world = await client.DrawPathAsync(
            new WatchPath("far", 1429, CoordinateSpace.World, [new(-8898, -117, 0), new(0, 3e11f, 0)]), deadline.Token);
        // Far outside the zone's 0 to 100, so far outside the world once converted.
        ServiceResult map = await client.ShowMarkerAsync(new WatchMarker("far", 1429, CoordinateSpace.Map, new(1e9f, 50, 0)),
            deadline.Token);
        ServiceResult inside = await client.ShowMarkerAsync(new WatchMarker("near", 1429, CoordinateSpace.Map, new(48.5f, 59.1f, 0)),
            deadline.Token);

        Assert.Equal(400, world.Status);
        Assert.Contains("point 1", world.Error);
        Assert.Contains("edge of the world", world.Error);
        Assert.Equal(400, map.Status);
        Assert.Contains("edge of the world", map.Error);
        Assert.True(inside.Ok, inside.Error);
    }

    private static CancellationTokenSource WithDeadline()
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        return deadline;
    }
}
