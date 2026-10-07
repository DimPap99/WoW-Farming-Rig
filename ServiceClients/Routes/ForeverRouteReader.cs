using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Routes;

/// <summary>
/// A Forever route file opened as a design, the way back from <see cref="ForeverRouteWriter"/>: a travel's
/// <c>Via</c>s are shape nodes and its <c>To</c> a required one; points closer together than
/// <see cref="RoutePoints.ExactSpacingYards"/> are a line walked as drawn, so an exact leg written as such
/// points comes back as one exact leg; a <c>Roam</c> is an area node, a <c>Visit</c> a stop. What a design
/// cannot hold - a <c>Quest</c> step, a visit with nowhere to stand, a step's pulls, seed, minutes or level -
/// is named, not dropped silently.
/// </summary>
public static class ForeverRouteReader
{
    // The points of a line written from an exact leg lie on it within this, or the rounding below if more.
    private const float LineToleranceYards = 1.5f;

    // A file keeps map coordinates to a hundredth of a map unit: a point is off by up to half of that on each axis.
    private const float RoundingUnits = 0.005f;

    /// <summary>
    /// The design <paramref name="sketch"/> describes, with what it leaves out (<paramref name="left"/>, each
    /// for a person to read).
    /// </summary>
    /// <param name="npcAt">Where an NPC a visit names stands, on the route's continent; null where none of that name does.</param>
    public static RouteDesign ToDesign(ForeverSketch sketch, IReadOnlyList<ZoneInfo> zones, Func<string, Point3?> npcAt,
        out List<string> left)
    {
        left = [];
        List<RouteNode> nodes = [];
        float rounding = 0;
        for (int i = 0; i < sketch.Steps.Length; i++)
        {
            ForeverStepSketch step = sketch.Steps[i];
            ZoneInfo? zone = zones.FirstOrDefault(z => z.UiMapId == step.UiMapId);
            Point3[] points = zone is null ? [] : [.. step.Points.Select(p => ZoneMath.ToWorld(zone, new Point3(p.X, p.Y, 0)))];
            if (zone is not null && points.Length > 0)
            {
                float yardsPerUnit = MathF.Max(MathF.Abs(zone.LocLeft - zone.LocRight), MathF.Abs(zone.LocTop - zone.LocBottom)) / ZoneMath.MapSize;
                rounding = MathF.Max(rounding, RoundingUnits * yardsPerUnit);
            }

            // The way drawn to an area or a stop ends at its place: the writer's travel there, its Vias and then
            // the place as its To, is the node's way and place, not a node of its own.
            Point3? place = null;
            if (step.Kind is ForeverStepKind.Roam or ForeverStepKind.Visit && i > 0 && sketch.Steps[i - 1] is { Travel: true, Points.Length: >= 2 } &&
                nodes.Count > 0 && nodes[^1].Kind == NodeKind.Required)
            {
                place = nodes[^1].At;
                nodes.RemoveAt(nodes.Count - 1);
            }

            if (step.Settings is { Length: > 0 } settings && step.Kind != ForeverStepKind.Quest)
            {
                left.Add($"step {i + 1}'s {string.Join(" and ", settings)}");
            }

            switch (step.Kind)
            {
                case ForeverStepKind.Travel when points.Length > 0:
                    nodes.AddRange(points.Select((at, k) => new RouteNode(at, k == points.Length - 1 ? NodeKind.Required : NodeKind.Shape)));
                    break;
                case ForeverStepKind.Roam when points.Length >= 2:
                    nodes.Add(new RouteNode(place ?? RouteGeometry.Middle(points), NodeKind.Area, Area: points,
                        Kills: step.Kills > 0 ? step.Kills : RouteEditor.DefaultKills));
                    break;
                case ForeverStepKind.Visit when step.Visit is { Npc: string npc } && (place ?? npcAt(npc)) is Point3 at:
                    nodes.Add(new RouteNode(at, NodeKind.Stop, Stop: step.Visit));
                    break;
                case ForeverStepKind.Visit when step.Visit is { Npc: null, Named: true } && (place ?? (nodes.Count > 0 ? nodes[^1].At : null)) is Point3 at:
                    // Found when the step begins - the nearest with a role, where a quest is given or handed in -: it
                    // stands where the route has come to.
                    nodes.Add(new RouteNode(at, NodeKind.Stop, Stop: step.Visit));
                    break;
                default:
                    if (place is Point3 to)
                    {
                        nodes.Add(new RouteNode(to, NodeKind.Required));
                    }

                    left.Add($"step {i + 1}, {What(step)}");
                    break;
            }
        }

        // Rounded, two points of one line are up to this much further apart than written, and as far off it.
        float error = 2 * MathF.Sqrt(2) * rounding;
        List<RouteNode> lined = Lines(nodes, RoutePoints.ExactSpacingYards + error + 0.05f, MathF.Max(LineToleranceYards, error));

        // A loop's writer ends back at the first node's place: the leg there is the closing one, on the node before it.
        if (sketch.Repeat && lined.Count > 2 && RouteGeometry.Distance(lined[^1].At, lined[0].At) <= MathF.Max(1, error))
        {
            lined.RemoveAt(lined.Count - 1);
        }

        return new RouteDesign([.. lined], sketch.Repeat, sketch.CostZones);
    }

    // Points close together are a line walked as drawn: the legs between them exact, its straight stretches one
    // leg each. A run goes through shape nodes only; any other kind ends one and may start the next.
    private static List<RouteNode> Lines(List<RouteNode> nodes, float spacing, float tolerance)
    {
        bool[] exact = [.. nodes.Select((n, i) => i + 1 < nodes.Count && RouteGeometry.Distance(n.At, nodes[i + 1].At) <= spacing)];
        List<RouteNode> kept = [];
        int start = 0;
        while (start < nodes.Count)
        {
            int end = start;
            while (exact[end] && (end == start || nodes[end].Kind == NodeKind.Shape))
            {
                end++;
            }

            if (end == start)
            {
                kept.Add(nodes[start]);
                start++;
                continue;
            }

            Point3[] line = RouteGeometry.Simplify([.. nodes[start..(end + 1)].Select(n => n.At)], tolerance);
            kept.AddRange(nodes[start..end].Where(n => line.Contains(n.At)).Select(n => n with { Leg = LegKind.Exact }));
            start = end;
        }

        return kept;
    }

    private static string What(ForeverStepSketch step)
    {
        return step.Kind switch
        {
            ForeverStepKind.Visit when step.Visit?.Npc is string npc => $"a visit to {npc}, who is not found on the route's continent",
            ForeverStepKind.Visit when step.Visit is { Named: true } => $"a visit to {step.Visit}, with no node before it to stand at",
            ForeverStepKind.Visit => "a visit that names nobody",
            ForeverStepKind.Quest => "a quest's camp, roamed",
            _ => $"a {step.Kind.ToString().ToLowerInvariant()} on a map this world does not have",
        };
    }
}
