using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Routes;

/// <summary>
/// The path library's files as the route designer opens and saves them. The bot records a path as
/// one zone map's coordinates (0 to 100) with no height; the few recorded with a height are world
/// yards. A designed route is saved as what a bot follows of it: its nodes, and its exact legs'
/// points; the Classic bot finds its own way between points, as a Forever run does.
/// </summary>
public static class RecordedPaths
{
    /// <summary>Whether a file's points are zone map coordinates: none has a height.</summary>
    public static bool AreMapPoints(IReadOnlyCollection<Point3> points)
    {
        return points.All(p => p.Z == 0);
    }

    /// <summary>A file's points as waypoints in world yards: map points through their zone, world points as they are.</summary>
    public static Point3[] ToWaypoints(IReadOnlyCollection<Point3> points, ZoneInfo zone)
    {
        return AreMapPoints(points) ? [.. points.Select(p => ZoneMath.ToWorld(zone, p))] : [.. points];
    }

    /// <summary>Waypoints as the bot records a path: the zone's map coordinates, with no height.</summary>
    public static Point3[] ToFile(IEnumerable<Point3> waypoints, ZoneInfo zone)
    {
        return [.. waypoints.Select(p => ZoneMath.ToMap(zone, p) with { Z = 0 })];
    }

    /// <summary>A designed route as a recorded path: what a bot follows of it (<see cref="RoutePoints"/>), on the zone's map.</summary>
    public static Point3[] ToFile(RouteDesign design, ZoneInfo zone)
    {
        return ToFile(RoutePoints.Of(design).Select(p => p.At), zone);
    }

    /// <summary>
    /// Whether a recorded path holds <paramref name="design"/> whole, so that saving it as one loses nothing: shape
    /// nodes only, replanned legs (an exact leg's points come back as nodes of their own) and no loop.
    /// </summary>
    public static bool Holds(RouteDesign design)
    {
        return !design.Loop && design.Nodes.All(n => n.Kind == NodeKind.Shape) &&
               design.Nodes.SkipLast(1).All(n => n.Leg == LegKind.Replanned);
    }
}
