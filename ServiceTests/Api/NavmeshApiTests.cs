using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using PPather.Navmesh;
using PPather.Planning;

using ServiceClients;
using ServiceClients.Pathing;

using SharedLib;

using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>Tile listing, cache clearing and the bake admin, on a data root without game files.</summary>
public sealed class NavmeshApiTests
{
    [Fact]
    public async Task A_bake_without_game_files_is_refused_with_the_reason()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<BakeStatus> bake = await client.StartBakeAsync(new BakeRequest("Azeroth"), TestContext.Current.CancellationToken);
        ServiceResult grid = await client.BuildAreaGridAsync("Azeroth", TestContext.Current.CancellationToken);
        ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)HttpStatusCode.Conflict, bake.Status);
        Assert.Contains("no game files", bake.Error);
        Assert.Equal((int)HttpStatusCode.Conflict, grid.Status);
        Assert.False(capabilities.Value!.GameFiles);
    }

    // Json/MPQ is one folder for every client, and Forever's continents open from another client's
    // archives too: a Forever instance has game files only through ForeverCasc (known issue 54).
    [Fact]
    public async Task Archives_in_Json_MPQ_are_not_game_files_for_Forever()
    {
        using TempDataRoot classic = new(withMapData: true);
        using TempDataRoot forever = new(withMapData: true, client: "forever");
        foreach (TempDataRoot root in (TempDataRoot[])[classic, forever])
        {
            Directory.CreateDirectory(root.Config().MPQ);
            File.WriteAllBytes(Path.Combine(root.Config().MPQ, "common.MPQ"), [0]);
        }

        using PathingServiceFactory classicService = new(classic.Root);
        using PathingServiceFactory foreverService = new(forever.Root, forever.Client);
        PathingClient classicClient = new(classicService.CreateClient());
        PathingClient foreverClient = new(foreverService.CreateClient());

        ServiceResult<PathingCapabilities> classicCapabilities = await classicClient.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        ServiceResult<PathingCapabilities> foreverCapabilities = await foreverClient.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> foreverBake = await foreverClient.StartBakeAsync(new BakeRequest("Azeroth"), TestContext.Current.CancellationToken);
        ServiceResult foreverGrid = await foreverClient.BuildAreaGridAsync("Azeroth", TestContext.Current.CancellationToken);

        Assert.True(classicCapabilities.Value!.GameFiles);
        Assert.Equal("forever", foreverCapabilities.Value!.Client);
        Assert.False(foreverCapabilities.Value.GameFiles);
        Assert.Equal((int)HttpStatusCode.Conflict, foreverBake.Status);
        Assert.Contains("ForeverCasc", foreverBake.Error);
        Assert.Equal((int)HttpStatusCode.Conflict, foreverGrid.Status);
    }

    // The world view asks for the relief of any continent it shows; one nothing was baked for has
    // none, which threw for the missing folder (500).
    [Fact]
    public async Task A_continent_with_nothing_baked_has_an_empty_relief()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        HttpClient http = factory.CreateClient();

        NavmeshOverview? overview = await http.GetFromJsonAsync<NavmeshOverview>($"{PathingApi.Navmesh}/Azeroth/{PathingApi.Overview}",
            ContractJson.Options, TestContext.Current.CancellationToken);

        Assert.NotNull(overview);
        Assert.Empty(overview.Tiles);
        Assert.Empty(overview.Heights);
        Assert.False(Directory.Exists(NavmeshSettings.CacheDirectory(data.Config(), new NavmeshBakeOptions(), "Azeroth")));
    }

    [Fact]
    public async Task Bake_requests_are_checked_before_anything_runs()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<BakeStatus> halfAdt = await client.StartBakeAsync(new BakeRequest("Azeroth", AdtX: 30),
            TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> adtWithoutContinent = await client.StartBakeAsync(new BakeRequest(null, 30, 49),
            TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> offGrid = await client.StartBakeAsync(new BakeRequest("Azeroth", 30, 64),
            TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> unknown = await client.StartBakeAsync(new BakeRequest("Atlantis"),
            TestContext.Current.CancellationToken);

        Assert.Equal((int)HttpStatusCode.BadRequest, halfAdt.Status);
        Assert.Contains("both", halfAdt.Error);
        Assert.Equal((int)HttpStatusCode.BadRequest, adtWithoutContinent.Status);
        Assert.Equal((int)HttpStatusCode.BadRequest, offGrid.Status);
        Assert.Equal((int)HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("Azeroth", unknown.Error);
    }

    [Fact]
    public async Task With_no_bake_running_there_is_nothing_to_cancel()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<BakeStatus> status = await client.GetBakeStatusAsync(TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> cancel = await client.CancelBakeAsync(TestContext.Current.CancellationToken);
        ServiceResult<PathingHealth> health = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Ok, status.Error);
        Assert.False(status.Value.Running);
        Assert.Equal((int)HttpStatusCode.Conflict, cancel.Status);
        Assert.False(health.Value!.Bake.Running);
    }

    [Fact]
    public async Task Baked_tiles_are_listed_with_their_bounds_and_cleared()
    {
        using TempDataRoot data = new(withMapData: true);
        string cache = NavmeshSettings.CacheDirectory(data.Config(), new NavmeshBakeOptions(), "Azeroth");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "tile_106_146.dnm"), new byte[100]);
        File.WriteAllBytes(Path.Combine(cache, "tile_107_146.dnm"), new byte[50]);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NavmeshTile[]> listed = await client.GetNavmeshTilesAsync("Azeroth", TestContext.Current.CancellationToken);
        ServiceResult<NavmeshTile[]> unknown = await client.GetNavmeshTilesAsync("Atlantis", TestContext.Current.CancellationToken);
        ServiceResult<CacheCleared> cleared = await client.ClearNavmeshAsync("Azeroth", TestContext.Current.CancellationToken);
        ServiceResult<NavmeshTile[]> after = await client.GetNavmeshTilesAsync("Azeroth", TestContext.Current.CancellationToken);
        ServiceResult<PathingCapabilities> capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.True(listed.Ok, listed.Error);
        Assert.Equal(2, listed.Value.Length);
        NavmeshTile tile = Assert.Single(listed.Value, t => t.X == 106);
        NavmeshCoords.GetTileWowBounds(106, 146, out float minX, out float minY, out float maxX, out float maxY);
        Assert.Equal(new NavmeshTile(106, 146, minX, minY, maxX, maxY, false), tile);
        Assert.Equal((int)HttpStatusCode.BadRequest, unknown.Status);

        Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal(150, cleared.Value.FreedBytes);
        Assert.Empty(after.Value!);
        Assert.False(Directory.Exists(cache));
        Assert.Empty(capabilities.Value!.BakedContinents);
    }

    // The continent rule is one (ClientContinents): any case, answered and stored in the client's own.
    [Fact]
    public async Task A_continent_is_taken_in_any_case_everywhere()
    {
        using TempDataRoot data = new(withMapData: true);
        string cache = NavmeshSettings.CacheDirectory(data.Config(), new NavmeshBakeOptions(), "Azeroth");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "tile_106_146.dnm"), new byte[100]);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NavmeshTile[]> listed = await client.GetNavmeshTilesAsync("azeroth", TestContext.Current.CancellationToken);
        ServiceResult saved = await client.SaveRoadsAsync("AZEROTH", 1429, new RoadZones([]), TestContext.Current.CancellationToken);
        ServiceResult<CacheCleared> cleared = await client.ClearNavmeshAsync("azeroth", TestContext.Current.CancellationToken);
        ServiceResult<BakeStatus> unknown = await client.StartBakeAsync(new BakeRequest("atlantis"), TestContext.Current.CancellationToken);

        Assert.True(listed.Ok, listed.Error);
        Assert.Single(listed.Value);
        Assert.True(saved.Ok, saved.Error);
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal("Azeroth", cleared.Value.Continent);
        Assert.False(Directory.Exists(cache));
        Assert.Equal((int)HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("Kalimdor", unknown.Error);
    }

    // A lane holds the tiles it read; a clear deletes them on disk and then has the planner let go,
    // whatever the delete managed (known issues 12 and 52).
    [Fact]
    public async Task A_clear_makes_the_planner_let_go_of_the_continents_tiles()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using TempDataRoot data = new(withMapData: true);
        string source = NavmeshSettings.CacheDirectory(TestData.RepoConfig(), new NavmeshBakeOptions(), "Azeroth");
        string cache = NavmeshSettings.CacheDirectory(data.Config(), new NavmeshBakeOptions(), "Azeroth");
        Directory.CreateDirectory(cache);
        RouteRequest elwynn = new(null, 1429, CoordinateSpace.World, new(-8898.32f, -117.35f, 81.84f), new(-8779.58f, -106.12f, 0));
        NavmeshCoords.GetTileIndex(elwynn.From.X, elwynn.From.Y, out int tileX, out int tileZ);
        for (int x = tileX - 3; x <= tileX + 3; x++)
        {
            for (int z = tileZ - 3; z <= tileZ + 3; z++)
            {
                string tile = Path.Combine(source, $"tile_{x}_{z}.dnm");
                if (File.Exists(tile))
                {
                    File.Copy(tile, Path.Combine(cache, Path.GetFileName(tile)));
                }
            }
        }

        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());
        RoutePlanner planner = factory.Services.GetRequiredService<RoutePlanner>();

        RouteResponse before = await client.FindRouteAsync(elwynn, TestContext.Current.CancellationToken);
        bool heldBefore = planner.ResidentTileSet("Azeroth").Count > 0;
        ServiceResult<CacheCleared> cleared = await client.ClearNavmeshAsync("Azeroth", TestContext.Current.CancellationToken);
        bool heldAfter = planner.ResidentTileSet("Azeroth").Count > 0;
        RouteResponse after = await client.FindRouteAsync(elwynn, TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.Found, before.Status);
        Assert.True(heldBefore);
        Assert.True(cleared.Ok, cleared.Error);
        Assert.False(heldAfter, "the lane should let go of the deleted tiles");
        Assert.Equal(RouteStatus.NotBaked, after.Status);
    }
}
