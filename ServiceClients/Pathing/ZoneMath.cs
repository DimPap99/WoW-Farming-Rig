using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;

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
    /// The smallest zone holding the world point: where zones overlap (Silithus and Feralas, or a
    /// city inside its zone) the smaller one is the more specific. Null when none does.
    /// </summary>
    public static ZoneInfo? ZoneAt(IEnumerable<ZoneInfo> zones, int mapId, float worldX, float worldY)
    {
        ZoneInfo? best = null;
        float bestArea = float.MaxValue;

        foreach (ZoneInfo zone in zones)
        {
            if (!Contains(zone, mapId, worldX, worldY))
            {
                continue;
            }

            float area = MathF.Abs((zone.LocTop - zone.LocBottom) * (zone.LocLeft - zone.LocRight));
            if (area < bestArea)
            {
                best = zone;
                bestArea = area;
            }
        }

        return best;
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
