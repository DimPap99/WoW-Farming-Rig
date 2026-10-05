using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The service end to end, in memory, through the same client the Control Center and agent use.</summary>
public sealed class PathingApiTests
{
    private static readonly Point3 ElwynnVendor = new(-8898.32f, -117.35608f, 81.840546f);
    private static readonly Point3 ElwynnPath = new(-8779.58f, -106.12012f, 0);

    [Fact]
    public async Task A_route_is_found_through_the_client()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        RouteResponse route = await client.FindRouteAsync(
            new RouteRequest(TestData.Client, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath),
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.True(route.Smoothed);
        Assert.True(route.Points.Length > 2);
        Assert.Null(route.Detail);
    }

    [Fact]
    public async Task An_unbaked_tile_is_an_answer_not_an_error()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        HttpClient http = factory.CreateClient();

        using HttpResponseMessage response = await http.PostAsJsonAsync(PathingApi.FindRoute,
            new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, new Point3(-14500, -6000, 0)),
            ContractJson.Options, TestContext.Current.CancellationToken);
        RouteResponse? route = await response.Content.ReadFromJsonAsync<RouteResponse>(ContractJson.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RouteStatus.NotBaked, route!.Status);
        Assert.Contains("no baked navmesh", route.Detail);
    }

    [Fact]
    public async Task A_bad_route_request_says_why()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        RouteResponse unknownMap = await client.FindRouteAsync(
            new RouteRequest(null, 999999, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);
        RouteResponse otherClient = await client.FindRouteAsync(
            new RouteRequest("forever", 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.BadRequest, unknownMap.Status);
        Assert.Contains("999999", unknownMap.Detail);
        Assert.Equal(RouteStatus.BadRequest, otherClient.Status);
        Assert.Contains("forever", otherClient.Detail);
    }

    [Fact]
    public async Task Capabilities_and_health_describe_the_instance()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        ServiceResult<PathingHealth> health = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(capabilities.Ok, capabilities.Error);
        Assert.Equal(PathingApi.Version, capabilities.Value.ApiVersion);
        Assert.Equal(TestData.Client, capabilities.Value.Client);
        Assert.Equal("precata", capabilities.Value.Era);
        Assert.Empty(capabilities.Value.BakedContinents);
        Assert.True(health.Ok, health.Error);
        Assert.All(health.Value.Coverage, c => Assert.Equal(0, c.Tiles));
    }

    [Fact]
    public async Task Recorded_paths_round_trip_through_the_client()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        Point3[] points = [new(48.5f, 59.1f, 0), new(48.6f, 58.5f, 0)];

        ServiceResult saved = await client.SavePathAsync("_herb/Elwynn Herbs.json", points, TestContext.Current.CancellationToken);
        ServiceResult<RecordedPath> read = await client.GetPathAsync("_herb/Elwynn Herbs.json", TestContext.Current.CancellationToken);
        ServiceResult<RecordedPathInfo[]> listed = await client.ListPathsAsync("herbs", TestContext.Current.CancellationToken);
        ServiceResult refused = await client.SavePathAsync("notes.txt", points, TestContext.Current.CancellationToken);
        // URL normalisation turns this into another route before it leaves the client; it must not save anything.
        ServiceResult escaped = await client.SavePathAsync("../escape.json", points, TestContext.Current.CancellationToken);
        ServiceResult deleted = await client.DeletePathAsync("_herb/Elwynn Herbs.json", TestContext.Current.CancellationToken);
        ServiceResult<RecordedPath> gone = await client.GetPathAsync("_herb/Elwynn Herbs.json", TestContext.Current.CancellationToken);

        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(points, read.Value!.Points);
        Assert.Equal("_herb/Elwynn Herbs.json", listed.Value!.Single().Name);
        Assert.Equal((int)HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("must end in .json", refused.Error);
        Assert.False(escaped.Ok);
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(data.Root, "escape.json")));
        Assert.True(deleted.Ok, deleted.Error);
        Assert.Equal((int)HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task Cost_zones_round_trip_through_the_client()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        RoadZones roads = new([new RoadLine("road", 2, [new(-120.3f, -1814.1f), new(-61.2f, -1836.0f)])]);

        ServiceResult saved = await client.SaveRoadsAsync("Azeroth", 1429, roads, TestContext.Current.CancellationToken);
        ServiceResult<CostZoneSet> read = await client.GetCostZonesAsync("Azeroth", TestContext.Current.CancellationToken);
        ServiceResult refused = await client.SaveRoadsAsync("Azeroth", 1429,
            new RoadZones([new RoadLine("wide", 99, [new(0, 0)])]), TestContext.Current.CancellationToken);
        ServiceResult reloaded = await client.ReloadCostZonesAsync(TestContext.Current.CancellationToken);

        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(roads.Roads[0].Points, read.Value!.Roads[1429].Roads[0].Points);
        Assert.Equal((int)HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("width 99", refused.Error);
        Assert.True(reloaded.Ok, reloaded.Error);
    }

    [Fact]
    public async Task The_api_describes_itself_for_swagger()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);

        string document = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json",
            TestContext.Current.CancellationToken);

        Assert.Contains(PathingApi.FindRoute, document);
        Assert.Contains(PathingApi.Paths + "/{name}", document);
        Assert.Contains(PathingApi.CostZones + "/{continent}/" + PathingApi.Roads + "/{uiMapId}", document);
    }

    [Fact]
    public async Task An_unreachable_service_is_unavailable_not_an_exception()
    {
        PathingClient client = new(new HttpClient { BaseAddress = ClosedPort.NewUrl(), Timeout = TimeSpan.FromSeconds(5) });

        RouteResponse route = await client.FindRouteAsync(
            new RouteRequest(null, 1429, CoordinateSpace.World, ElwynnVendor, ElwynnPath), TestContext.Current.CancellationToken);
        ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.Unavailable, route.Status);
        Assert.Contains("could not be reached", route.Detail);
        Assert.Equal(0, capabilities.Status);
        Assert.False(capabilities.Ok);
    }

    [Fact]
    public async Task The_registered_client_works_with_its_resilience_settings()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        ServiceCollection services = new();
        services.AddPathingClient(new Uri("http://localhost/"))
            .ConfigurePrimaryHttpMessageHandler(() => factory.Server.CreateHandler());
        await using ServiceProvider provider = services.BuildServiceProvider();

        ServiceResult<PathingCapabilities> capabilities = await provider.GetRequiredService<IPathingClient>()
            .GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.True(capabilities.Ok, capabilities.Error);
    }
}
