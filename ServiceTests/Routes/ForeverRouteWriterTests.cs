using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;
using ServiceClients.Routes;

using SharedLib;
using SharedLib.Forever;

using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>
/// A route designed on the Control Center's map, saved as a Forever route file: the text must load as a
/// run loads it (<see cref="ForeverRouteFile"/>), with each point where it was drawn, each node's kind
/// the step it means, and the cost-zone choice and the loop the run's.
/// </summary>
public sealed class ForeverRouteWriterTests
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

    // The zone maps' guess, as the Control Center falls back to without the service's area grid.
    private static ZoneInfo? Guess(Point3 p)
    {
        return ZoneMath.ZoneAt(Zones, Kalimdor, p.X, p.Y);
    }

    // Shape nodes, planned as a Forever design starts: without the cost zones.
    private static RouteDesign Shapes(params Point3[] at)
    {
        return RouteDesign.For(GameClients.Forever) with { Nodes = [.. at.Select(p => new RouteNode(p))] };
    }

    private static RouteNode Area(int kills, params Point3[] corners)
    {
        return new RouteNode(RouteGeometry.Middle(corners), NodeKind.Area, Area: corners, Kills: kills);
    }

    private static string Write(string title, RouteDesign design)
    {
        Assert.True(ForeverRouteWriter.TryWrite(title, design, Guess, Zones, Kalimdor, out string text, out string error), error);
        return text;
    }

    private static ForeverRoute Load(string text)
    {
        Assert.True(ForeverRouteFile.TryParse(text, "designed.json", Maps, out ForeverRoute route, out string error), error + "\n" + text);
        return route;
    }

    [Fact]
    public void Shape_nodes_in_two_zones_travel_one_step_per_zone_through_each_point()
    {
        ForeverRoute route = Load(Write("Across", Shapes(On(Durotar, 50, 40), On(Durotar, 45, 45), On(Barrens, 60, 30), On(Barrens, 55, 35))));

        Assert.Equal("Across", route.Name);
        Assert.False(route.Repeat);
        Assert.False(route.ApplyCostZones);
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
    public void A_node_is_named_on_the_zone_it_is_in_when_that_zones_map_holds_it()
    {
        ZoneInfo durotar = Zones.Single(z => z.UiMapId == Durotar);
        ZoneInfo tanaris = Zones.Single(z => z.Name == "Tanaris");
        // South Durotar lies inside the Barrens' map too, further than inside Durotar's: the maps alone say the Barrens.
        Point3 southDurotar = On(Durotar, 48, 85);
        Assert.Equal("The Barrens", Guess(southDurotar)?.Name);

        Assert.True(ForeverRouteWriter.TryWrite("South", Shapes(southDurotar), _ => durotar, Zones, Kalimdor, out string named, out string error), error);
        Assert.True(ForeverRouteWriter.TryWrite("Wrong", Shapes(southDurotar), _ => tanaris, Zones, Kalimdor, out string guessed, out error), error);

        ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(Assert.Single(Load(named).Tasks));
        Assert.Equal(new ForeverMapPoint(Durotar, 48, 85), travel.To);
        // A zone whose map does not hold the point is not used: the maps' guess is.
        Assert.Equal(Barrens, Assert.IsType<ForeverTravelTask>(Assert.Single(Load(guessed).Tasks)).To.UIMapId);
    }

    [Fact]
    public void Required_nodes_end_travels_and_shape_nodes_are_passed_through()
    {
        RouteDesign design = new([new RouteNode(On(Durotar, 40, 40)), new RouteNode(On(Durotar, 42, 42), NodeKind.Required),
            new RouteNode(On(Durotar, 44, 44)), new RouteNode(On(Durotar, 46, 46))]);

        ForeverRoute route = Load(Write("Pinned", design));

        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverTravelTask { To: { X: 42, Y: 42 }, Via.Count: 1 }),
            task => Assert.True(task is ForeverTravelTask { To: { X: 46, Y: 46 }, Via.Count: 1 }));
    }

    [Fact]
    public void An_area_is_roamed_where_it_stands_the_way_drawn_to_it_travelled_first()
    {
        RouteDesign design = new([new RouteNode(On(Durotar, 30, 30)), new RouteNode(On(Durotar, 50, 50)),
            Area(7, On(Durotar, 51, 51), On(Durotar, 55, 51), On(Durotar, 53, 55)), new RouteNode(On(Durotar, 70, 70))]);

        ForeverRoute route = Load(Write("Roam on the way", design));

        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverTravelTask { Via.Count: 2 } travel && travel.To.X > 52 && travel.To.X < 54, task.Name),
            task => Assert.True(task is ForeverRoamAreaTask { Until.Kills: 7, Area.Corners.Count: 3 }),
            task => Assert.True(task is ForeverTravelTask { To: { X: 70, Y: 70 }, Via.Count: 0 }));
    }

    [Fact]
    public void A_rectangle_is_two_opposite_corners_and_areas_alone_make_a_route()
    {
        ForeverRoute route = Load(Write("Just roam", new RouteDesign([Area(3, On(Durotar, 51.5f, 44), On(Durotar, 53.5f, 46))])));

        ForeverRoamAreaTask roam = Assert.IsType<ForeverRoamAreaTask>(Assert.Single(route.Tasks));
        Assert.Equal(4, roam.Area.Corners.Count);
    }

    [Fact]
    public void A_stop_visits_its_npc_or_the_nearest_with_its_role()
    {
        RouteDesign design = new([new RouteNode(On(Durotar, 50, 40)), new RouteNode(On(Durotar, 52, 42), NodeKind.Stop, Stop: new StopTarget(Npc: "Innkeeper Grosk")),
            new RouteNode(On(Durotar, 53, 43), NodeKind.Stop, Stop: new StopTarget(Role: "vendor"))]);

        ForeverRoute route = Load(Write("Errands", design));

        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverTravelTask { To: { X: 52, Y: 42 }, Via.Count: 1 }),
            task => Assert.Equal(ForeverVisitTarget.Npc("Innkeeper Grosk"), Assert.IsType<ForeverVisitTask>(task).Target),
            task => Assert.Equal(ForeverVisitTarget.Nearest("vendor"), Assert.IsType<ForeverVisitTask>(task).Target));
    }

    [Fact]
    public void A_quest_stop_visits_where_the_quest_is_given_or_handed_in()
    {
        RouteDesign design = new([new RouteNode(On(Durotar, 52, 42), NodeKind.Stop, Stop: new StopTarget(QuestStart: 5441)),
            new RouteNode(On(Durotar, 53, 43), NodeKind.Stop, Stop: new StopTarget(QuestEnd: 5441))]);

        ForeverRoute route = Load(Write("Quest", design));

        Assert.Equal([ForeverVisitTarget.QuestStart(5441), ForeverVisitTarget.QuestEnd(5441)],
            route.Tasks.Select(t => Assert.IsType<ForeverVisitTask>(t).Target));
    }

    [Fact]
    public void An_exact_leg_is_walked_as_drawn_and_a_replanned_one_is_only_its_ends()
    {
        Point3 from = On(Durotar, 40, 40);
        Point3 to = On(Durotar, 44, 40);
        float yards = RouteGeometry.Distance(from, to);

        ForeverRoute exact = Load(Write("Exact", new RouteDesign([new RouteNode(from, Leg: LegKind.Exact), new RouteNode(to)])));
        ForeverRoute replanned = Load(Write("Replanned", new RouteDesign([new RouteNode(from), new RouteNode(to)])));

        ForeverTravelTask line = Assert.IsType<ForeverTravelTask>(Assert.Single(exact.Tasks));
        int pieces = (int)System.MathF.Ceiling(yards / RoutePoints.ExactSpacingYards);
        Assert.Equal(pieces, line.Via.Count);
        Assert.All(line.Via, v => Assert.Equal(40, v.Y, 0.05));
        Assert.Single(Assert.IsType<ForeverTravelTask>(Assert.Single(replanned.Tasks)).Via);
    }

    [Fact]
    public void A_loop_repeats_back_to_its_first_node_and_the_cost_zones_are_the_runs()
    {
        RouteDesign design = new([new RouteNode(On(Durotar, 40, 40)), new RouteNode(On(Durotar, 45, 45))], Loop: true, CostZones: CostZoneUse.Apply);

        string text = Write("Loop", design);
        ForeverRoute route = Load(text);

        Assert.True(route.Repeat);
        Assert.True(route.ApplyCostZones);
        ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(Assert.Single(route.Tasks));
        Assert.Equal(new ForeverMapPoint(Durotar, 40, 40), travel.To);
        Assert.Contains("\"CostZones\": \"Apply\"", text);
    }

    [Fact]
    public void A_long_travel_wraps_its_via_points_and_still_loads()
    {
        ForeverRoute route = Load(Write("Long", Shapes([.. Enumerable.Range(0, 20).Select(i => On(Durotar, 40 + i, 40 + (i / 2f)))])));

        ForeverTravelTask travel = Assert.IsType<ForeverTravelTask>(Assert.Single(route.Tasks));
        Assert.Equal(19, travel.Via.Count);
    }

    [Fact]
    public void An_area_no_zone_map_holds_is_named_on_the_continents_map()
    {
        int tanaris = Zones.Single(z => z.Name == "Tanaris").UiMapId;
        int continentMap = Zones.Single(z => z.Name == "Kalimdor" && z.MapId == Kalimdor).UiMapId;

        ForeverRoute route = Load(Write("Wide", new RouteDesign([Area(1, On(Durotar, 50, 50), On(tanaris, 50, 50), On(tanaris, 60, 40))])));

        ForeverRoamAreaTask roam = Assert.IsType<ForeverRoamAreaTask>(Assert.Single(route.Tasks));
        Assert.Equal(continentMap, roam.Area.UIMapId);
    }

    [Fact]
    public void What_a_run_could_not_load_is_refused_with_the_node()
    {
        Point3 nowhere = new(20000, 20000, 0);
        IReadOnlyList<(RouteDesign Design, string Why)> refused =
        [
            (Shapes(On(Durotar, 50, 50), nowhere), "Node 2 is in no zone"),
            (new RouteDesign([Area(1, nowhere, new Point3(20010, 20010, 0))]), "Node 1, an area, is not inside one zone"),
            (new RouteDesign([Area(0, On(Durotar, 50, 50), On(Durotar, 52, 52))]), "Node 1 is an area roamed for no kills"),
            (new RouteDesign([new RouteNode(On(Durotar, 50, 50), NodeKind.Stop, Stop: new StopTarget())]), "Node 1 is a stop with nobody to visit"),
            (RouteDesign.Empty, "nothing to save"),
        ];

        foreach ((RouteDesign design, string why) in refused)
        {
            Assert.False(ForeverRouteWriter.TryWrite("Refused", design, Guess, Zones, Kalimdor, out _, out string error));
            Assert.Contains(why, error);
        }
    }
}
