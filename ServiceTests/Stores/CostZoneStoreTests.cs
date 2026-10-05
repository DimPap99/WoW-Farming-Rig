using Contracts;
using Contracts.Pathing;

using Newtonsoft.Json;

using PathingService.CostZones;
using PathingService.Hosting;

using PPather.Graph;
using PPather.Navmesh;

using System.IO;

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
        using TempDataRoot data = new();
        CostZoneStore store = new(data.Config());

        Assert.Equal(StoreOutcome.Done, store.SaveRoads("Azeroth", 1429, Roads).Outcome);

        // Frontend/Controllers/UIMapJsonFiles.Write: Newtonsoft, indented, the engine's RoadData.
        string expected = JsonConvert.SerializeObject(CostZoneMapping.ToModel(Roads), Formatting.Indented);
        Assert.Equal(expected, File.ReadAllText(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
    }

    [Fact]
    public void Saved_zones_read_back_and_load_in_the_engine()
    {
        using TempDataRoot data = new();
        CostZoneStore store = new(data.Config());
        store.SaveRoads("Azeroth", 1429, Roads);
        store.SaveDanger("Azeroth", 1429, Danger);

        CostZoneSet set = store.Get("Azeroth").Value!;

        Assert.Equal(Roads.Roads[0].Points, set.Roads[1429].Roads[0].Points);
        Assert.Equal(Danger.Circles, set.Danger[1429].Circles);
        Assert.Equal(Danger.Rectangles, set.Danger[1429].Rectangles);
        Assert.True(CostZoneLoader.TryLoad(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            data.Config().Road, "Azeroth", out CostZones zones));
        Assert.False(zones.IsEmpty);
    }

    [Fact]
    public void An_invalid_road_is_refused_with_the_loaders_reason()
    {
        using TempDataRoot data = new();
        CostZoneStore store = new(data.Config());

        StoreResult saved = store.SaveRoads("Azeroth", 1429, new([new RoadLine("wide", 99, [new(0, 0)])]));

        Assert.Equal(StoreOutcome.Invalid, saved.Outcome);
        Assert.Equal(CostZoneLoader.Problem(new RoadData { Roads = [new RoadSegment("wide", 99, [new(0, 0)])] }), saved.Error);
        Assert.False(Directory.Exists(IOPath.Combine(data.Config().Road, "Azeroth")));
    }

    [Fact]
    public void An_unknown_danger_mode_is_refused()
    {
        using TempDataRoot data = new();
        DangerZones odd = new([new DangerCircle("odd", 0, 0, 10, 1, (ZoneMode)7)], []);

        Assert.Equal(StoreOutcome.Invalid, new CostZoneStore(data.Config()).SaveDanger("Azeroth", 1429, odd).Outcome);
    }

    [Fact]
    public void Saving_nothing_deletes_the_maps_file()
    {
        using TempDataRoot data = new();
        CostZoneStore store = new(data.Config());
        store.SaveRoads("Azeroth", 1429, Roads);

        Assert.Equal(StoreOutcome.Done, store.SaveRoads("Azeroth", 1429, new([])).Outcome);
        Assert.False(File.Exists(IOPath.Combine(data.Config().Road, "Azeroth", "1429.json")));
    }

    [Fact]
    public void The_legacy_single_road_file_reads_as_map_zero()
    {
        using TempDataRoot data = new();
        Directory.CreateDirectory(data.Config().Road);
        File.WriteAllText(IOPath.Combine(data.Config().Road, "Kalimdor.json"),
            JsonConvert.SerializeObject(CostZoneMapping.ToModel(Roads)));

        CostZoneSet set = new CostZoneStore(data.Config()).Get("Kalimdor").Value!;

        Assert.Equal(Roads.Roads[0].Points, set.Roads[CostZoneStore.LegacyRoadUiMap].Roads[0].Points);
    }

    [Theory]
    [InlineData("../Azeroth")]
    [InlineData("Azeroth/dangerzone")]
    [InlineData("")]
    public void Continent_names_are_plain_names(string continent)
    {
        using TempDataRoot data = new();
        CostZoneStore store = new(data.Config());

        Assert.Equal(StoreOutcome.Invalid, store.Get(continent).Outcome);
        Assert.Equal(StoreOutcome.Invalid, store.SaveRoads(continent, 1429, Roads).Outcome);
    }
}
