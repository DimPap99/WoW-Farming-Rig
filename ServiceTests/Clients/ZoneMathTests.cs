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
    public void The_smallest_zone_holding_a_point_wins()
    {
        ZoneInfo[] zones = [.. Maps.Values.Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent, a.MapID,
            a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];

        // Stormwind's trade district: inside Elwynn's map and Stormwind City's; the city is smaller.
        ZoneInfo? city = ZoneMath.ZoneAt(zones, 0, -8833f, 628f);
        ZoneInfo? forest = ZoneMath.ZoneAt(zones, 0, -9449f, 64f);
        ZoneInfo? wrongMap = ZoneMath.ZoneAt(zones, 1, -9449f, 64f);

        Assert.Equal("Stormwind City", city?.Name);
        Assert.Equal("Elwynn Forest", forest?.Name);
        Assert.NotEqual("Elwynn Forest", wrongMap?.Name);
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
