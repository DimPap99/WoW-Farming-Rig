using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;
using ServiceClients.Routes;

using SharedLib;

using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>
/// A Forever route file opened as nodes: what the designer wrote comes back as the design it was - node
/// kinds, exact legs, the loop, the cost-zone choice - and what a design cannot hold is named.
/// </summary>
public sealed class ForeverRouteReaderTests
{
    private const int Durotar = 1411;
    private const int Kalimdor = 1;

    private static readonly WorldMapAreaDB Maps = new(new DataConfig { Root = TestData.RepoJson, Exp = "forever" });

    private static readonly ZoneInfo[] Zones = [.. Maps.Values.Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName, a.Continent,
        a.MapID, a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];

    private static readonly Point3 Grosk = On(52.6f, 41.3f);

    private static Point3 On(float x, float y)
    {
        return ZoneMath.ToWorld(Zones.Single(z => z.UiMapId == Durotar), new Point3(x, y, 0));
    }

    private static RouteDesign Read(string text, out List<string> left)
    {
        Assert.True(ForeverRouteSketch.TryRead(text, out ForeverSketch sketch, out string error), error);
        return ForeverRouteReader.ToDesign(sketch, Zones, npc => npc == "Innkeeper Grosk" ? Grosk : null, out left);
    }

    [Fact]
    public void A_designed_route_comes_back_as_it_was_designed()
    {
        Point3[] corners = [On(50, 50), On(53, 50), On(51.5f, 53)];
        RouteDesign designed = new(
        [
            new RouteNode(On(40, 40), Leg: LegKind.Exact),
            new RouteNode(On(44, 40)),
            new RouteNode(On(46, 44), NodeKind.Required, LegKind.Exact),
            new RouteNode(On(48, 46)),
            new RouteNode(RouteGeometry.Middle(corners), NodeKind.Area, Area: corners, Kills: 9),
            new RouteNode(Grosk, NodeKind.Stop, Stop: new StopTarget(Npc: "Innkeeper Grosk")),
        ], Loop: true, CostZones: CostZoneUse.Apply);
        Assert.True(ForeverRouteWriter.TryWrite("Round trip", designed, p => ZoneMath.ZoneAt(Zones, Kalimdor, p.X, p.Y), Zones, Kalimdor,
            out string text, out string error), error);

        RouteDesign read = Read(text, out List<string> left);

        Assert.Empty(left);
        Assert.True(read.Loop);
        Assert.Equal(CostZoneUse.Apply, read.CostZones);
        Assert.Equal(designed.Nodes.Select(n => n.Kind), read.Nodes.Select(n => n.Kind));
        Assert.Equal(designed.Nodes.Select(n => n.Leg), read.Nodes.Select(n => n.Leg));
        Assert.All(designed.Nodes.Zip(read.Nodes), pair => Assert.True(RouteGeometry.Distance(pair.First.At, pair.Second.At) < 1,
            $"{pair.First.At} came back at {pair.Second.At}"));
        Assert.Equal(9, read.Nodes[4].Kills);
        Assert.Equal(3, read.Nodes[4].Area!.Length);
        Assert.Equal(new StopTarget(Npc: "Innkeeper Grosk"), read.Nodes[5].Stop);
    }

    [Fact]
    public void Exact_legs_by_an_area_a_quest_stop_and_a_loops_last_leg_come_back_as_they_were()
    {
        Point3[] corners = [On(50, 50), On(53, 50), On(51.5f, 53)];
        RouteDesign designed = new(
        [
            new RouteNode(On(40, 40), Leg: LegKind.Exact),
            new RouteNode(RouteGeometry.Middle(corners), NodeKind.Area, LegKind.Exact, Area: corners, Kills: 5),
            new RouteNode(On(56, 46), Leg: LegKind.Exact),
            new RouteNode(On(58, 44), NodeKind.Stop, LegKind.Exact, new StopTarget(QuestStart: 5441)),
        ], Loop: true);

        RouteDesign read = RoundTrip(designed, out List<string> left);

        Assert.Empty(left);
        Assert.True(read.Loop);
        Assert.Equal(designed.Nodes.Select(n => (n.Kind, n.Leg)), read.Nodes.Select(n => (n.Kind, n.Leg)));
        Assert.Equal(new StopTarget(QuestStart: 5441), read.Nodes[3].Stop);
    }

    [Fact]
    public void A_line_in_a_wide_zone_survives_the_files_rounding()
    {
        // The Barrens' map is over 10,000 yards across: a hundredth of its map is about a yard.
        ZoneInfo barrens = Zones.Single(z => z.UiMapId == 1413);
        Point3 Barrens(float x, float y) => ZoneMath.ToWorld(barrens, new Point3(x, y, 0));
        RouteDesign designed = new([new RouteNode(Barrens(40.3f, 30.7f), Leg: LegKind.Exact), new RouteNode(Barrens(47.1f, 33.9f), Leg: LegKind.Exact),
            new RouteNode(Barrens(52.2f, 40.4f))]);

        RouteDesign read = RoundTrip(designed, out List<string> left);

        Assert.Empty(left);
        Assert.Equal([LegKind.Exact, LegKind.Exact, LegKind.Replanned], read.Nodes.Select(n => n.Leg));
        Assert.All(designed.Nodes.Zip(read.Nodes), pair => Assert.True(RouteGeometry.Distance(pair.First.At, pair.Second.At) < 1.5f));
    }

    private static RouteDesign RoundTrip(RouteDesign designed, out List<string> left)
    {
        Assert.True(ForeverRouteWriter.TryWrite("Round trip", designed, p => ZoneMath.ZoneAt(Zones, Kalimdor, p.X, p.Y), Zones, Kalimdor,
            out string text, out string error), error);
        return Read(text, out left);
    }

    [Fact]
    public void The_shipped_example_opens_as_required_nodes_around_an_area()
    {
        string example = File.ReadAllText(Path.Combine(TestData.RepoJson, "path", "_forever", "durotar_example.json"));

        RouteDesign read = Read(example, out List<string> left);

        // Its roam ends after 3 kills or 10 minutes; a design's area knows kills only.
        Assert.Equal(["step 2's Minutes"], left);
        Assert.Equal([NodeKind.Required, NodeKind.Area, NodeKind.Required], read.Nodes.Select(n => n.Kind));
        Assert.Equal(3, read.Nodes[1].Kills);
        Assert.Equal(CostZoneUse.Ignore, read.CostZones);
    }

    [Fact]
    public void What_a_design_cannot_hold_is_named_by_its_step()
    {
        const string text = """
            { "UIMapId": 1411, "Steps": [
              { "Travel": { "To": [50, 50], "Pulls": "OnTheWay" } }, { "Visit": { "Role": "vendor" } }, { "Visit": { "Npc": "Nobody Here" } },
              { "Visit": { "QuestStart": 5441 } }, { "Quest": { "Id": 5441, "Until": { "Kills": 8 } } } ] }
            """;

        RouteDesign read = Read(text, out List<string> left);

        // A visit found when its step begins stands where the route has come to.
        Assert.Equal([NodeKind.Required, NodeKind.Stop, NodeKind.Stop], read.Nodes.Select(n => n.Kind));
        Assert.All(read.Nodes, n => Assert.Equal(read.Nodes[0].At, n.At));
        Assert.Equal([null, new StopTarget(Role: "vendor"), new StopTarget(QuestStart: 5441)], read.Nodes.Select(n => n.Stop));
        Assert.Collection(left,
            l => Assert.Equal("step 1's Pulls", l),
            l => Assert.Equal("step 3, a visit to Nobody Here, who is not found on the route's continent", l),
            l => Assert.Equal("step 5, a quest's camp, roamed", l));
    }
}
