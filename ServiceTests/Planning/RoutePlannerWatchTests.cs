using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>What the planner tells its watchers, and lanes that see tiles which appear after they looked.</summary>
public sealed class RoutePlannerWatchTests
{
    [Fact]
    public void Routes_are_reported_in_world_yards_with_the_tiles_a_lane_loads()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();
        List<PlannedRoute> planned = [];
        ConcurrentBag<LaneTile> loaded = [];
        planner.RoutePlanned += planned.Add;
        planner.TileLoaded += loaded.Add;

        RouteOutcome outcome = planner.Find(new RouteQuery(1426, RouteSpace.Map, new(28.7f, 70.1f, 0), new(28.8f, 67.9f, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(RoutePlanStatus.Found, outcome.Status);
        PlannedRoute route = Assert.Single(planned);
        Assert.Equal("Azeroth", route.Continent);
        Assert.Equal(outcome.Points.Length, route.Points.Length);
        // The answer is in the zone's map coordinates (0 to 100); what watchers get is world yards.
        Assert.All(outcome.Points, p => Assert.InRange(p.X, 0f, 100f));
        Assert.All(route.Points, p => Assert.True(MathF.Abs(p.X) > 1000f, $"{p} is not a Dun Morogh world point"));
        Assert.NotEmpty(loaded);
        Assert.All(loaded, t => Assert.NotNull(t.Data));

        IReadOnlyList<LaneTile> resident = planner.ResidentTiles("Azeroth");
        Assert.NotEmpty(resident);
        Assert.All(resident, t => Assert.Contains((t.X, t.Z), planner.ResidentTileSet("Azeroth")));
        Assert.Empty(planner.ResidentTiles("Kalimdor"));
    }

    [Fact]
    public void A_route_on_an_unbaked_continent_is_reported_too()
    {
        using TempDataRoot data = new(withMapData: true);
        using RoutePlanner planner = new(NullLogger.Instance, data.Config(), new WorldMapAreaDB(data.Config()),
            new NavmeshBakeOptions(), new NavmeshQueryOptions());
        List<PlannedRoute> planned = [];
        planner.RoutePlanned += planned.Add;

        RouteOutcome outcome = planner.Find(new RouteQuery(1429, RouteSpace.World, new(-8898f, -117f, 0), new(-8779f, -106f, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(RoutePlanStatus.NotBaked, outcome.Status);
        Assert.Equal(RoutePlanStatus.NotBaked, Assert.Single(planned).Status);
    }

    // docs/known-issues.md item 12: a disk-only lane records a tile that is not on disk as empty
    // for its life. A bake writes tiles under a lane that already looked; closing the lane is
    // what makes them count. Without the tiles across the way the pathfinder still answers, with
    // a route retargeted short of the destination, so arriving is what is checked.
    [Fact]
    public void Tiles_that_appear_after_a_lane_looked_count_once_the_lane_is_closed()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using TempDataRoot data = new(withMapData: true);
        NavmeshBakeOptions bake = new();
        string source = NavmeshSettings.CacheDirectory(TestData.RepoConfig(), bake, "Azeroth");
        string target = NavmeshSettings.CacheDirectory(data.Config(), bake, "Azeroth");
        Directory.CreateDirectory(target);

        // Dun Morogh 1 of the corpus: tiles apart on both axes.
        Vector3 from = new(-6238.128f, 139.40344f, 430.9192f);
        Vector3 to = new(-5884.185f, -118.66675f, 364.64783f);
        NavmeshCoords.GetTileIndex(from.X, from.Y, out int fromTileX, out int fromTileZ);
        NavmeshCoords.GetTileIndex(to.X, to.Y, out int toTileX, out int toTileZ);

        // A wall of withheld tiles across the axis the route crosses most, inside a box of tiles
        // around the route: no way round.
        bool wallAcrossX = Math.Abs(fromTileX - toTileX) >= Math.Abs(fromTileZ - toTileZ);
        int wall = wallAcrossX ? (fromTileX + toTileX) / 2 : (fromTileZ + toTileZ) / 2;
        Assert.True(Math.Abs(wallAcrossX ? fromTileX - toTileX : fromTileZ - toTileZ) >= 2,
            "the route must cross a whole row of tiles");

        List<string> withheld = [];
        for (int x = Math.Min(fromTileX, toTileX) - 2; x <= Math.Max(fromTileX, toTileX) + 2; x++)
        {
            for (int z = Math.Min(fromTileZ, toTileZ) - 2; z <= Math.Max(fromTileZ, toTileZ) + 2; z++)
            {
                string file = Path.Combine(source, $"tile_{x}_{z}.dnm");
                if (!File.Exists(file))
                {
                    continue;
                }

                if ((wallAcrossX ? x : z) == wall)
                {
                    withheld.Add(file);
                }
                else
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
            }
        }

        using RoutePlanner planner = new(NullLogger.Instance, data.Config(), new WorldMapAreaDB(data.Config()), bake,
            new NavmeshQueryOptions());
        RouteQuery query = new(1426, RouteSpace.World, from, to);

        RouteOutcome walled = planner.Find(query, TestContext.Current.CancellationToken);
        foreach (string file in withheld)
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        RouteOutcome remembered = planner.Find(query, TestContext.Current.CancellationToken);
        planner.CloseLanes("Azeroth");
        RouteOutcome reopened = planner.Find(query, TestContext.Current.CancellationToken);

        Assert.False(Arrives(walled, to), "the wall should keep the route from arriving");
        Assert.False(Arrives(remembered, to), "the open lane should still remember the wall as empty");
        Assert.True(Arrives(reopened, to), "the reopened lane should see the tiles");
    }

    private const float ArrivalYards = 5f;

    private static bool Arrives(RouteOutcome outcome, Vector3 destination)
    {
        return outcome.Found &&
               Vector2.Distance(new Vector2(outcome.Points[^1].X, outcome.Points[^1].Y),
                   new Vector2(destination.X, destination.Y)) <= ArrivalYards;
    }
}
