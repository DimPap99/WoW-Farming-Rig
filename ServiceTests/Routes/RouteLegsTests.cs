using Contracts;
using Contracts.Pathing;

using ServiceClients.Routes;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>
/// The route designer's legs: one request per pair of waypoints, kept by their ends, so moving a
/// waypoint plans only its two legs again, and a few requests at a time.
/// </summary>
public sealed class RouteLegsTests
{
    private static readonly Point3 A = new(100, 100, 0);
    private static readonly Point3 B = new(200, 100, 0);
    private static readonly Point3 C = new(300, 150, 0);
    private static readonly Point3 D = new(400, 150, 0);

    /// <summary>Answers each request with a straight route, or holds it until released.</summary>
    private sealed class FakePlanner
    {
        public ConcurrentQueue<RouteRequest> Asked { get; } = new();

        public TaskCompletionSource? Hold { get; set; }

        public int InFlight;

        public int MostInFlight;

        public async Task<RouteResponse> FindAsync(RouteRequest request, CancellationToken token)
        {
            Asked.Enqueue(request);
            int now = Interlocked.Increment(ref InFlight);
            InterlockedMax(ref MostInFlight, now);
            try
            {
                if (Hold is TaskCompletionSource hold)
                {
                    await hold.Task.WaitAsync(token);
                }

                return new RouteResponse(RouteStatus.Found, [request.From, request.To], true, 1, null);
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    private static async Task SettleAsync(RouteLegs legs)
    {
        for (int i = 0; i < 200 && legs.Planning > 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, legs.Planning);
    }

    [Fact]
    public async Task Each_pair_of_waypoints_is_one_request_and_a_moved_waypoint_plans_only_its_legs()
    {
        FakePlanner planner = new();
        int changed = 0;
        RouteLegs legs = new(planner.FindAsync, () => Interlocked.Increment(ref changed));

        legs.Plan([A, B, C, D], RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        await SettleAsync(legs);

        Assert.Equal(3, planner.Asked.Count);
        Assert.Equal(3, changed);
        Assert.True(legs.Get(B, C, RouteSearch.Band).Found);
        Assert.Equal(100, legs.Get(A, B, RouteSearch.Band).Length);

        Point3 moved = C with { Y = 180 };
        legs.Plan([A, B, moved, D], RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        await SettleAsync(legs);

        Assert.Equal(5, planner.Asked.Count);
        Assert.Equal([(B, moved), (moved, D)], planner.Asked.Skip(3).Select(r => (r.From, r.To)));
    }

    [Fact]
    public async Task A_waypoint_sent_back_to_a_tenth_of_a_yard_and_with_a_height_keeps_its_legs()
    {
        FakePlanner planner = new();
        RouteLegs legs = new(planner.FindAsync, () => { });
        Point3 exact = new(250.04f, 120.96f, 0);

        legs.Plan([A, exact], RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        await SettleAsync(legs);
        legs.Plan([A, new Point3(250.0f, 121.0f, 33.5f)], RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        await SettleAsync(legs);

        Assert.Single(planner.Asked);
        Assert.True(legs.Get(A, new Point3(250.0f, 121.0f, 33.5f), RouteSearch.Band).Found);
        // The other search is another leg.
        Assert.Null(legs.Get(A, exact, RouteSearch.Continent).Status);
    }

    [Fact]
    public async Task No_more_than_a_few_requests_run_at_once()
    {
        FakePlanner planner = new() { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        RouteLegs legs = new(planner.FindAsync, () => { });
        Point3[] waypoints = [.. Enumerable.Range(0, 12).Select(i => new Point3(i * 50, 0, 0))];

        legs.Plan(waypoints, RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(RouteLegs.Concurrent, planner.InFlight);
        Assert.Equal(11, legs.Planning);

        planner.Hold.SetResult();
        await SettleAsync(legs);

        Assert.Equal(RouteLegs.Concurrent, planner.MostInFlight);
        Assert.Equal(11, planner.Asked.Count);
    }

    [Fact]
    public async Task Answers_that_arrive_after_a_clear_are_dropped()
    {
        FakePlanner planner = new() { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        int changed = 0;
        RouteLegs legs = new(planner.FindAsync, () => Interlocked.Increment(ref changed));

        legs.Plan([A, B], RouteSearch.Band, _ => 1411, TestContext.Current.CancellationToken);
        legs.Clear();
        planner.Hold.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Null(legs.Get(A, B, RouteSearch.Band).Status);
        Assert.Equal(0, changed);
        Assert.Equal(0, legs.Planning);
    }

    [Fact]
    public void Each_request_names_the_zone_of_its_start_in_world_yards()
    {
        FakePlanner planner = new();
        RouteLegs legs = new(planner.FindAsync, () => { });
        Dictionary<Point3, int> zones = new() { [A] = 1411, [B] = 1413 };

        legs.Plan([A, B, C], RouteSearch.Continent, at => zones.GetValueOrDefault(at), TestContext.Current.CancellationToken);

        Assert.Equal([1411, 1413], planner.Asked.Select(r => r.UiMapId));
        Assert.All(planner.Asked, r => Assert.Equal(CoordinateSpace.World, r.Space));
        Assert.All(planner.Asked, r => Assert.Equal(RouteSearch.Continent, r.Search));
    }
}
