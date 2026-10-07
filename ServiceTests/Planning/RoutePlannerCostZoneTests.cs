using PPather.Navmesh;
using PPather.Planning;

using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// A query may bring its own cost zones (<see cref="RouteQuery.Zones"/>): none, as a Forever run whose
/// route does not apply them walks, prices that query alone, and the lane keeps the continent's for the
/// next one.
/// </summary>
public sealed class RoutePlannerCostZoneTests
{
    // Goldshire to Eastvale Logging Camp: Elwynn's road runs between them, and its roads are authored.
    private static readonly RouteQuery Elwynn = new(1429, RouteSpace.World, new Vector3(-9464f, 62f, 0), new Vector3(-9467f, -1276f, 0));

    [Theory]
    [InlineData(RouteReach.Band)]
    [InlineData(RouteReach.Continent)]
    public void A_query_without_zones_plans_as_a_lane_with_none_and_leaves_the_lanes_in_place(RouteReach reach)
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();
        RouteQuery query = Elwynn with { Reach = reach };

        RouteOutcome applied = planner.Find(query);
        NavmeshPathfinder lane = planner.LanePathfinder("Azeroth")!;
        CostZones authored = lane.Zones;
        Assert.False(authored.IsEmpty);

        RouteOutcome ignored = planner.Find(query with { Zones = CostZones.Empty });
        Assert.Same(authored, lane.Zones);

        // The same query on a lane that has no zones at all.
        lane.Zones = CostZones.Empty;
        RouteOutcome none = planner.Find(query);
        lane.Zones = authored;

        Assert.True(applied.Found, applied.Detail);
        Assert.True(ignored.Found, ignored.Detail);
        Assert.Equal(none.Points, ignored.Points);
        Assert.NotEqual(applied.Points, ignored.Points);
        Assert.Equal(applied.Points, planner.Find(query).Points);
    }
}
