using PPather.Planning;

using System.Numerics;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// A route the search cannot finish is answered as partial, with the part it found and where it
/// stops - never handed over as found. The case is the one that showed it: Durotar to Tanaris,
/// whose straight line holds no way on past southern Dustwallow.
/// </summary>
public sealed class RoutePlannerPartialTests
{
    private static readonly Vector3 Durotar = new(1007.78f, -4446.22f, 11.2022f);
    private static readonly Vector3 Tanaris = new(-7931.2f, -3414.28f, 80.7365f);

    [Fact]
    public void A_route_that_stops_short_is_partial_and_says_where()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();

        RouteOutcome outcome = planner.Find(new RouteQuery(1411, RouteSpace.World, Durotar, Tanaris));

        Assert.Equal(RoutePlanStatus.Partial, outcome.Status);
        Assert.False(outcome.Found);
        Assert.True(Vector3.Distance(outcome.Points[0], Durotar) < RoutePlanner.ArrivalToleranceYd, "it starts where asked");
        Assert.True(Vector3.Distance(outcome.Points[^1], Tanaris) > 1000, "it ends far from Tanaris");
        Assert.Contains("yd short of the destination", outcome.Detail);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void A_route_that_arrives_is_found(CorpusRoute route)
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();

        RouteOutcome outcome = planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To));

        Assert.True(outcome.Found, $"{outcome.Status}: {outcome.Detail}");
        Assert.Null(outcome.Detail);
    }

    public static TheoryData<CorpusRoute> Corpus()
    {
        return TestData.CorpusData();
    }
}
