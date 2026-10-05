using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Pathing;

/// <summary>
/// Zone map coordinates (0 to 100) and world yards, from the bounds the service publishes in
/// <see cref="ZoneInfo"/>. The arithmetic is <c>WorldMapArea</c>'s, flip included: map x runs
/// along world y and map y along world x.
/// </summary>
public static class ZoneMath
{
    public const float MapSize = 100f;

    /// <summary>The world point of a map point; the height is carried as given.</summary>
    public static Point3 ToWorld(ZoneInfo zone, Point3 map)
    {
        float worldX = ((zone.LocBottom - zone.LocTop) * map.Y / MapSize) + zone.LocTop;
        float worldY = ((zone.LocRight - zone.LocLeft) * map.X / MapSize) + zone.LocLeft;
        return new Point3(worldX, worldY, map.Z);
    }

    /// <summary>The map point of a world point; the height is carried as given.</summary>
    public static Point3 ToMap(ZoneInfo zone, Point3 world)
    {
        float mapX = MapSize - (((world.Y - zone.LocRight) * MapSize) / (zone.LocLeft - zone.LocRight));
        float mapY = MapSize - (((world.X - zone.LocBottom) * MapSize) / (zone.LocTop - zone.LocBottom));
        return new Point3(mapX, mapY, world.Z);
    }

    /// <summary>Whether the world point lies inside the zone's rectangle on its instance map.</summary>
    public static bool Contains(ZoneInfo zone, int mapId, float worldX, float worldY)
    {
        return zone.MapId == mapId &&
               worldX <= zone.LocTop && worldX >= zone.LocBottom &&
               worldY <= zone.LocLeft && worldY >= zone.LocRight;
    }

    /// <summary>
    /// The zone a world point is in, judged from the zone maps alone: of those whose map holds it,
    /// the one it lies furthest inside. Zone maps overlap a lot - Durotar's covers the east of the
    /// Barrens, Elwynn's all of Stormwind - and a point lies well inside its own zone's map but
    /// near the edge of a neighbour's, while a city's streets lie well inside the city's map.
    /// Continent and world maps (area 0) count only when no zone's map holds the point: the
    /// continent's own (named as its continent) before any other. Null when no map does.
    /// </summary>
    public static ZoneInfo? ZoneAt(IEnumerable<ZoneInfo> zones, int mapId, float worldX, float worldY)
    {
        return ZoneHolding(zones, mapId, [new Point3(worldX, worldY, 0)]);
    }

    /// <summary>
    /// The zone whose map holds every one of <paramref name="points"/> furthest inside, as
    /// <see cref="ZoneAt"/> judges one point; null when no map holds them all.
    /// </summary>
    public static ZoneInfo? ZoneHolding(IEnumerable<ZoneInfo> zones, int mapId, IReadOnlyCollection<Point3> points)
    {
        ZoneInfo? best = null;
        int bestRank = -1;
        float bestScore = float.MinValue;

        foreach (ZoneInfo zone in zones)
        {
            if (points.Count == 0 || !points.All(p => Contains(zone, mapId, p.X, p.Y)))
            {
                continue;
            }

            // Zones first, by how far inside; then the continent's own map; then any other (the world's).
            int rank = zone.AreaId != 0 ? 2 : zone.Name.Equals(zone.Continent, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            float score = rank == 2 ? points.Min(p => Margin(zone, p)) : 0;
            if (rank > bestRank || (rank == bestRank && score > bestScore))
            {
                best = zone;
                bestRank = rank;
                bestScore = score;
            }
        }

        return best;
    }

    // How far inside the zone's map a world point lies: map units to the nearest edge.
    private static float Margin(ZoneInfo zone, Point3 world)
    {
        Point3 map = ToMap(zone, world);
        return MathF.Min(MathF.Min(map.X, MapSize - map.X), MathF.Min(map.Y, MapSize - map.Y));
    }

    /// <summary>
    /// The zone whose name a recorded path's file name contains, the longest match winning
    /// ("16_Loch Modan.json" is Loch Modan). Null when no zone name appears in it.
    /// </summary>
    public static ZoneInfo? GuessFromName(IEnumerable<ZoneInfo> zones, string fileName)
    {
        ZoneInfo? best = null;
        foreach (ZoneInfo zone in zones)
        {
            if (zone.Name.Length > 0 &&
                fileName.Contains(zone.Name, StringComparison.OrdinalIgnoreCase) &&
                (best is null || zone.Name.Length > best.Name.Length))
            {
                best = zone;
            }
        }

        return best;
    }
}
