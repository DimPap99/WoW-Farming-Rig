using Contracts;
using Contracts.Pathing;

using PathingService.Paths;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System;
using System.Globalization;
using System.Numerics;

namespace PathingService.Watch;

/// <summary>
/// Checks what a caller asks the viewers to draw and turns it into world yards, the viewers'
/// only space. Points in map space, or given without a height, take the navmesh height under
/// them; 0 where nothing is baked. A point beyond the edge of the world is refused, since its
/// height is never looked up (<see cref="NavmeshCoords.InWorld"/>).
/// </summary>
public static class WatchDrawing
{
    public const int MaxNameLength = 100;

    public static string? Problem(string? name, int uiMapId, ReadOnlySpan<Point3> points, CoordinateSpace space, WatchColor color,
        WorldMapAreaDB maps, out WorldMapArea zone)
    {
        zone = default;

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return $"a drawing needs a name of at most {MaxNameLength} characters";
        }

        if (!maps.TryGet(uiMapId, out zone))
        {
            return $"uiMap {uiMapId} is not in the map data";
        }

        if (points.IsEmpty || points.Length > RecordedPathStore.MaxPoints)
        {
            return $"a drawing has 1 to {RecordedPathStore.MaxPoints} points";
        }

        for (int i = 0; i < points.Length; i++)
        {
            Point3 p = points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                return "points must be finite numbers";
            }

            Vector3 at = At(p, space, zone);
            if (!NavmeshCoords.InWorld(at.X, at.Y))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"point {i} is at ({at.X:F0}, {at.Y:F0}) in world yards, beyond the edge of the world ({NavmeshCoords.WorldEdge:F2} from 0)");
            }
        }

        return Enum.IsDefined(color) ? null : $"{(int)color} is not a colour";
    }

    public static Vector3[] ToWorld(ReadOnlySpan<Point3> points, CoordinateSpace space, in WorldMapArea zone, RoutePlanner planner)
    {
        Vector3[] world = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 at = At(points[i], space, zone);
            if (at.Z == 0)
            {
                at.Z = planner.GetAreaAndHeight(zone.MapID, at.X, at.Y).Z;
            }

            world[i] = at;
        }

        return world;
    }

    // A point in world yards; a map point's height is left 0, to be looked up.
    private static Vector3 At(Point3 p, CoordinateSpace space, in WorldMapArea zone)
    {
        return space == CoordinateSpace.Map
            ? WorldMapAreaDB.ToWorld_FlipXY(new Vector3(p.X, p.Y, 0), zone)
            : new Vector3(p.X, p.Y, p.Z);
    }
}
