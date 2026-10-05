using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PPather;
using PPather.Planning;

using SharedLib;

using System;
using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// The gate behind "a route previewed through the service is the route the bot computes":
/// <see cref="RoutePlanner"/> must return, point for point, what the bot's
/// <c>LocalPathingApi</c> gets from <see cref="PPatherService"/> on a disk-only install.
/// Each side starts fresh for each route, because tiles already resident from an earlier
/// route are part of what a search sees.
/// </summary>
public sealed class RoutePlannerParityTests
{
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Planner_matches_the_bots_local_pathing(CorpusRoute route)
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        using PPatherService service = NewService();
        Vector3[] expected = BotRoute(service, route);
        Assert.SkipUnless(service.DiskOnly, "the bot side has game archives (Json/MPQ); parity is defined for disk-only pathing");

        using RoutePlanner planner = TestData.NewPlanner();
        RouteOutcome outcome = planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To));

        // The bot walks what it gets, all the way or part of it; the planner says which, with the same points.
        Assert.Equal(expected.Length > 0, outcome.Points.Length > 0);
        Assert.Equal(expected, outcome.Points);
    }

    public static TheoryData<CorpusRoute> Corpus()
    {
        return TestData.CorpusData();
    }

    private static PPatherService NewService()
    {
        DataConfig config = TestData.RepoConfig();
        return new PPatherService(NullLogger<PPatherService>.Instance, config, new WorldMapAreaDB(config),
            Options.Create(new NavmeshBakeOptions()), Options.Create(new NavmeshQueryOptions()));
    }

    // The calls Core/PPather/LocalPathingApi makes. The start's startIndoors is passed and
    // then reset by the destination's ToWorldZ, so the search always runs with none.
    private static Vector3[] BotRoute(PPatherService service, CorpusRoute route)
    {
        if (route.Space == RouteSpace.World)
        {
            service.SetLocations(
                service.ToWorldZ(route.UiMap, route.From.X, route.From.Y, route.From.Z, null),
                service.ToWorldZ(route.UiMap, route.To.X, route.To.Y, route.To.Z));
        }
        else
        {
            service.SetLocations(
                service.ToWorld(route.UiMap, route.From.X, route.From.Y, route.From.Z),
                service.ToWorld(route.UiMap, route.To.X, route.To.Y));
        }

        PPather.Graph.Path? path = service.DoSearch(SearchStrategy.A_Star_With_Model_Avoidance);
        if (path is null)
        {
            return [];
        }

        Vector3[] points = path.locations.ToArray();
        if (route.Space == RouteSpace.Map)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = service.ToLocal(points[i], (int)service.SearchFrom.W, route.UiMap);
            }
        }

        return points;
    }
}
