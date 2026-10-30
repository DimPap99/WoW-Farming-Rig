using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;

using PathingService.CostZones;
using PathingService.Hosting;
using PathingService.World;

using PPather.Graph;
using PPather.Navmesh;

using SharedLib;

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using IOPath = System.IO.Path;

using Xunit;

namespace ServiceTests.Stores;

public sealed class CostZoneStoreTests
{
    private static readonly RoadZones Roads = new([
        new RoadLine("road", 2, [new(-120.283264f, -1814.0873f), new(-61.215378f, -1836.0492f)]),
    ]);

    private static readonly DangerZones Danger = new(
        [new DangerCircle("camp", -100f, -1800f, 40f, 5f, ZoneMode.Block)],
        [new DangerRectangle("cave", -200f, -1900f, -150f, -1850f, 3f)]);

    [Fact]
    public void Saved_roads_are_byte_for_byte_what_the_road_controller_wrote()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);

        Assert.Equal(StoreOutcome.Done, store.SaveRoads("Azeroth", 1429, Roads).Outcome);

        // Frontend/Controllers/UIMapJsonFiles.Write: Newtonsoft, indented, the engine's RoadData.
        string expected = JsonConvert.SerializeObject(CostZoneMapping.ToModel(Roads), Formatting.Indented);
        Assert.Equal(expected, File.ReadAllText(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
    }

    [Fact]
    public void Saved_zones_read_back_and_load_in_the_engine()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);
        store.SaveRoads("Azeroth", 1429, Roads);
        store.SaveDanger("Azeroth", 1429, Danger);

        CostZoneSet set = store.Get("Azeroth").Value!;

        Assert.Equal(Roads.Roads[0].Points, set.Roads[1429].Roads[0].Points);
        Assert.Equal(Danger.Circles, set.Danger[1429].Circles);
        Assert.Equal(Danger.Rectangles, set.Danger[1429].Rectangles);
        Assert.True(CostZoneLoader.TryLoad(NullLogger.Instance, data.Config().Road, "Azeroth", out CostZones zones));
        Assert.False(zones.IsEmpty);
    }

    [Fact]
    public void An_invalid_road_is_refused_with_the_loaders_reason()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);

        StoreResult saved = store.SaveRoads("Azeroth", 1429, new([new RoadLine("wide", 99, [new(0, 0)])]));

        Assert.Equal(StoreOutcome.Invalid, saved.Outcome);
        Assert.Equal(CostZoneLoader.Problem(new RoadData { Roads = [new RoadSegment("wide", 99, [new(0, 0)])] }), saved.Error);
        Assert.False(Directory.Exists(IOPath.Combine(data.Config().Road, "Azeroth")));
    }

    [Fact]
    public void A_road_that_would_take_too_long_to_load_is_refused_naming_it()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);
        // Two points, corner to corner: the leg's bounding box is the whole map, about 2.6e7 cells.
        RoadZones diagonal = new([
            new RoadLine("short", 2, [new(0, 0), new(100, 100)]),
            new RoadLine("diagonal", 64, [new(-40000, -40000), new(40000, 40000)]),
        ]);

        StoreResult saved = store.SaveRoads("Azeroth", 1429, diagonal);

        Assert.Equal(StoreOutcome.Invalid, saved.Outcome);
        Assert.Contains("road 'diagonal'", saved.Error);
        Assert.Contains("add points along it", saved.Error);
        Assert.False(Directory.Exists(IOPath.Combine(data.Config().Road, "Azeroth")));
    }

    [Fact]
    public void Danger_zones_that_would_take_too_long_to_load_are_refused()
    {
        using TempDataRoot data = new(withMapData: true);
        // Each rectangle is within the size cap, about 90,000 cells; thirty of them are not.
        DangerRectangle[] many = [.. Enumerable.Range(0, 30)
            .Select(i => new DangerRectangle($"r{i}", i * 100, 0, (i * 100) + 5000, 5000, 1))];

        StoreResult saved = Store(data).SaveDanger("Azeroth", 1429, new([], many));

        Assert.Equal(StoreOutcome.Invalid, saved.Outcome);
        Assert.Contains("danger zones would take", saved.Error);
        Assert.Contains("rectangle 'r0'", saved.Error);
    }

    [Fact]
    public async Task A_file_on_disk_past_the_cap_is_skipped_and_the_rest_load()
    {
        using TempDataRoot data = new(withMapData: true);
        string continent = IOPath.Combine(data.Config().Road, "Azeroth");
        Directory.CreateDirectory(continent);
        File.WriteAllText(IOPath.Combine(continent, "1429.json"), JsonConvert.SerializeObject(CostZoneMapping.ToModel(Roads)));
        // As a writer that skips the check would leave it: a zig-zag from corner to corner.
        Vector2[] zigzag = [.. Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? new Vector2(-40000, -40000) : new Vector2(40000, 40000))];
        File.WriteAllText(IOPath.Combine(continent, "1430.json"), JsonConvert.SerializeObject(new RoadData
        {
            Roads = [new RoadSegment("zigzag", 64, zigzag)],
        }));

        (bool complete, CostZones zones) = await Task.Run(() =>
            {
                bool all = CostZoneLoader.TryLoad(NullLogger.Instance, data.Config().Road, "Azeroth", out CostZones loaded);
                return (all, loaded);
            }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(complete);
        Assert.False(zones.IsEmpty);
        Assert.Equal(1f, zones.CostFactor(-90f, -1825f));
    }

    [Fact]
    public void Every_zone_file_in_the_repository_is_well_within_the_cap()
    {
        string[] files = Directory.GetFiles(IOPath.Combine(TestData.RepoJson, "road"), "*.json", SearchOption.AllDirectories);

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            (string? problem, double cells) = IOPath.GetFileName(IOPath.GetDirectoryName(file)) == CostZoneLoader.DangerZoneFolder
                ? Check(JsonConvert.DeserializeObject<DangerZoneData>(text)!)
                : Check(JsonConvert.DeserializeObject<RoadData>(text)!);

            Assert.Null(problem);
            Assert.True(cells < CostZoneLoader.MaxRasterCells / 10, $"{file}: {cells} cells");
        }
    }

    // The store as the service has it: the client's map data names its continents.
    private static CostZoneStore Store(TempDataRoot data)
    {
        return new CostZoneStore(data.Config(), new ClientContinents(data.Config(), new WorldMapAreaDB(data.Config())));
    }

    private static (string? Problem, double Cells) Check(RoadData data)
    {
        return (CostZoneLoader.Problem(data), data.Roads.Sum(r => CostZones.RasterCells(r)));
    }

    private static (string? Problem, double Cells) Check(DangerZoneData data)
    {
        return (CostZoneLoader.Problem(data),
            data.Circles.Sum(c => CostZones.RasterCells(c)) + data.Rectangles.Sum(r => CostZones.RasterCells(r)));
    }

    [Fact]
    public void An_unknown_danger_mode_is_refused()
    {
        using TempDataRoot data = new(withMapData: true);
        DangerZones odd = new([new DangerCircle("odd", 0, 0, 10, 1, (ZoneMode)7)], []);

        Assert.Equal(StoreOutcome.Invalid, Store(data).SaveDanger("Azeroth", 1429, odd).Outcome);
    }

    [Fact]
    public void Saving_nothing_deletes_the_maps_file()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);
        store.SaveRoads("Azeroth", 1429, Roads);

        Assert.Equal(StoreOutcome.Done, store.SaveRoads("Azeroth", 1429, new([])).Outcome);
        Assert.False(File.Exists(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
    }

    [Fact]
    public void The_legacy_single_road_file_reads_as_map_zero()
    {
        using TempDataRoot data = new(withMapData: true);
        Directory.CreateDirectory(data.Config().Road);
        File.WriteAllText(IOPath.Combine(data.Config().Road, "Kalimdor.json"),
            JsonConvert.SerializeObject(CostZoneMapping.ToModel(Roads)));

        CostZoneSet set = Store(data).Get("Kalimdor").Value!;

        Assert.Equal(Roads.Roads[0].Points, set.Roads[CostZoneStore.LegacyRoadUiMap].Roads[0].Points);
    }

    [Fact]
    public void A_continent_is_named_in_the_clients_own_case()
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);

        Assert.Equal(StoreOutcome.Done, store.SaveRoads("azeroth", 1429, Roads).Outcome);

        Assert.True(File.Exists(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
        Assert.Equal("Azeroth", store.Get("AZEROTH").Value!.Continent);
        Assert.Single(Directory.GetDirectories(data.Config().Road));
    }

    [Theory]
    [InlineData("../Azeroth")]
    [InlineData("Azeroth/dangerzone")]
    [InlineData("")]
    [InlineData("Northrend")]
    [InlineData("Narnia")]
    public void Continent_names_are_the_clients_own_continents(string continent)
    {
        using TempDataRoot data = new(withMapData: true);
        CostZoneStore store = Store(data);

        Assert.Equal(StoreOutcome.Invalid, store.Get(continent).Outcome);
        Assert.Equal(StoreOutcome.Invalid, store.SaveRoads(continent, 1429, Roads).Outcome);
    }
}
