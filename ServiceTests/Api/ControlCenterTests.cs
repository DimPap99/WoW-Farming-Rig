using ControlCenter.Components.Pages;
using ControlCenter.Hosting;
using ControlCenter.Pathing;

using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The Control Center with its Pathing Service down: the pages and the map still load, the forwarding says 502.</summary>
public sealed class ControlCenterTests
{
    [Fact]
    public async Task The_shell_and_the_map_load_while_the_service_is_down()
    {
        using ControlCenterFactory factory = new();
        HttpClient http = factory.CreateClient();

        string shell = await http.GetStringAsync("/", TestContext.Current.CancellationToken);
        using HttpResponseMessage map = await http.GetAsync("/_content/MapAssets/script/leaflet-watch.js", TestContext.Current.CancellationToken);
        using HttpResponseMessage forwarded = await http.GetAsync(PathingApi.Capabilities, TestContext.Current.CancellationToken);

        Assert.Contains("<title>Control Center</title>", shell);
        Assert.Contains("_content/MapAssets/script/leaflet-map", shell);
        Assert.Equal(HttpStatusCode.OK, map.StatusCode);
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
    public void The_api_the_hub_and_every_world_data_prefix_are_forwarded()
    {
        Assert.Contains(PathingApi.Root, ServiceProxy.PathingPrefixes);
        Assert.Contains(PathingApi.WatchHub, ServiceProxy.PathingPrefixes);
        Assert.Contains(PathingApi.Swagger, ServiceProxy.PathingPrefixes);
        Assert.All(WorldData.All, prefix => Assert.Contains(prefix, ServiceProxy.PathingPrefixes));
    }
}
