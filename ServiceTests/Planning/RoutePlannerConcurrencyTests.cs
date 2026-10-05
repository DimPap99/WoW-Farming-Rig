using PPather.Planning;

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// Several callers on one planner: across continents in parallel, and on one continent in
/// turn. Nothing one query does may change another's answer - neither its tuning nor the
/// floor it was resolved onto.
/// </summary>
public sealed class RoutePlannerConcurrencyTests
{
    private const int Copies = 8;

    [Fact]
    public void Parallel_queries_answer_as_serial_ones()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        using RoutePlanner planner = TestData.NewPlanner();
        IReadOnlyList<CorpusRoute> corpus = TestData.Corpus;

        // Warm every lane first, so the serial and the parallel pass see the same tiles.
        foreach (CorpusRoute route in corpus)
        {
            Find(planner, route);
        }

        Dictionary<CorpusRoute, Vector3[]> serial = corpus.ToDictionary(r => r, r => Find(planner, r).Points);

        CorpusRoute[] work = [.. Enumerable.Range(0, Copies).SelectMany(_ => corpus)];
        RouteOutcome[] parallel = new RouteOutcome[work.Length];
        Parallel.For(0, work.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => parallel[i] = Find(planner, work[i]));

        for (int i = 0; i < work.Length; i++)
        {
            Assert.NotEqual(RoutePlanStatus.Busy, parallel[i].Status);
            Assert.Equal(serial[work[i]], parallel[i].Points);
        }

        Assert.All(planner.Lanes(), lane => Assert.True(lane.Queries >= Copies));
    }

    [Fact]
    public void A_query_does_not_inherit_the_previous_callers_floor()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        // Inside Anvilmar with a height, then from the same spot without one. A shared
        // pathfinder would resolve the second start onto the first one's floor.
        CorpusRoute indoors = new("Anvilmar, height known", 1426, RouteSpace.World,
            new(-6101.417f, 390.9181f, 395.626f), new(-6154.18f, 609.84973f, 395.626f));
        CorpusRoute noHeight = indoors with { Name = "Anvilmar, no height", From = indoors.From with { Z = 0 } };

        Vector3[] alone;
        using (RoutePlanner fresh = TestData.NewPlanner())
        {
            alone = Find(fresh, noHeight).Points;
        }

        using RoutePlanner planner = TestData.NewPlanner();
        Find(planner, indoors);
        Vector3[] afterAnother = Find(planner, noHeight).Points;

        Assert.Equal(alone, afterAnother);
    }

    [Fact]
    public void Jitter_applies_to_its_own_query_only()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        using RoutePlanner planner = TestData.NewPlanner();
        CorpusRoute route = TestData.Corpus[0];

        Vector3[] plain = Find(planner, route).Points;
        RouteOutcome jittered = planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To,
            new PPather.Navmesh.NavmeshQueryTuning(JitterYards: 3f, JitterSeed: 7)));
        Vector3[] plainAgain = Find(planner, route).Points;

        Assert.True(jittered.Found);
        Assert.NotEqual(plain, jittered.Points);
        Assert.Equal(plain, plainAgain);
    }

    private static RouteOutcome Find(RoutePlanner planner, CorpusRoute route)
    {
        return planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To));
    }
}
