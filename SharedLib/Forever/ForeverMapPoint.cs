using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace SharedLib.Forever;

/// <summary>
/// A point on a zone map in the coordinates a player reads, 0 to 100 across
/// the map, as upstream's recorded routes store them.
/// </summary>
public readonly record struct ForeverMapPoint(int UIMapId, double X, double Y)
{
    /// <summary>Map coordinates run from 0 to this.</summary>
    public const double MapSize = 100;

    /// <summary>Whether <paramref name="coordinate"/> lies on a map: a number from 0 to <see cref="MapSize"/>.</summary>
    public static bool OnMap(double coordinate)
    {
        return double.IsFinite(coordinate) && coordinate is >= 0 and <= MapSize;
    }

    /// <summary>
    /// The point in world yards, with the instance map its world positions
    /// report, or false with why. Any zone in the map data converts; whether
    /// the player can walk there is the start-up check's to say.
    /// </summary>
    public bool TryToWorld(WorldMapAreaDB maps, out Vector2 world, out int instanceMapId, out string reason)
    {
        world = default;
        instanceMapId = 0;
        if (!OnMap(X) || !OnMap(Y))
        {
            reason = $"the map point {this} is outside 0 to {MapSize}";
            return false;
        }
        if (!maps.TryGet(UIMapId, out WorldMapArea area))
        {
            reason = $"map {UIMapId} of the point {this} is not in the map data";
            return false;
        }
        Vector3 point = WorldMapAreaDB.ToWorld_FlipXY(new Vector3((float)X, (float)Y, 0), area);
        world = new(point.X, point.Y);
        instanceMapId = area.MapID;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The world point (<paramref name="x"/>, <paramref name="y"/>) of instance map <paramref name="mapId"/>
    /// as a point of the zone map it lies furthest inside, a continent's map only where no zone's holds
    /// it; null where no map of the client does. Zone maps overlap - Elwynn Forest's holds Stormwind -
    /// and a point lies well inside its own zone's map but near the edge of a neighbour's.
    /// </summary>
    public static ForeverMapPoint? FromWorld(WorldMapAreaDB maps, int mapId, float x, float y) =>
        OnOneMap(maps, mapId, [new Vector2(x, y)]) is [ForeverMapPoint point] ? point : null;

    /// <summary>
    /// World <paramref name="points"/> of instance map <paramref name="mapId"/> on the one map that holds
    /// them all: the zone map their middle lies furthest inside, else a continent's; null where no map
    /// of the client holds them all.
    /// </summary>
    public static ForeverMapPoint[]? OnOneMap(WorldMapAreaDB maps, int mapId, IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
            return null;
        Vector2 middle = new(points.Average(p => p.X), points.Average(p => p.Y));
        WorldMapArea? best = null;
        double bestInside = double.MinValue;
        foreach (WorldMapArea area in maps.Values)
        {
            if (area.MapID != mapId || points.Any(p => Inside(area, p) < 0))
                continue;
            double inside = Inside(area, middle);
            bool zone = area.AreaID != 0;
            bool bestIsZone = best is { AreaID: not 0 };
            if (best is null || (zone && !bestIsZone) || (zone == bestIsZone && inside > bestInside))
            {
                best = area;
                bestInside = inside;
            }
        }
        if (best is not { } on)
            return null;
        return [.. points.Select(p =>
        {
            Vector3 map = WorldMapAreaDB.ToMap_FlipXY(new Vector3(p.X, p.Y, 0), on);
            return new ForeverMapPoint(on.UIMapId, Math.Clamp(map.X, 0, MapSize), Math.Clamp(map.Y, 0, MapSize));
        })];
    }

    // How far inside a map a world point lies, in the map's own 0 to 100: the nearest edge, negative outside.
    private static double Inside(in WorldMapArea area, Vector2 world)
    {
        Vector3 map = WorldMapAreaDB.ToMap_FlipXY(new Vector3(world.X, world.Y, 0), area);
        return Math.Min(Math.Min(map.X, MapSize - map.X), Math.Min(map.Y, MapSize - map.Y));
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:0.##}, {Y:0.##} on map {UIMapId}");
}
