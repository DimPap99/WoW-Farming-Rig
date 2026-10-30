using ControlCenter.Components.Pages;
using ControlCenter.Pathing;

using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The Control Center with its Pathing Service down: the pages and the world view still load, the forwarding says 502.</summary>
public sealed class ControlCenterTests
{
    [Fact]
    public async Task The_shell_and_the_world_view_load_while_the_service_is_down()
    {
        using ControlCenterFactory factory = new();
        HttpClient http = factory.CreateClient();

        string shell = await http.GetStringAsync("/", TestContext.Current.CancellationToken);
        using HttpResponseMessage view = await http.GetAsync("/js/world-view.js", TestContext.Current.CancellationToken);
        using HttpResponseMessage forwarded = await http.GetAsync(PathingApi.Capabilities, TestContext.Current.CancellationToken);

        Assert.Contains("<title>Control Center</title>", shell);
        Assert.Contains("js/world-view", shell);
        Assert.Contains("js/world-edit", shell);
        Assert.DoesNotContain("leaflet", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, forwarded.StatusCode);
    }

    [Fact]
    public async Task The_route_map_waits_for_the_service_and_says_how_to_start_it()
    {
        using ControlCenterFactory factory = new();
        await factory.Services.GetRequiredService<PathingStatus>().RefreshAsync(TestContext.Current.CancellationToken);

        string page = await factory.RenderAsync<RouteMap>();

        Assert.Contains("pa-world", page);
        Assert.Contains("Waiting for the Pathing Service", page);
        Assert.Contains("The Pathing Service is offline", page);
    }

    [Fact]
    public async Task The_world_view_pages_wait_for_the_service()
    {
        using ControlCenterFactory factory = new();
        await factory.Services.GetRequiredService<PathingStatus>().RefreshAsync(TestContext.Current.CancellationToken);

        string zones = await factory.RenderAsync<CostZones>();
        string live = await factory.RenderAsync<LiveView>();
        string npcs = await factory.RenderAsync<NpcRoutes>();
        string navmesh = await factory.RenderAsync<Navmesh>();
        string map = await factory.RenderAsync<WorldMap>();

        Assert.Contains("Waiting for the Pathing Service", zones);
        Assert.All(new[] { zones, live, npcs, navmesh, map }, page =>
        {
            Assert.Contains("pa-world", page);
            Assert.Contains("The Pathing Service is offline", page);
        });
    }

}
