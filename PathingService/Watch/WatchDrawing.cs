using Contracts;
using Contracts.Pathing;

using PathingService.Paths;

using PPather.Planning;

using SharedLib;

using System;
using System.Numerics;

namespace PathingService.Watch;

/// <summary>
/// Checks what a caller asks the viewers to draw and turns it into world yards, the viewers'
/// only space. Points in map space, or given without a height, take the navmesh height under
/// them; 0 where nothing is baked.
/// </summary>
public static class WatchDrawing
{
    public const int MaxNameLength = 100;

    public static string? Problem(string? name, int uiMapId, ReadOnlySpan<Point3> points, WatchColor color,
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

        foreach (Point3 p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                return "points must be finite numbers";
            }
        }

        return Enum.IsDefined(color) ? null : $"{(int)color} is not a colour";
    }

    public static Vector3[] ToWorld(ReadOnlySpan<Point3> points, CoordinateSpace space, in WorldMapArea zone, RoutePlanner planner)
    {
        Vector3[] world = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Point3 p = points[i];
            Vector3 at = space == CoordinateSpace.Map
                ? WorldMapAreaDB.ToWorld_FlipXY(new Vector3(p.X, p.Y, 0), zone)
                : new Vector3(p.X, p.Y, p.Z);

            if (at.Z == 0)
            {
                at.Z = planner.GetAreaAndHeight(zone.MapID, at.X, at.Y).Z;
            }

            world[i] = at;
        }

        return world;
    }
}
