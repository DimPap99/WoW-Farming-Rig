using Contracts;
using Contracts.Pathing;

using Newtonsoft.Json;

using PPather.Graph;
using PPather.Navmesh;

using ServiceClients;
using ServiceClients.Pathing;

using SharedLib;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using IOPath = System.IO.Path;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// A cost-zone save reloads its continent's zones in the planner, all or nothing (known issue 41):
/// when another file of the continent does not load, the save is kept but not in use, and the
/// answer says so, naming that file.
/// </summary>
public sealed class CostZoneApiTests
{
    private static readonly RoadZones Roads = new([new RoadLine("road", 2, [new(-120.3f, -1814.1f), new(-61.2f, -1836.0f)])]);

    [Fact]
    public async Task A_save_that_does_not_apply_answers_409_naming_the_file_in_the_way()
    {
        using TempDataRoot data = new(withMapData: true);
        string bad = WriteRoads(data, "1430.json", new RoadData { Roads = [new RoadSegment("wide", 100, [new(0, 0)])] });
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        await OpenAzerothsLane(data, client);

        ServiceResult saved = await client.SaveRoadsAsync("Azeroth", 1429, Roads, TestContext.Current.CancellationToken);
        ServiceResult reloaded = await client.ReloadCostZonesAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)HttpStatusCode.Conflict, saved.Status);
        Assert.Contains("saved", saved.Error);
        Assert.Contains("1430.json", saved.Error);
        Assert.Contains("width 100", saved.Error);
        Assert.True(File.Exists(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
        Assert.Equal((int)HttpStatusCode.Conflict, reloaded.Status);
        Assert.Contains("1430.json", reloaded.Error);

        // Once the file in the way is fixed, the next save applies.
        WriteRoads(data, "1430.json", new RoadData { Roads = [new RoadSegment("narrow", 2, [new(0, 0)])] });
        ServiceResult again = await client.SaveRoadsAsync("Azeroth", 1429, Roads, TestContext.Current.CancellationToken);

        Assert.True(again.Ok, again.Error);
        Assert.True(File.Exists(bad));
    }

    [Fact]
    public async Task A_save_on_a_continent_with_no_open_lane_applies_when_it_opens()
    {
        using TempDataRoot data = new(withMapData: true);
        WriteRoads(data, "1430.json", new RoadData { Roads = [new RoadSegment("wide", 100, [new(0, 0)])] });
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        // No lane to reload: it reads the files, the bad one left out, when a route opens it.
        ServiceResult saved = await client.SaveRoadsAsync("Azeroth", 1429, Roads, TestContext.Current.CancellationToken);

        Assert.True(saved.Ok, saved.Error);
    }

    [Theory]
    [InlineData(PathingApi.Roads, """{"roads":[null]}""", "roads[0] is missing")]
    [InlineData(PathingApi.Roads, """{"roads":[{"name":"a","width":2,"points":[{"x":1,"y":2}]},null]}""", "roads[1] is missing")]
    [InlineData(PathingApi.Danger, """{"circles":[null],"rectangles":[]}""", "circles[0] is missing")]
    [InlineData(PathingApi.Danger, """{"circles":[],"rectangles":[null]}""", "rectangles[0] is missing")]
    public async Task A_missing_entry_is_a_bad_request(string kind, string body, string why)
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);

        using HttpResponseMessage response = await factory.CreateClient().PutAsync($"{PathingApi.CostZones}/Azeroth/{kind}/1429",
            new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(why, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // Minimal APIs throw these in Development, the environment run.sh and the tests use.
    [Fact]
    public async Task A_request_the_service_cannot_read_is_a_bad_request_not_its_failure()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        HttpClient http = factory.CreateClient();

        using HttpResponseMessage points = await http.PutAsync($"{PathingApi.CostZones}/Azeroth/{PathingApi.Roads}/1429",
            new StringContent("""{"roads":[{"name":"a","width":2,"points":[null]}]}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        using HttpResponseMessage json = await http.PutAsync($"{PathingApi.CostZones}/Azeroth/{PathingApi.Roads}/1429",
            new StringContent("{not json", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        using HttpResponseMessage number = await http.GetAsync($"{PathingApi.Area}?mapId=0&x=abc&y=1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, points.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, number.StatusCode);
        JsonElement problem = await number.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("float x", problem.GetProperty("detail").GetString());
    }

    // The planner opens a lane on a continent whose tile folder exists, even an empty one; a
    // route there answers NotBaked and leaves the lane open.
    private static async Task OpenAzerothsLane(TempDataRoot data, PathingClient client)
    {
        Directory.CreateDirectory(NavmeshSettings.CacheDirectory(data.Config(), new NavmeshBakeOptions(), "Azeroth"));
        RouteResponse route = await client.FindRouteAsync(new RouteRequest(null, 1429, CoordinateSpace.World,
            new Point3(-8898.32f, -117.35608f, 81.840546f), new Point3(-8779.58f, -106.12012f, 0)), TestContext.Current.CancellationToken);
        Assert.Equal(RouteStatus.NotBaked, route.Status);

        ServiceResult<PathingHealth> health = await client.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Contains(health.Value!.Lanes, lane => lane.Continent == "Azeroth");
    }

    private static string WriteRoads(TempDataRoot data, string name, RoadData roads)
    {
        string folder = IOPath.Combine(data.Config().Road, "Azeroth");
        Directory.CreateDirectory(folder);
        string file = IOPath.Combine(folder, name);
        File.WriteAllText(file, JsonConvert.SerializeObject(roads, Formatting.Indented));
        return file;
    }
}
