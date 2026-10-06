using PPather.Planning;

using SharedLib;

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// A route asked in a zone's map coordinates is answered in that zone's map coordinates, every
/// point of it (known issue 2): a route that strays into the next zone, or into a gap that only
/// the continent's map holds, keeps one frame, and its points turn back into the world yards it
/// was planned in.
/// </summary>
public sealed class RoutePlannerMapTests
{
    [Theory]
    // Eastvale Logging Camp to Lakeshire, east of Elwynn's map, where Redridge's holds the points.
    [InlineData(1429, -9467f, -1276f, -9227f, -2157f)]
    // The Eastern Plaguelands to a cliff on the coast that no zone's map holds, only the
    // continent's (Eastern Kingdoms, 1415), whose own 0 to 100 frame the old conversion used.
    [InlineData(1423, 1476.7f, -6036.7f, 1476.7f, -6136.7f)]
    public void A_map_route_out_of_its_zone_stays_in_the_zone_it_was_asked_in(int uiMap, float fromX, float fromY, float toX, float toY)
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        WorldMapAreaDB maps = new(TestData.RepoConfig());
        Assert.True(maps.TryGet(uiMap, out WorldMapArea zone));

        using RoutePlanner planner = TestData.NewPlanner();
        List<PlannedRoute> planned = [];
        planner.RoutePlanned += planned.Add;

        RouteOutcome outcome = planner.Find(new RouteQuery(uiMap, RouteSpace.Map,
            WorldMapAreaDB.ToMap_FlipXY(new Vector3(fromX, fromY, 0), zone),
            WorldMapAreaDB.ToMap_FlipXY(new Vector3(toX, toY, 0), zone)));

        Assert.True(outcome.Found, outcome.Detail);
        Vector3[] world = planned.Single().Points;
        Assert.Contains(outcome.Points, p => p.X > 100);
        Assert.Equal(world.Select(p => WorldMapAreaDB.ToMap_FlipXY(p, zone)), outcome.Points);
    }
}
