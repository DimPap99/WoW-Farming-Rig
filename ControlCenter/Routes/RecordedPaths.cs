using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System.Collections.Generic;
using System.Linq;

namespace ControlCenter.Routes;

/// <summary>
/// The path library's files as the route designer opens and saves them. The bot records a path as
/// one zone map's coordinates (0 to 100) with no height; the few recorded with a height are world
/// yards.
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
}
