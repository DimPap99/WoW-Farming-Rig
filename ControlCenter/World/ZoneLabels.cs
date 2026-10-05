using Contracts;
using Contracts.Pathing;

using ControlCenter.Pathing;

using ServiceClients.Pathing;

using System;
using System.Linq;

namespace ControlCenter.World;

/// <summary>Zone names at their middles, for finding one's way on a zoomed-out world view.</summary>
public static class ZoneLabels
{
    /// <summary>Wider than any zone: a map this size is a continent's or the world's, not a zone to name.</summary>
    public const float MaxZoneSpanYd = 9000;

    public static WorldLabel[] Of(WorldSnapshot? world, string continent)
    {
        return world is null
            ? []
            : [.. world.World.Zones
                .Where(z => z.Continent == continent &&
                            MathF.Abs(z.LocLeft - z.LocRight) < MaxZoneSpanYd && MathF.Abs(z.LocTop - z.LocBottom) < MaxZoneSpanYd)
                .Select(z =>
                {
                    Point3 middle = ZoneMath.ToWorld(z, new Point3(50, 50, 0));
                    return new WorldLabel(z.Name, middle.X, middle.Y);
                })];
    }

    /// <summary>A world point as a player reads it: its zone's name and map coordinates (<see cref="ZoneLookup"/>).</summary>
    public static string Where(ZoneInfo? zone, Point3 at)
    {
        if (zone is null)
        {
            return FormattableString.Invariant($"x {at.X:F0}, y {at.Y:F0}");
        }

        Point3 map = ZoneMath.ToMap(zone, at);
        return FormattableString.Invariant($"{zone.Name} {map.X:F1}, {map.Y:F1}");
    }
}
