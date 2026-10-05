using ControlCenter.Hosting;

using Contracts.Pathing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using ServiceClients.Pathing;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The Control Center with its Pathing Service down: the pages and the map still load, the forwarding says 502.</summary>
public sealed class ControlCenterTests
{
    private sealed class ControlCenterFactory : WebApplicationFactory<ControlCenter.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(ControlCenterLogging.FileKey, string.Empty);
            // The discard port: nothing listens there, so the service is down.
            builder.UseSetting(PathingServiceOptions.UrlKey, "http://127.0.0.1:9");
        }
    }

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
    public void The_api_the_hub_and_every_world_data_prefix_are_forwarded()
    {
        Assert.Contains(PathingApi.Root, ServiceProxy.PathingPrefixes);
        Assert.Contains(PathingApi.WatchHub, ServiceProxy.PathingPrefixes);
        Assert.Contains(PathingApi.Swagger, ServiceProxy.PathingPrefixes);
        Assert.All(WorldData.All, prefix => Assert.Contains(prefix, ServiceProxy.PathingPrefixes));
    }
}
