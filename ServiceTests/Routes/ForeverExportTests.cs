using Contracts;
using Contracts.Pathing;

using ControlCenter.Routes;

using ServiceClients.Pathing;

using SharedLib;
using SharedLib.Forever;

using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>
/// A route designed on the Control Center's map, saved as a Forever route file: the text must load
/// as a run loads it (<see cref="ForeverRouteFile"/>), with each point where it was drawn.
/// </summary>
public sealed class ForeverExportTests
{
    private const int Durotar = 1411;
    private const int Barrens = 1413;
    private const int Kalimdor = 1;

    private static readonly WorldMapAreaDB Maps = new(new DataConfig { Root = TestData.RepoJson, Exp = "forever" });

    private static readonly ZoneInfo[] Zones = [.. Maps.Values.Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent,
        a.MapID, a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];

    private static Point3 On(int uiMapId, float x, float y)
    {
        return ZoneMath.ToWorld(Zones.Single(z => z.UiMapId == uiMapId), new Point3(x, y, 0));
    }

    private static ForeverRoute Load(string text)
    {
        Assert.True(ForeverRouteFile.TryParse(text, "designed.json", Maps, out ForeverRoute route, out string error), error + "\n" + text);
        return route;
    }

    [Fact]
    public void Waypoints_in_two_zones_travel_one_step_per_zone_through_each_point()
    {
        Point3[] waypoints = [On(Durotar, 50, 40), On(Durotar, 45, 45), On(Barrens, 60, 30), On(Barrens, 55, 35)];

        Assert.True(ForeverExport.TryBuild("Across", waypoints, [], ForeverExport.DefaultKills, Zones, Kalimdor, out string text, out string error), error);
        ForeverRoute route = Load(text);

        Assert.Equal("Across", route.Name);
        Assert.Collection(route.Tasks,
            task =>
            {
                ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(task);
                Assert.Equal(new ForeverMapPoint(Durotar, 45, 45), travel.To);
                Assert.Equal([new ForeverMapPoint(Durotar, 50, 40)], travel.Via);
            },
            task =>
            {
                ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(task);
                Assert.Equal(new ForeverMapPoint(Barrens, 55, 35), travel.To);
                Assert.Equal([new ForeverMapPoint(Barrens, 60, 30)], travel.Via);
            });
    }

    [Fact]
    public void An_area_is_roamed_after_the_waypoint_nearest_it()
    {
        Point3[] waypoints = [On(Durotar, 30, 30), On(Durotar, 50, 50), On(Durotar, 70, 70)];
        IReadOnlyList<Point3>[] areas = [[On(Durotar, 51, 51), On(Durotar, 55, 51), On(Durotar, 53, 55)]];

        Assert.True(ForeverExport.TryBuild("Roam on the way", waypoints, areas, 7, Zones, Kalimdor, out string text, out string error), error);
        ForeverRoute route = Load(text);

        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverTravelTask { To: { X: 50, Y: 50 }, Via.Count: 1 }),
            task => Assert.True(task is ForeverRoamAreaTask { Until.Kills: 7, Area.Corners.Count: 3 }),
            task => Assert.True(task is ForeverTravelTask { To: { X: 70, Y: 70 }, Via.Count: 0 }));
    }

    [Fact]
    public void A_rectangle_is_two_opposite_corners_and_areas_alone_make_a_route()
    {
        IReadOnlyList<Point3>[] areas = [[On(Durotar, 51.5f, 44), On(Durotar, 53.5f, 46)]];

        Assert.True(ForeverExport.TryBuild("Just roam", [], areas, 3, Zones, Kalimdor, out string text, out string error), error);
        ForeverRoute route = Load(text);

        ForeverRoamAreaTask roam = Assert.IsType<ForeverRoamAreaTask>(Assert.Single(route.Tasks));
        Assert.Equal(4, roam.Area.Corners.Count);
    }

    [Fact]
    public void A_long_travel_wraps_its_via_points_and_still_loads()
    {
        Point3[] waypoints = [.. Enumerable.Range(0, 20).Select(i => On(Durotar, 40 + i, 40 + (i / 2f)))];

        Assert.True(ForeverExport.TryBuild("Long", waypoints, [], 1, Zones, Kalimdor, out string text, out string error), error);
        ForeverRoute route = Load(text);

        ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(Assert.Single(route.Tasks));
        Assert.Equal(19, travel.Via.Count);
        Assert.Contains("\"Via\": [\n", text);
    }

    [Fact]
    public void An_area_no_zone_map_holds_is_named_on_the_continents_map()
    {
        int tanaris = Zones.Single(z => z.Name == "Tanaris").UiMapId;
        IReadOnlyList<Point3>[] across = [[On(Durotar, 50, 50), On(tanaris, 50, 50), On(tanaris, 60, 40)]];
        int continentMap = Zones.Single(z => z.Name == "Kalimdor" && z.MapId == Kalimdor).UiMapId;

        Assert.True(ForeverExport.TryBuild("Wide", [], across, 1, Zones, Kalimdor, out string text, out string error), error);
        ForeverRoute route = Load(text);

        ForeverRoamAreaTask roam = Assert.IsType<ForeverRoamAreaTask>(Assert.Single(route.Tasks));
        Assert.Equal(continentMap, roam.Area.UIMapId);
    }

    [Fact]
    public void A_point_off_every_map_and_an_empty_route_are_refused()
    {
        Point3 nowhere = new(20000, 20000, 0);
        IReadOnlyList<Point3>[] offMap = [[nowhere, new Point3(20010, 20010, 0)]];

        Assert.False(ForeverExport.TryBuild("Lost", [On(Durotar, 50, 50), nowhere], [], 1, Zones, Kalimdor, out _, out string lost));
        Assert.False(ForeverExport.TryBuild("Off", [], offMap, 1, Zones, Kalimdor, out _, out string off));
        Assert.False(ForeverExport.TryBuild("Empty", [], [], 1, Zones, Kalimdor, out _, out string empty));

        Assert.Contains("Waypoint 2 is in no zone", lost);
        Assert.Contains("Area 1 is not inside one zone", off);
        Assert.Contains("nothing to save", empty);
    }
}
