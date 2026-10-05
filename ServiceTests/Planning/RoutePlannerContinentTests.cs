using PPather.Navmesh;
using PPather.Planning;

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// Routes across the whole continent: the way is found on the continent's tile graph, then searched
/// leg by leg along it. The first such route builds the graph from the tiles (about 10 s for
/// Kalimdor) and keeps it beside them, as the service does; later ones read it.
/// </summary>
public sealed class RoutePlannerContinentTests
{
    private static readonly Vector3 Durotar = new(1007.78f, -4446.22f, 11.2022f);
    private static readonly Vector3 Tanaris = new(-7931.2f, -3414.28f, 80.7365f);

    [Fact]
    public void A_route_the_band_cannot_finish_reaches_its_destination_across_the_continent()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();

        RouteOutcome band = planner.Find(new RouteQuery(1411, RouteSpace.World, Durotar, Tanaris));
        RouteOutcome across = planner.Find(new RouteQuery(1411, RouteSpace.World, Durotar, Tanaris, Reach: RouteReach.Continent));

        Assert.Equal(RoutePlanStatus.Partial, band.Status);
        Assert.True(across.Found, $"{across.Status}: {across.Detail}");
        Assert.True(Vector3.Distance(across.Points[0], Durotar) < RoutePlanner.ArrivalToleranceYd, "it starts where asked");
        Assert.True(Vector2.Distance(new(across.Points[^1].X, across.Points[^1].Y), new(Tanaris.X, Tanaris.Y)) < 50, "it ends at Tanaris");
    }

    [Fact]
    public void A_short_route_across_the_continent_is_the_band_route()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();
        CorpusRoute route = TestData.Corpus.First(r => r.Name == "Durotar Razor Hill to mining");

        RouteOutcome across = planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To, Reach: RouteReach.Continent));

        Assert.True(across.Found, $"{across.Status}: {across.Detail}");
    }

    [Fact]
    public void Legs_keep_both_ends_and_every_turn_of_the_way()
    {
        List<Vector3> way = [new(0, 0, 0), new(100, 0, 0), new(200, 0, 0), new(200, 400, 0), new(200, 800, 0)];

        List<Vector3> legs = RoutePlanner.Legs(way);

        Vector3[] corners = [new(0, 0, 0), new(200, 0, 0), new(200, 800, 0)];
        Assert.Equal(corners, legs);
    }

    [Fact]
    public void A_long_straight_way_is_cut_into_legs_no_longer_than_the_limit()
    {
        List<Vector3> way = [.. Enumerable.Range(0, 51).Select(i => new Vector3(i * 100, 0, 0))];

        List<Vector3> legs = RoutePlanner.Legs(way);

        Assert.Equal(way[0], legs[0]);
        Assert.Equal(way[^1], legs[^1]);
        for (int i = 1; i < legs.Count; i++)
        {
            Assert.True(Vector3.Distance(legs[i - 1], legs[i]) <= RoutePlanner.MaxLegYd, $"leg {i} is {Vector3.Distance(legs[i - 1], legs[i]):F0} yd");
        }
    }

    [Fact]
    public void A_way_of_two_points_is_one_leg()
    {
        List<Vector3> legs = RoutePlanner.Legs([new(0, 0, 0), new(5000, 0, 0)]);

        Assert.Equal(2, legs.Count);
    }
}
