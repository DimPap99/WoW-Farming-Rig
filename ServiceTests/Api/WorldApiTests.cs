using Contracts;
using Contracts.Pathing;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>World data, the catalog and the static files the map reads, through the client and plain HTTP.</summary>
public sealed class WorldApiTests
{
    [Fact]
    public async Task World_info_describes_the_client()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<WorldInfo> world = await client.GetWorldAsync(TestContext.Current.CancellationToken);

        Assert.True(world.Ok, world.Error);
        Assert.Equal(TestData.Client, world.Value.Client);
        Assert.Equal("precata", world.Value.Era);
        Assert.True(world.Value.HasMap);
        Assert.Contains(world.Value.Continents, c => c.Name == "Azeroth" && c.MapId == 0 && c.Tiles == 0);
        ZoneInfo elwynn = Assert.Single(world.Value.Zones, z => z.UiMapId == 1429);
        Assert.Equal("Elwynn Forest", elwynn.Name);
        Assert.Equal("Azeroth", elwynn.Continent);
        Assert.Contains("vendor", world.Value.NpcFlags.Keys);
    }

    [Fact]
    public async Task Npcs_of_a_type_are_listed_for_a_zone()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NpcInfo[]> vendors = await client.GetNpcsAsync(1429, "vendor", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> unknownType = await client.GetNpcsAsync(1429, "dragon", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> unknownZone = await client.GetNpcsAsync(999999, "vendor", TestContext.Current.CancellationToken);

        Assert.True(vendors.Ok, vendors.Error);
        Assert.NotEmpty(vendors.Value);
        Assert.Equal(vendors.Value.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Select(n => n.Name),
            vendors.Value.Select(n => n.Name));
        Assert.All(vendors.Value, n => Assert.NotEqual(0f, n.Position.X));
        Assert.Equal((int)HttpStatusCode.BadRequest, unknownType.Status);
        Assert.Contains("vendor", unknownType.Error);
        Assert.Equal((int)HttpStatusCode.BadRequest, unknownZone.Status);
    }

    [Fact]
    public async Task A_zones_layers_list_what_is_inside_it_in_world_yards()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());
        ServiceResult<WorldInfo> world = await client.GetWorldAsync(TestContext.Current.CancellationToken);
        ZoneInfo durotar = Assert.Single(world.Value!.Zones, z => z.UiMapId == 1411);

        foreach (string layer in SpawnLayers.All)
        {
            ServiceResult<SpawnPoint[]> spawns = await client.GetSpawnsAsync(1411, layer, TestContext.Current.CancellationToken);

            Assert.True(spawns.Ok, $"{layer}: {spawns.Error}");
            Assert.True(spawns.Value.Length > 0, $"no {layer} in Durotar");
            // World yards near Durotar, most of them on its map: the zone's area box reaches a little past the map's edge.
            Point3[] onMap = [.. spawns.Value.Select(s => ZoneMath.ToMap(durotar, s.At))];
            Assert.All(onMap, p =>
            {
                Assert.InRange(p.X, -20, 120);
                Assert.InRange(p.Y, -20, 120);
            });
            Assert.True(onMap.Count(p => p.X is >= 0 and <= 100 && p.Y is >= 0 and <= 100) * 2 > onMap.Length, $"{layer}: most are off Durotar's map");
        }

        ServiceResult<SpawnPoint[]> herbs = await client.GetSpawnsAsync(1411, SpawnLayers.Herb, TestContext.Current.CancellationToken);
        ServiceResult<SpawnPoint[]> unknown = await client.GetSpawnsAsync(1411, "dragons", TestContext.Current.CancellationToken);

        Assert.Contains(herbs.Value!, h => h.Name == "Peacebloom");
        Assert.Equal((int)HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("herb", unknown.Error);
    }

    [Fact]
    public async Task Creatures_are_found_by_a_part_of_their_name_on_one_continent()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NpcInfo[]> onElwynnsContinent = await client.GetNpcsAsync(1429, "vendor", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> hogger = await client.SearchNpcsAsync(0, "hogg", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> notOnKalimdor = await client.SearchNpcsAsync(1, "Hogger", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> tooShort = await client.SearchNpcsAsync(0, "h", TestContext.Current.CancellationToken);

        Assert.True(onElwynnsContinent.Ok);
        Assert.Contains(hogger.Value!, n => n.Name == "Hogger");
        Assert.DoesNotContain(notOnKalimdor.Value!, n => n.Name == "Hogger");
        Assert.Equal((int)HttpStatusCode.BadRequest, tooShort.Status);
    }

    [Fact]
    public async Task Locations_hold_the_curated_spots_and_the_teleports()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NamedLocation[]> locations = await client.GetLocationsAsync(TestContext.Current.CancellationToken);

        Assert.True(locations.Ok, locations.Error);
        NamedLocation crossroads = Assert.Single(locations.Value, l => l.Name == "Barrens, Crossroads");
        Assert.Equal("Kalimdor", crossroads.Continent);
        Assert.Equal(1, crossroads.MapId);
        Assert.True(locations.Value.Length > 500, $"{locations.Value.Length} locations: the teleports are missing");
    }

    [Fact]
    public async Task Test_routes_name_zones_the_client_has_and_are_answers_not_refusals()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<TestRoute[]> routes = await client.GetTestRoutesAsync(TestContext.Current.CancellationToken);
        ServiceResult<WorldInfo> world = await client.GetWorldAsync(TestContext.Current.CancellationToken);

        Assert.True(routes.Ok, routes.Error);
        Assert.NotEmpty(routes.Value);
        Assert.All(routes.Value, r => Assert.Contains(world.Value!.Zones, z => z.UiMapId == r.Forward.UiMapId));
        Assert.DoesNotContain(routes.Value, r => r.Forward.UiMapId == 1943);

        if (!TestData.HasPrecataTiles)
        {
            return;
        }

        foreach (TestRoute route in routes.Value)
        {
            RouteResponse answer = await client.FindRouteAsync(route.Forward, TestContext.Current.CancellationToken);
            Assert.True(answer.Status is RouteStatus.Found or RouteStatus.NoPath or RouteStatus.NotBaked,
                $"{route.Zone} / {route.Name}: {answer.Status} {answer.Detail}");
        }
    }

    [Fact]
    public async Task The_map_data_is_served_where_the_map_fetches_it()
    {
        using TempDataRoot data = new(withMapData: true);
        string locations = Path.Combine(data.Root, "locations");
        Directory.CreateDirectory(locations);
        File.Copy(Path.Combine(TestData.RepoJson, "locations", DataConfig.TeleportLocationsFile),
            Path.Combine(locations, DataConfig.TeleportLocationsFile));
        using PathingServiceFactory factory = new(data.Root);
        HttpClient http = factory.CreateClient();

        using HttpResponseMessage dbc = await http.GetAsync($"{WorldData.Dbc}/WorldMapArea.json", TestContext.Current.CancellationToken);
        using HttpResponseMessage teleports = await http.GetAsync($"{WorldData.Locations}/{DataConfig.TeleportLocationsFile}",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage missing = await http.GetAsync($"{WorldData.Tiles}/Azeroth/z2x0y0.webp", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, dbc.StatusCode);
        Assert.Equal("application/json", dbc.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, teleports.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Minimap_tiles_are_served_when_downloaded()
    {
        string tile = Path.Combine(TestData.RepoJson, "leaflet", "precata", "Azeroth", "z2x0y0.webp");
        Assert.SkipUnless(File.Exists(tile), "needs the precata minimap tiles");
        using PathingServiceFactory factory = new(TestData.RepoJson);

        using HttpResponseMessage response = await factory.CreateClient().GetAsync($"{WorldData.Tiles}/Azeroth/z2x0y0.webp",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Only_the_cata_era_reads_shared_tiles_from_precata()
    {
        DataConfig som = new() { Root = "Json", Exp = "som" };
        DataConfig cata = new() { Root = "Json", Exp = "cata" };

        Assert.Empty(som.SharedTileFolders());
        Assert.Equal(["Northrend", "Expansion01"], cata.SharedTileFolders().Select(f => f.Continent));
        Assert.All(cata.SharedTileFolders(), f => Assert.Contains("precata", f.Folder));
    }
}
