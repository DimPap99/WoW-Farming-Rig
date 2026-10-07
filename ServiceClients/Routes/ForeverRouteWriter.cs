using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ServiceClients.Routes;

/// <summary>
/// A designed route as a WoW: Forever route file (<c>SharedLib/Forever/ForeverRouteFile</c>), what a bot
/// follows of it (<see cref="RoutePoints"/>) written as steps. Points travel: shape nodes and the points
/// along exact legs are <c>Via</c>s, a required node a <c>To</c>, which ends a <c>Travel</c>, as does a
/// change of zone, since a step's points are one zone map's coordinates (0 to 100). A stop is a
/// <c>Visit</c> and an area a <c>Roam</c>, the way drawn to them a <c>Travel</c> to their place first. A
/// loop repeats; a route that applies the cost zones says so. The Pathing Service checks the text as a
/// run loads it before it saves it.
/// </summary>
public static class ForeverRouteWriter
{
    /// <summary>Via points written on one line of the file.</summary>
    private const int PointsPerLine = 6;

    /// <summary>
    /// The route file's text, or false with what stops it: a node, a point or an area in no zone, a stop
    /// with nobody to visit, an area with no kills to end, or nothing at all.
    /// </summary>
    /// <param name="zoneOf">The zone a point is in (<c>ZoneLookup</c>); a zone whose map does not hold the point is not used.</param>
    /// <param name="zones">Every zone map, for a point <paramref name="zoneOf"/> does not place.</param>
    /// <param name="mapId">The continent's instance map, which the zones are looked up on.</param>
    public static bool TryWrite(string title, RouteDesign design, Func<Point3, ZoneInfo?> zoneOf, IReadOnlyList<ZoneInfo> zones,
        int mapId, out string text, out string error)
    {
        text = string.Empty;
        RouteNode[] nodes = design.Nodes;
        if (nodes.Length == 0)
        {
            error = "There is nothing to save: add nodes, or draw a path or an area.";
            return false;
        }

        // Each node's zone, and what makes one unwritable.
        ZoneInfo[] zoneOfNode = new ZoneInfo[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (Problem(nodes[i]) is string problem)
            {
                error = $"Node {i + 1} {problem}.";
                return false;
            }

            Point3[] placed = nodes[i].Kind == NodeKind.Area ? nodes[i].Area! : [nodes[i].At];
            if ((Holding(zoneOf(RouteGeometry.Middle(placed)), mapId, placed) ?? ZoneMath.ZoneHolding(zones, mapId, placed))
                is not ZoneInfo zone)
            {
                error = nodes[i].Kind == NodeKind.Area
                    ? $"Node {i + 1}, an area, is not inside one zone: a Forever route names its corners by one zone map."
                    : $"Node {i + 1} is in no zone: a Forever route names points by their zone map.";
                return false;
            }

            zoneOfNode[i] = zone;
        }

        List<Step> steps = [];
        List<(Point3 At, ZoneInfo Zone)> travel = [];
        int leg = 0;
        foreach (RoutePoint point in RoutePoints.Of(design))
        {
            RouteNode? node = point.Node >= 0 ? nodes[point.Node] : null;
            leg = point.Node >= 0 ? point.Node : leg;
            bool ownStep = node is { Kind: NodeKind.Stop or NodeKind.Area };
            if ((node is not null && !ownStep ? zoneOfNode[point.Node] : PointZone(point.At)) is not ZoneInfo zone)
            {
                error = node is null
                    ? $"A point of the leg from node {leg + 1} is in no zone: a Forever route names points by their zone map."
                    : $"Node {point.Node + 1} is in no zone: a Forever route names points by their zone map.";
                return false;
            }

            if (ownStep)
            {
                // The way drawn to it ends at its place; its own step follows.
                if (travel.Count > 0)
                {
                    Travel(point.At, zone);
                    Flush(steps, travel);
                }

                steps.Add(node!.Kind == NodeKind.Stop ? Visit(node.Stop!) : Roam(node, zoneOfNode[point.Node]));
                continue;
            }

            Travel(point.At, zone);
            if (node?.Kind == NodeKind.Required)
            {
                Flush(steps, travel);
            }
        }

        Flush(steps, travel);
        text = Write(title, design, steps);
        error = string.Empty;
        return true;

        // A point off the nodes stays on the travel's map while that map holds it, so a line is one step where it can be.
        ZoneInfo? PointZone(Point3 at)
        {
            return travel.Count > 0 && ZoneMath.Contains(travel[^1].Zone, mapId, at.X, at.Y)
                ? travel[^1].Zone
                : Holding(zoneOf(at), mapId, [at]) ?? ZoneMath.ZoneAt(zones, mapId, at.X, at.Y);
        }

        // A point on the travel; one on another map ends it first, its last point the To.
        void Travel(Point3 at, ZoneInfo zone)
        {
            if (travel.Count > 0 && travel[^1].Zone.UiMapId != zone.UiMapId)
            {
                Flush(steps, travel);
            }

            travel.Add((at, zone));
        }
    }

    // What a node lacks to be written; null when nothing.
    private static string? Problem(RouteNode node)
    {
        return node.Kind switch
        {
            NodeKind.Stop when node.Stop is not { Named: true } => "is a stop with nobody to visit: choose an NPC or a role",
            NodeKind.Area when node.Area is not { Length: >= 2 } => "is an area without its corners: two opposite ones, or three or more",
            NodeKind.Area when node.Kills < 1 => "is an area roamed for no kills: a roam needs at least one to end",
            _ => null,
        };
    }

    /// <summary>A step's body, and the map its points are on (none for a visit, which names no point).</summary>
    private sealed record Step(ZoneInfo? Zone, string Body);

    // The travel built so far as one step, to its last point, via the others.
    private static void Flush(List<Step> steps, List<(Point3 At, ZoneInfo Zone)> travel)
    {
        if (travel.Count == 0)
        {
            return;
        }

        ZoneInfo zone = travel[0].Zone;
        StringBuilder body = new();
        body.Append("\"Travel\": { ");
        body.Append("\"To\": ").Append(MapPoint(zone, travel[^1].At));
        if (travel.Count > 1)
        {
            body.Append(", \"Via\": ");
            AppendPoints(body, zone, [.. travel.SkipLast(1).Select(t => t.At)], "        ");
        }

        body.Append(" }");
        steps.Add(new Step(zone, body.ToString()));
        travel.Clear();
    }

    // The first one the stop names, as ToString reads it.
    private static Step Visit(StopTarget stop)
    {
        string who = !string.IsNullOrWhiteSpace(stop.Npc) ? $"\"Npc\": {JsonSerializer.Serialize(stop.Npc.Trim())}"
            : !string.IsNullOrWhiteSpace(stop.Role) ? $"\"Role\": {JsonSerializer.Serialize(stop.Role.Trim())}"
            : stop.QuestStart > 0 ? string.Create(CultureInfo.InvariantCulture, $"\"QuestStart\": {stop.QuestStart}")
            : string.Create(CultureInfo.InvariantCulture, $"\"QuestEnd\": {stop.QuestEnd}");
        return new Step(null, $"\"Visit\": {{ {who} }}");
    }

    private static Step Roam(RouteNode area, ZoneInfo zone)
    {
        StringBuilder body = new();
        body.Append("\"Roam\": { \"Area\": ");
        AppendPoints(body, zone, area.Area!, "        ");
        body.Append(CultureInfo.InvariantCulture, $", \"Until\": {{ \"Kills\": {area.Kills} }} }}");
        return new Step(zone, body.ToString());
    }

    private static string Write(string title, RouteDesign design, List<Step> steps)
    {
        int areas = design.Nodes.Count(n => n.Kind == NodeKind.Area);
        int routeMap = steps.FirstOrDefault(s => s.Zone is not null)?.Zone!.UiMapId ?? 0;
        StringBuilder file = new();
        file.Append("{\n");
        file.Append(CultureInfo.InvariantCulture,
            $"  // Designed on the Control Center's route map: {Count(design.Nodes.Length - areas, "node")} and {Count(areas, "area")} to roam{(design.Loop ? ", as a loop" : string.Empty)}.\n");
        file.Append("  // Travel steps fight only what attacks; \"Pulls\": \"OnTheWay\" also takes what stands on the way.\n");
        file.Append("  \"Name\": ").Append(JsonSerializer.Serialize(title)).Append(",\n");
        file.Append(CultureInfo.InvariantCulture, $"  \"UIMapId\": {routeMap},\n");
        if (design.CostZones == CostZoneUse.Apply)
        {
            file.Append(CultureInfo.InvariantCulture, $"  \"{ForeverRouteSketch.CostZonesField}\": \"{nameof(CostZoneUse.Apply)}\",\n");
        }

        file.Append("  \"Steps\": [\n");
        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            // A step on another zone than the route's names its own map, first.
            string body = step.Zone is null || step.Zone.UiMapId == routeMap
                ? step.Body
                : step.Body.Insert(step.Body.IndexOf('{', StringComparison.Ordinal) + 2,
                    string.Create(CultureInfo.InvariantCulture, $"\"UIMapId\": {step.Zone.UiMapId}, "));
            file.Append("    { ").Append(body).Append(" }").Append(i + 1 < steps.Count ? ",\n" : "\n");
        }

        file.Append("  ],\n");
        file.Append(CultureInfo.InvariantCulture, $"  \"Repeat\": {(design.Loop ? "true" : "false")}\n");
        file.Append("}\n");
        return file.ToString();
    }

    private static string Count(int count, string noun)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
    }

    // Points as [x, y] pairs; a long list wraps, a few to a line.
    private static void AppendPoints(StringBuilder body, ZoneInfo zone, Point3[] points, string indent)
    {
        if (points.Length <= PointsPerLine)
        {
            body.Append('[').Append(string.Join(", ", points.Select(p => MapPoint(zone, p)))).Append(']');
            return;
        }

        body.Append("[\n");
        for (int i = 0; i < points.Length; i += PointsPerLine)
        {
            body.Append(indent)
                .Append(string.Join(", ", points.Skip(i).Take(PointsPerLine).Select(p => MapPoint(zone, p))))
                .Append(i + PointsPerLine < points.Length ? ",\n" : "\n");
        }

        body.Append(indent[..^2]).Append(']');
    }

    private static string MapPoint(ZoneInfo zone, Point3 world)
    {
        Point3 map = ZoneMath.ToMap(zone, world);
        return string.Create(CultureInfo.InvariantCulture, $"[{Round(map.X)}, {Round(map.Y)}]");
    }

    // Map coordinates to a hundredth: half a yard or so on a zone map.
    private static string Round(float value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);
    }

    // The zone, when its map holds every point: a step's points must lie on its map, 0 to 100.
    private static ZoneInfo? Holding(ZoneInfo? zone, int mapId, Point3[] points)
    {
        return zone is not null && points.All(p => ZoneMath.Contains(zone, mapId, p.X, p.Y)) ? zone : null;
    }
}
