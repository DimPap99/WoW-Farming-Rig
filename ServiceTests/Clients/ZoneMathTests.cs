using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using SharedLib;

using System.Linq;
using System.Numerics;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>The Control Center's coordinate arithmetic must be the engine's, to the bit.</summary>
public sealed class ZoneMathTests
{
    private static readonly WorldMapAreaDB Maps = new(TestData.RepoConfig());

    private static ZoneInfo Zone(int uiMapId)
    {
        Assert.True(Maps.TryGet(uiMapId, out WorldMapArea a));
        return new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent, a.MapID, a.LocLeft, a.LocRight, a.LocTop, a.LocBottom);
    }

    [Theory]
    [InlineData(1429, 48.5f, 59.1f)]
    [InlineData(1426, 28.7f, 70.1f)]
    [InlineData(1413, 0f, 100f)]
    public void Map_and_world_convert_as_WorldMapArea_does(int uiMapId, float mapX, float mapY)
    {
        Assert.True(Maps.TryGet(uiMapId, out WorldMapArea area));
        ZoneInfo zone = Zone(uiMapId);

        Vector3 engineWorld = WorldMapAreaDB.ToWorld_FlipXY(new Vector3(mapX, mapY, 0), area);
        Point3 world = ZoneMath.ToWorld(zone, new Point3(mapX, mapY, 0));
        Vector3 engineMap = WorldMapAreaDB.ToMap_FlipXY(engineWorld, area);
        Point3 map = ZoneMath.ToMap(zone, world);

        Assert.Equal(new Point3(engineWorld.X, engineWorld.Y, engineWorld.Z), world);
        Assert.Equal(new Point3(engineMap.X, engineMap.Y, engineMap.Z), map);
    }

    [Fact]
    public void A_point_is_in_the_zone_whose_map_it_lies_furthest_inside()
    {
        ZoneInfo[] zones = [.. Maps.Values.Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent, a.MapID,
            a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];
        ZoneInfo durotar = Zone(1411);
        ZoneInfo barrens = Zone(1413);

        // Stormwind's trade district: inside Elwynn's map and Stormwind City's, well inside the city's.
        ZoneInfo? city = ZoneMath.ZoneAt(zones, 0, -8833f, 628f);
        ZoneInfo? forest = ZoneMath.ZoneAt(zones, 0, -9449f, 64f);
        ZoneInfo? wrongMap = ZoneMath.ZoneAt(zones, 1, -9449f, 64f);
        // The east of the Barrens lies inside Durotar's map too, near its western edge.
        Point3 eastBarrens = ZoneMath.ToWorld(barrens, new Point3(55, 35, 0));
        Point3 westDurotar = ZoneMath.ToWorld(durotar, new Point3(35, 45, 0));
        // Between Durotar and Tanaris no zone's map holds both: only the continent's.
        Point3 tanaris = ZoneMath.ToWorld(Zone(1446), new Point3(50, 50, 0));

        Assert.Equal("Stormwind City", city?.Name);
        Assert.Equal("Elwynn Forest", forest?.Name);
        Assert.NotEqual("Elwynn Forest", wrongMap?.Name);
        Assert.True(ZoneMath.Contains(durotar, 1, eastBarrens.X, eastBarrens.Y));
        Assert.Equal("The Barrens", ZoneMath.ZoneAt(zones, 1, eastBarrens.X, eastBarrens.Y)?.Name);
        Assert.Equal("Durotar", ZoneMath.ZoneAt(zones, 1, westDurotar.X, westDurotar.Y)?.Name);
        Assert.Equal("Kalimdor", ZoneMath.ZoneHolding(zones, 1, [westDurotar, tanaris])?.Name);
        Assert.Null(ZoneMath.ZoneAt(zones, 1, 20000, 20000));
    }

    [Theory]
    [InlineData(14, 1411)]     // Durotar itself
    [InlineData(362, 1411)]    // Razor Hill, a town of Durotar
    [InlineData(1637, 1454)]   // Orgrimmar, a zone of its own
    public void An_area_lies_in_its_own_zone_or_its_parents(int areaId, int uiMapId)
    {
        Assert.True(Maps.TryGetZoneOfArea(areaId, out WorldMapArea zone));
        Assert.Equal(uiMapId, zone.UIMapId);
    }

    [Fact]
    public void An_area_the_map_data_does_not_know_lies_in_no_zone()
    {
        Assert.False(Maps.TryGetZoneOfArea(0, out _));
        Assert.False(Maps.TryGetZoneOfArea(987654, out _));
    }

    [Theory]
    [InlineData("16_Loch Modan", "Loch Modan")]
    [InlineData("_herb/Elwynn Forest Herbs", "Elwynn Forest")]
    [InlineData("crossroads_trip", null)]
    public void A_path_file_name_guesses_its_zone(string fileName, string? zoneName)
    {
        ZoneInfo[] zones = [.. Maps.Values.Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent, a.MapID,
            a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];

        Assert.Equal(zoneName, ZoneMath.GuessFromName(zones, fileName)?.Name);
    }
}
