using PPather.Navmesh;
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

    // Each query brings fresh memory (NavmeshQueryMemory): the floor it was resolved onto, the band
    // that worked, the last stall. A lane's pathfinder also keeps memory for a caller of its own,
    // which a shared planner must never use: the next caller would start on the previous one's
    // floor. Answers alone cannot show it, since the planner gives a start without a height the
    // surface under it before the pathfinder's floor memory could apply (known issue 42), so this
    // reads that memory: every kind of query leaves it as a new pathfinder has it.
    [Fact]
    public void Queries_leave_the_lanes_own_memory_as_they_found_it()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        using RoutePlanner planner = TestData.NewPlanner();
        foreach (CorpusRoute route in TestData.Corpus)
        {
            Assert.Contains(Find(planner, route).Status, Planned);
        }

        CorpusRoute barrens = TestData.Corpus.Single(r => r.Name == "The Barrens search");
        Assert.Contains(planner.Find(new RouteQuery(barrens.UiMap, barrens.Space, barrens.From, barrens.To,
            Reach: RouteReach.Continent)).Status, Planned);

        foreach (string continent in (string[])["Azeroth", "Kalimdor"])
        {
            NavmeshQueryMemory own = planner.LanePathfinder(continent)!.OwnMemory;
            Assert.Null(own.LastResolvedFrom);
            Assert.Equal(0, own.StickyStamp);
            Assert.Equal(0, own.StalledStamp);
        }
    }

    // The statuses of a query the pathfinder answered.
    private static readonly RoutePlanStatus[] Planned = [RoutePlanStatus.Found, RoutePlanStatus.Partial, RoutePlanStatus.NoPath];

    [Fact]
    public void Jitter_applies_to_its_own_query_only()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);

        using RoutePlanner planner = TestData.NewPlanner();
        CorpusRoute route = TestData.Corpus[0];

        Vector3[] plain = Find(planner, route).Points;
        RouteOutcome jittered = planner.Find(new RouteQuery(route.UiMap, route.Space, route.From, route.To,
            new NavmeshQueryTuning(JitterYards: 3f, JitterSeed: 7)));
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
