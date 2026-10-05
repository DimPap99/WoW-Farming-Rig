using Contracts.Pathing;

using PPather.Navmesh;

using ServiceClients;
using ServiceClients.Pathing;

using SharedLib;

using System.IO;
using System.Linq;
using System.Net;
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
}
