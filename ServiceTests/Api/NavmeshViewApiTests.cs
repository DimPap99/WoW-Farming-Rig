using Contracts;
using Contracts.Pathing;

using PPather.Navmesh;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The navmesh as a map draws it: a continent's relief, and one tile's polygons.</summary>
public sealed class NavmeshViewApiTests
{
    private static string Overview(string continent) => $"{PathingApi.Navmesh}/{continent}/{PathingApi.Overview}";

    private static string Tile(string continent, int x, int z) => $"{PathingApi.Navmesh}/{continent}/{PathingApi.Tiles}/{x}/{z}";

    [Fact]
    public async Task A_continents_overview_has_one_cell_per_baked_tile()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        HttpClient http = factory.CreateClient();

        NavmeshOverview? overview = await http.GetFromJsonAsync<NavmeshOverview>(Overview("Kalimdor"), ContractJson.Options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(overview);
        Assert.Equal(NavmeshSettings.TileWorldSize, overview.TileSize);
        Assert.Equal(NavmeshCoords.Origin, overview.Origin);
        Assert.Equal(overview.Heights.Length * 2, overview.Tiles.Length);
        Assert.True(overview.Heights.Length > 10_000, $"{overview.Heights.Length} tiles");

        // The grid as the overview states it is the one the tiles are named by.
        NavmeshCoords.GetTileWowBounds(overview.Tiles[0], overview.Tiles[1], out float minX, out float minY, out _, out _);
        Assert.Equal(overview.Origin + (overview.Tiles[1] * overview.TileSize), minX, 0.01f);
        Assert.Equal(overview.Origin + (overview.Tiles[0] * overview.TileSize), minY, 0.01f);
    }

    [Fact]
    public async Task A_tiles_polygons_lie_inside_it_and_an_unbaked_tile_is_not_found()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using PathingServiceFactory factory = new(TestData.RepoJson);
        HttpClient http = factory.CreateClient();

        // Razor Hill, Durotar.
        NavmeshCoords.GetTileIndex(200.97559f, -4785.549f, out int x, out int z);
        NavmeshTileGeometry? tile = await http.GetFromJsonAsync<NavmeshTileGeometry>(Tile("Kalimdor", x, z), ContractJson.Options,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage missing = await http.GetAsync(Tile("Kalimdor", 0, 0), TestContext.Current.CancellationToken);
        using HttpResponseMessage unknown = await http.GetAsync(Tile("Atlantis", x, z), TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.NotEmpty(tile.Water);
        NavmeshCoords.GetTileWowBounds(x, z, out float minX, out float minY, out float maxX, out float maxY);
        for (int v = 0; v < tile.Vertices.Length; v += 3)
        {
            Assert.InRange(tile.Vertices[v], minX - 1, maxX + 1);
            Assert.InRange(tile.Vertices[v + 1], minY - 1, maxY + 1);
        }

        int polygons = 0;
        for (int i = 0; i < tile.Polygons.Length; i += tile.Polygons[i] + 1)
        {
            polygons++;
        }

        Assert.Equal(tile.Water.Length, polygons);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
