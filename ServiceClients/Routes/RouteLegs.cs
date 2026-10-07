using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Routes;

/// <summary>
/// The planned legs of designed routes: one route request per pair of ends, a few at a time, each kept
/// by its ends (to a tenth of a yard, the precision the map sends), the search and the cost zones. Moving
/// a node plans only the legs it ends; the rest are kept. Requests go through <c>find</c>: the Pathing
/// Service's client (<see cref="Pathing.IPathingClient.FindRouteAsync"/>), or a planner in process.
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
    public Leg Get(Point3 from, Point3 to, RouteSearch search, CostZoneUse zones = CostZoneUse.Apply)
    {
        lock (sync)
        {
            return legs.GetValueOrDefault(LegKey.Of(from, to, search, zones), Leg.Pending);
        }
    }

    /// <summary>Each leg of <paramref name="design"/> as it is walked, with what is planned of it so far.</summary>
    public DesignLeg[] Of(RouteDesign design, RouteSearch search)
    {
        return [.. RouteEditor.Legs(design).Select(l =>
        {
            RouteNode from = design.Nodes[l.From];
            Point3 to = design.Nodes[l.To].At;
            return new DesignLeg(l.From, l.To, from.Leg, from.At, to, Get(from.At, to, search, design.CostZones));
        })];
    }

    /// <summary>
    /// Asks for the replanned legs of <paramref name="design"/> not planned or asked for yet, under its cost
    /// zones; with <paramref name="exact"/>, its exact legs too, to compare a line with the way the navmesh takes.
    /// </summary>
    /// <param name="uiMapOf">A zone map of the continent for a leg's start; the request needs one.</param>
    public void Plan(RouteDesign design, RouteSearch search, Func<Point3, int> uiMapOf, CancellationToken token, bool exact = false)
    {
        Plan(RouteEditor.Legs(design).Where(l => exact || design.Nodes[l.From].Leg == LegKind.Replanned)
                .Select(l => (design.Nodes[l.From].At, design.Nodes[l.To].At)),
            search, design.CostZones, uiMapOf, token);
    }

    /// <summary>
    /// Asks for every leg between consecutive <paramref name="waypoints"/> not planned or asked for yet;
    /// <c>changed</c> is called as each answer arrives, on the thread it arrives on.
    /// </summary>
    public void Plan(IReadOnlyList<Point3> waypoints, RouteSearch search, Func<Point3, int> uiMapOf, CancellationToken token)
    {
        Plan(waypoints.Zip(waypoints.Skip(1)), search, CostZoneUse.Apply, uiMapOf, token);
    }

    /// <summary>Asks for each leg of <paramref name="ends"/> not planned or asked for yet.</summary>
    public void Plan(IEnumerable<(Point3 From, Point3 To)> ends, RouteSearch search, CostZoneUse zones, Func<Point3, int> uiMapOf,
        CancellationToken token)
    {
        foreach ((Point3 from, Point3 to) in ends)
        {
            LegKey key = LegKey.Of(from, to, search, zones);
            lock (sync)
            {
                if (legs.ContainsKey(key) || !planning.Add(key))
                {
                    continue;
                }
            }

            _ = PlanAsync(key, new RouteRequest(null, uiMapOf(from), CoordinateSpace.World, from, to, Search: search, CostZones: zones), token);
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

    /// <summary>A leg's ends to a tenth of a yard, the search and the cost zones; heights are left out, as the service finds them.</summary>
    private readonly record struct LegKey(float FromX, float FromY, float ToX, float ToY, RouteSearch Search, CostZoneUse Zones)
    {
        public static LegKey Of(Point3 from, Point3 to, RouteSearch search, CostZoneUse zones)
        {
            return new LegKey(Tenth(from.X), Tenth(from.Y), Tenth(to.X), Tenth(to.Y), search, zones);
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

/// <summary>A leg of a design: its ends (node indices and places), how it is walked, and what the navmesh answered for its ends.</summary>
/// <param name="Planned">The way a replanned leg is walked; for an exact leg, the navmesh's way to compare it with
/// (<see cref="Leg.Pending"/> unless asked for).</param>
public sealed record DesignLeg(int From, int To, LegKind Kind, Point3 Start, Point3 End, Leg Planned)
{
    /// <summary>The way it is walked: the line as drawn, or the planned way (none until planned).</summary>
    public IReadOnlyList<Point3> Way => Kind == LegKind.Exact ? [Start, End] : Planned.Points;

    /// <summary>Its length along the ground in yards: the line's, or the planned way's (0 until planned).</summary>
    public float Yards => Kind == LegKind.Exact ? RouteGeometry.Distance(Start, End) : Planned.Length;

    /// <summary>Still waiting for the navmesh's answer the way it is walked depends on.</summary>
    public bool Pending => Kind == LegKind.Replanned && Planned.Status is null;
}
