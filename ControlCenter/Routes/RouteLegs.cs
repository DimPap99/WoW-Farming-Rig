using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Routes;

/// <summary>
/// The legs of a designed route: one route request per pair of consecutive waypoints, a few at a
/// time, each kept by its ends (to a tenth of a yard, the precision the map sends) and the search.
/// Moving a waypoint plans only the two legs it ends; the rest are kept.
/// </summary>
public sealed class RouteLegs(Func<RouteRequest, CancellationToken, Task<RouteResponse>> find, Action changed)
{
    /// <summary>Route requests in flight at once.</summary>
    public const int Concurrent = 4;

    private readonly Dictionary<LegKey, Leg> legs = [];
    private readonly HashSet<LegKey> planning = [];
    private readonly SemaphoreSlim gate = new(Concurrent);
    private readonly Lock sync = new();

    /// <summary>Legs asked for and not answered yet.</summary>
    public int Planning
    {
        get
        {
            lock (sync)
            {
                return planning.Count;
            }
        }
    }

    /// <summary>The leg from <paramref name="from"/> to <paramref name="to"/>: planned, or <see cref="Leg.Pending"/>.</summary>
    public Leg Get(Point3 from, Point3 to, RouteSearch search)
    {
        lock (sync)
        {
            return legs.GetValueOrDefault(LegKey.Of(from, to, search), Leg.Pending);
        }
    }

    /// <summary>
    /// Asks for every leg between consecutive <paramref name="waypoints"/> not planned or asked for
    /// yet; <see cref="changed"/> is called as each answer arrives, on the thread it arrives on.
    /// </summary>
    /// <param name="uiMapOf">A zone map of the continent for a leg's start; the request needs one.</param>
    public void Plan(IReadOnlyList<Point3> waypoints, RouteSearch search, Func<Point3, int> uiMapOf, CancellationToken token)
    {
        for (int i = 0; i + 1 < waypoints.Count; i++)
        {
            Point3 from = waypoints[i];
            Point3 to = waypoints[i + 1];
            LegKey key = LegKey.Of(from, to, search);
            lock (sync)
            {
                if (legs.ContainsKey(key) || !planning.Add(key))
                {
                    continue;
                }
            }

            _ = PlanAsync(key, new RouteRequest(null, uiMapOf(from), CoordinateSpace.World, from, to, Search: search), token);
        }
    }

    /// <summary>Forgets every leg, as when the continent changes; answers still on their way are dropped.</summary>
    public void Clear()
    {
        lock (sync)
        {
            legs.Clear();
            planning.Clear();
        }
    }

    private async Task PlanAsync(LegKey key, RouteRequest request, CancellationToken token)
    {
        Leg leg;
        try
        {
            await gate.WaitAsync(token);
            try
            {
                RouteResponse response = await find(request, token);
                leg = new Leg(response.Status, response.Points, response.Detail, response.ElapsedMs);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                planning.Remove(key);
            }

            return;
        }

        lock (sync)
        {
            // Cleared meanwhile: the answer is for a route no longer shown.
            if (!planning.Remove(key))
            {
                return;
            }

            legs[key] = leg;
        }

        changed();
    }

    /// <summary>A leg's ends to a tenth of a yard, and the search that planned it; heights are left out, as the service finds them.</summary>
    private readonly record struct LegKey(float FromX, float FromY, float ToX, float ToY, RouteSearch Search)
    {
        public static LegKey Of(Point3 from, Point3 to, RouteSearch search)
        {
            return new LegKey(Tenth(from.X), Tenth(from.Y), Tenth(to.X), Tenth(to.Y), search);
        }

        private static float Tenth(float value)
        {
            return MathF.Round(value * 10, MidpointRounding.AwayFromZero) / 10;
        }
    }
}

/// <summary>One leg as the service answered it; <see cref="Status"/> is null while it is being planned.</summary>
public sealed record Leg(RouteStatus? Status, Point3[] Points, string? Detail, double ElapsedMs)
{
    public static readonly Leg Pending = new(null, [], null, 0);

    /// <summary>A route all the way.</summary>
    public bool Found => Status == RouteStatus.Found;

    /// <summary>A route part of the way.</summary>
    public bool Partial => Status == RouteStatus.Partial;

    /// <summary>Answered with no route at all.</summary>
    public bool Failed => Status is not null && Status != RouteStatus.Found && Status != RouteStatus.Partial;

    /// <summary>Its length along the ground, in yards; 0 without points.</summary>
    public float Length => RouteGeometry.Length(Points);
}

/// <summary>Lengths along points, across the ground.</summary>
public static class RouteGeometry
{
    public static float Length(IReadOnlyList<Point3> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    public static float Distance(Point3 a, Point3 b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
