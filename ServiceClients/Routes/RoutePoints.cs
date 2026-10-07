using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Routes;

/// <summary>A point a bot follows along a designed route: a node (its index), or a point along an exact leg (-1).</summary>
public readonly record struct RoutePoint(Point3 At, int Node);

/// <summary>
/// What a bot follows of a designed route, the same for both bots: every node, and along each exact leg
/// points <see cref="ExactSpacingYards"/> apart, so it walks the line as drawn; a replanned leg is only its
/// ends, and the bot finds the navmesh's way between them as it walks. A route that loops ends back at its
/// first node's place.
/// </summary>
public static class RoutePoints
{
    /// <summary>How far apart the points along an exact leg are: a Forever run passes each within 6 yards, so its walk keeps to the line.</summary>
    public const float ExactSpacingYards = 10;

    public static List<RoutePoint> Of(RouteDesign design)
    {
        RouteNode[] nodes = design.Nodes;
        List<RoutePoint> points = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            points.Add(new RoutePoint(nodes[i].At, i));
            if (i + 1 < nodes.Length)
            {
                AddLeg(points, nodes[i], nodes[i + 1].At);
            }
        }

        if (design.Loop && nodes.Length > 1)
        {
            AddLeg(points, nodes[^1], nodes[0].At);
            points.Add(new RoutePoint(nodes[0].At, -1));
        }

        return points;
    }

    private static void AddLeg(List<RoutePoint> points, RouteNode from, Point3 to)
    {
        if (from.Leg == LegKind.Exact)
        {
            points.AddRange(RouteGeometry.Between(from.At, to, ExactSpacingYards).Select(p => new RoutePoint(p, -1)));
        }
    }
}

/// <summary>What a designed route adds up to, from its legs as planned so far.</summary>
/// <param name="Yards">Along the legs: the lines of exact ones, the planned ways of replanned ones.</param>
/// <param name="Climb">Yards climbed along the points whose heights are known.</param>
/// <param name="Pending">Replanned legs not answered yet.</param>
/// <param name="Partial">Replanned legs whose way stops short.</param>
/// <param name="Failed">Replanned legs with no way at all.</param>
public sealed record RouteStats(int Legs, float Yards, float Climb, int Pending, int Partial, int Failed)
{
    /// <summary>A character's run speed, in yards a second.</summary>
    public const float RunYardsPerSecond = 7;

    /// <summary>How long it takes to run, at <see cref="RunYardsPerSecond"/>.</summary>
    public TimeSpan Running => TimeSpan.FromSeconds(Yards / RunYardsPerSecond);

    public static RouteStats Of(IReadOnlyCollection<DesignLeg> legs)
    {
        float climb = 0;
        foreach (DesignLeg leg in legs)
        {
            IReadOnlyList<Point3> way = leg.Way;
            for (int i = 1; i < way.Count; i++)
            {
                if (way[i].Z != 0 && way[i - 1].Z != 0)
                {
                    climb += MathF.Max(0, way[i].Z - way[i - 1].Z);
                }
            }
        }

        IEnumerable<DesignLeg> replanned = legs.Where(l => l.Kind == LegKind.Replanned);
        return new RouteStats(legs.Count, legs.Sum(l => l.Yards), climb, replanned.Count(l => l.Pending),
            replanned.Count(l => l.Planned.Partial), replanned.Count(l => l.Planned.Failed));
    }
}
