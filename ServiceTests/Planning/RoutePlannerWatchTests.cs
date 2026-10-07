using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>What the planner tells its watchers, and lanes that see the tiles on disk as they change.</summary>
public sealed class RoutePlannerWatchTests
{
    // Dun Morogh 1 of the corpus: tiles apart on both axes.
    private static readonly Vector3 DunMoroghFrom = new(-6238.128f, 139.40344f, 430.9192f);
    private static readonly Vector3 DunMoroghTo = new(-5884.185f, -118.66675f, 364.64783f);

    [Fact]
    public void Routes_are_reported_in_world_yards()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();
        List<PlannedRoute> planned = [];
        planner.RoutePlanned += planned.Add;

        RouteOutcome outcome = planner.Find(new RouteQuery(1426, RouteSpace.Map, new(28.7f, 70.1f, 0), new(28.8f, 67.9f, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(RoutePlanStatus.Found, outcome.Status);
        PlannedRoute route = Assert.Single(planned);
        Assert.Equal("Azeroth", route.Continent);
        Assert.Equal(outcome.Points.Length, route.Points.Length);
        // The answer is in the zone's map coordinates (0 to 100); what watchers get is world yards.
        Assert.All(outcome.Points, p => Assert.InRange(p.X, 0f, 100f));
        Assert.All(route.Points, p => Assert.True(MathF.Abs(p.X) > 1000f, $"{p} is not a Dun Morogh world point"));
        Assert.NotEmpty(planner.ResidentTileSet("Azeroth"));
        Assert.Empty(planner.ResidentTileSet("Kalimdor"));
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

    // RoutePlanned is every answer of a query that reached a continent: the Live view shows a route
    // that waited in vain as well, and says why.
    [Fact]
    public void A_busy_answer_is_reported_too()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        DataConfig config = TestData.RepoConfig();
        using RoutePlanner planner = new(NullLogger.Instance, config, new WorldMapAreaDB(config), new NavmeshBakeOptions(),
            new NavmeshQueryOptions(), laneWait: TimeSpan.FromMilliseconds(50));
        RouteQuery query = new(1426, RouteSpace.World, DunMoroghFrom, DunMoroghTo);
        Assert.Equal(RoutePlanStatus.Found, planner.Find(query, TestContext.Current.CancellationToken).Status);
        List<PlannedRoute> planned = [];
        planner.RoutePlanned += planned.Add;

        SemaphoreSlim gate = planner.LaneGate("Azeroth")!;
        gate.Wait(TestContext.Current.CancellationToken);
        RouteOutcome busy;
        RouteOutcome busyAcross;
        try
        {
            busy = planner.Find(query, TestContext.Current.CancellationToken);
            busyAcross = planner.Find(query with { Reach = RouteReach.Continent }, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(RoutePlanStatus.Busy, busy.Status);
        Assert.Equal(RoutePlanStatus.Busy, busyAcross.Status);
        Assert.Equal(2, planned.Count);
        Assert.All(planned, p =>
        {
            Assert.Equal(RoutePlanStatus.Busy, p.Status);
            Assert.Equal("Azeroth", p.Continent);
            Assert.Contains("busy", p.Detail);
        });
    }

    // docs/known-issues.md items 12, 38 and 52: a lane that did not find a tile looks for it again a
    // moment later, so tiles a bake, a download or a copy writes under a lane that already looked
    // count without a restart. Without the tiles across the way the pathfinder still answers, with a
    // route retargeted short of the destination, so arriving is what is checked.
    [Fact]
    public void Tiles_that_appear_after_a_lane_looked_count_a_moment_later()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using TempDataRoot data = new(withMapData: true);
        NavmeshBakeOptions bake = new();
        string target = NavmeshSettings.CacheDirectory(data.Config(), bake, "Azeroth");
        List<string> withheld = CopyTilesAround(DunMoroghFrom, DunMoroghTo, target, withholdWall: true);

        using RoutePlanner planner = new(NullLogger.Instance, data.Config(), new WorldMapAreaDB(data.Config()), bake,
            new NavmeshQueryOptions());
        RouteQuery query = new(1426, RouteSpace.World, DunMoroghFrom, DunMoroghTo);

        RouteOutcome walled = planner.Find(query, TestContext.Current.CancellationToken);
        foreach (string file in withheld)
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        Thread.Sleep(NavmeshTileCache.MissingTileRecheck + TimeSpan.FromMilliseconds(100));
        RouteOutcome later = planner.Find(query, TestContext.Current.CancellationToken);

        Assert.False(Arrives(walled, DunMoroghTo), "the wall should keep the route from arriving");
        Assert.True(Arrives(later, DunMoroghTo), "the lane should see the tiles that appeared");
    }

    // A lane holds the tiles it read: deleted or replaced on disk, they stay in memory until the lane
    // is told (BakeAdmin does, after a clear and after a bake).
    [Fact]
    public void A_lane_told_that_tiles_changed_lets_go_of_them_and_reads_the_disk_again()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using TempDataRoot data = new(withMapData: true);
        NavmeshBakeOptions bake = new();
        string target = NavmeshSettings.CacheDirectory(data.Config(), bake, "Azeroth");
        CopyTilesAround(DunMoroghFrom, DunMoroghTo, target, withholdWall: false);

        using RoutePlanner planner = new(NullLogger.Instance, data.Config(), new WorldMapAreaDB(data.Config()), bake,
            new NavmeshQueryOptions());
        RouteQuery query = new(1426, RouteSpace.World, DunMoroghFrom, DunMoroghTo);

        RouteOutcome before = planner.Find(query, TestContext.Current.CancellationToken);
        Assert.NotEmpty(planner.ResidentTileSet("Azeroth"));

        string[] tiles = Directory.GetFiles(target, "*.dnm");
        string aside = Path.Combine(data.Root, "aside");
        Directory.CreateDirectory(aside);
        foreach (string tile in tiles)
        {
            File.Move(tile, Path.Combine(aside, Path.GetFileName(tile)));
        }

        planner.TilesChanged("Azeroth");
        IReadOnlySet<(int X, int Z)> heldAfter = planner.ResidentTileSet("Azeroth");
        RouteOutcome gone = planner.Find(query, TestContext.Current.CancellationToken);

        foreach (string tile in tiles)
        {
            File.Move(Path.Combine(aside, Path.GetFileName(tile)), tile);
        }

        RouteOutcome back = planner.Find(query, TestContext.Current.CancellationToken);

        Assert.True(Arrives(before, DunMoroghTo));
        Assert.Empty(heldAfter);
        Assert.Equal(RoutePlanStatus.NotBaked, gone.Status);
        Assert.True(Arrives(back, DunMoroghTo), "the lane should read the tiles again");
    }

    // Copies the repository's tiles in a box around the route into target. With withholdWall, a row
    // of tiles across the axis the route crosses most is left out, and returned: no way round.
    private static List<string> CopyTilesAround(Vector3 from, Vector3 to, string target, bool withholdWall)
    {
        string source = NavmeshSettings.CacheDirectory(TestData.RepoConfig(), new NavmeshBakeOptions(), "Azeroth");
        Directory.CreateDirectory(target);
        NavmeshCoords.GetTileIndex(from.X, from.Y, out int fromTileX, out int fromTileZ);
        NavmeshCoords.GetTileIndex(to.X, to.Y, out int toTileX, out int toTileZ);

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

                if (withholdWall && (wallAcrossX ? x : z) == wall)
                {
                    withheld.Add(file);
                }
                else
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
            }
        }

        return withheld;
    }

    private const float ArrivalYards = 5f;

    private static bool Arrives(RouteOutcome outcome, Vector3 destination)
    {
        return outcome.Found &&
               Vector2.Distance(new Vector2(outcome.Points[^1].X, outcome.Points[^1].Y),
                   new Vector2(destination.X, destination.Y)) <= ArrivalYards;
    }
}
