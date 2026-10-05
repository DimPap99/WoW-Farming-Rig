using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ControlCenter.Routes;

/// <summary>
/// A designed route as a WoW: Forever route file (<c>SharedLib/Forever/ForeverRouteFile</c>). The
/// waypoints become Travel steps, one per run of waypoints in the same zone, since a step's points
/// are one zone map's coordinates (0 to 100). Each area becomes a Roam step, placed after the
/// waypoint nearest its middle, so the route roams it on the way. The Pathing Service checks the
/// text as a run loads it before it saves it.
/// </summary>
public static class ForeverExport
{
    /// <summary>Kills a roam ends after when the page asks for no other number.</summary>
    public const int DefaultKills = 20;

    /// <summary>Via points written on one line of the file.</summary>
    private const int PointsPerLine = 6;

    /// <summary>
    /// The route file's text, or false with what stops it: a waypoint or an area in no zone, or a
    /// route with nothing in it.
    /// </summary>
    /// <param name="areas">Each area's corners: two opposite ones, or three or more in order.</param>
    /// <param name="mapId">The continent's instance map, which the zones are looked up on.</param>
    public static bool TryBuild(string title, IReadOnlyList<Point3> waypoints, IReadOnlyList<IReadOnlyList<Point3>> areas,
        int kills, IReadOnlyList<ZoneInfo> zones, int mapId, out string text, out string error)
    {
        text = string.Empty;
        if (waypoints.Count == 0 && areas.Count == 0)
        {
            error = "There is nothing to save: add waypoints or draw an area.";
            return false;
        }

        if (kills < 1)
        {
            error = "A roam needs at least one kill to end.";
            return false;
        }

        // Each waypoint's zone; a travel step stays on one.
        ZoneInfo[] zoneOf = new ZoneInfo[waypoints.Count];
        for (int i = 0; i < waypoints.Count; i++)
        {
            if (ZoneMath.ZoneAt(zones, mapId, waypoints[i].X, waypoints[i].Y) is not ZoneInfo zone)
            {
                error = $"Waypoint {i + 1} is in no zone: a Forever route names points by their zone map.";
                return false;
            }

            zoneOf[i] = zone;
        }

        // Each area on the zone map holding all its corners furthest inside, roamed after the waypoint nearest its middle.
        List<(int After, int Index, ZoneInfo Zone)> roams = [];
        for (int k = 0; k < areas.Count; k++)
        {
            if (areas[k].Count < 2)
            {
                error = $"Area {k + 1} needs two opposite corners, or three or more.";
                return false;
            }

            if (ZoneMath.ZoneHolding(zones, mapId, [.. areas[k]]) is not ZoneInfo zone)
            {
                error = $"Area {k + 1} is not inside one zone: a Forever route names its corners by one zone map.";
                return false;
            }

            roams.Add((Nearest(waypoints, Middle(areas[k])), k, zone));
        }

        List<Step> steps = [];
        int next = 0;
        foreach ((int after, int index, ZoneInfo zone) in roams.OrderBy(r => r.After).ThenBy(r => r.Index))
        {
            AddTravel(steps, waypoints, zoneOf, next, after + 1);
            next = Math.Max(next, after + 1);
            steps.Add(new Step(zone, Roam(areas[index], zone, kills)));
        }

        AddTravel(steps, waypoints, zoneOf, next, waypoints.Count);

        text = Write(title, steps, waypoints.Count, areas.Count);
        error = string.Empty;
        return true;
    }

    private sealed record Step(ZoneInfo Zone, string Body);

    // One Travel step per run of waypoints [from, to) in the same zone: to its last, via the others.
    private static void AddTravel(List<Step> steps, IReadOnlyList<Point3> waypoints, ZoneInfo[] zoneOf, int from, int to)
    {
        int start = from;
        while (start < to)
        {
            int end = start;
            while (end + 1 < to && zoneOf[end + 1].UiMapId == zoneOf[start].UiMapId)
            {
                end++;
            }

            ZoneInfo zone = zoneOf[start];
            StringBuilder body = new();
            body.Append("\"Travel\": { ");
            body.Append("\"To\": ").Append(MapPoint(zone, waypoints[end]));
            if (end > start)
            {
                body.Append(", \"Via\": ");
                AppendPoints(body, zone, Enumerable.Range(start, end - start).Select(i => waypoints[i]).ToList(), "        ");
            }

            body.Append(" }");
            steps.Add(new Step(zone, body.ToString()));
            start = end + 1;
        }
    }

    private static string Roam(IReadOnlyList<Point3> corners, ZoneInfo zone, int kills)
    {
        StringBuilder body = new();
        body.Append("\"Roam\": { \"Area\": ");
        AppendPoints(body, zone, corners, "        ");
        body.Append(CultureInfo.InvariantCulture, $", \"Until\": {{ \"Kills\": {kills} }} }}");
        return body.ToString();
    }

    private static string Write(string title, List<Step> steps, int waypoints, int areas)
    {
        int routeMap = steps[0].Zone.UiMapId;
        StringBuilder file = new();
        file.Append("{\n");
        file.Append(CultureInfo.InvariantCulture,
            $"  // Designed on the Control Center's route map: {Count(waypoints, "waypoint")} and {Count(areas, "area")} to roam.\n");
        file.Append("  // Travel steps fight only what attacks; \"Pulls\": \"OnTheWay\" also takes what stands on the way.\n");
        file.Append("  \"Name\": ").Append(JsonSerializer.Serialize(title)).Append(",\n");
        file.Append(CultureInfo.InvariantCulture, $"  \"UIMapId\": {routeMap},\n");
        file.Append("  \"Steps\": [\n");
        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            // A step on another zone than the route's names its own map, first.
            string body = step.Zone.UiMapId == routeMap
                ? step.Body
                : step.Body.Insert(step.Body.IndexOf('{', StringComparison.Ordinal) + 2,
                    string.Create(CultureInfo.InvariantCulture, $"\"UIMapId\": {step.Zone.UiMapId}, "));
            file.Append("    { ").Append(body).Append(" }").Append(i + 1 < steps.Count ? ",\n" : "\n");
        }

        file.Append("  ],\n");
        file.Append("  \"Repeat\": false\n");
        file.Append("}\n");
        return file.ToString();
    }

    private static string Count(int count, string noun)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
    }

    // Points as [x, y] pairs; a long list wraps, a few to a line.
    private static void AppendPoints(StringBuilder body, ZoneInfo zone, IReadOnlyList<Point3> points, string indent)
    {
        if (points.Count <= PointsPerLine)
        {
            body.Append('[').Append(string.Join(", ", points.Select(p => MapPoint(zone, p)))).Append(']');
            return;
        }

        body.Append("[\n");
        for (int i = 0; i < points.Count; i += PointsPerLine)
        {
            body.Append(indent)
                .Append(string.Join(", ", points.Skip(i).Take(PointsPerLine).Select(p => MapPoint(zone, p))))
                .Append(i + PointsPerLine < points.Count ? ",\n" : "\n");
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

    private static Point3 Middle(IReadOnlyList<Point3> corners)
    {
        return new Point3(corners.Average(c => c.X), corners.Average(c => c.Y), 0);
    }

    // The waypoint nearest a point; -1 (before the first) when there are none.
    private static int Nearest(IReadOnlyList<Point3> waypoints, Point3 at)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < waypoints.Count; i++)
        {
            float distance = RouteGeometry.Distance(waypoints[i], at);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
