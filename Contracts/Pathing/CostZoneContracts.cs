using System.Collections.Generic;

namespace Contracts.Pathing;

/// <summary>How a danger zone affects routing.</summary>
public enum ZoneMode
{
    /// <summary>Priced up: a route crosses it only when there is no reasonable alternative.</summary>
    Avoid,

    /// <summary>Impassable, which can leave a destination unreachable.</summary>
    Block
}

/// <summary>A road routes are drawn towards.</summary>
/// <param name="Name">For people.</param>
/// <param name="Width">Corridor width in 33-yard chunks, 1 to 64.</param>
/// <param name="Points">The centreline, in world yards.</param>
public sealed record RoadLine(string Name, int Width, Point2[] Points);

/// <summary>The roads authored for one zone map.</summary>
public sealed record RoadZones(RoadLine[] Roads);

/// <summary>A circular danger zone, in world yards.</summary>
public sealed record DangerCircle(string Name, float CenterX, float CenterY, float Radius, float Penalty,
    ZoneMode Mode = ZoneMode.Avoid);

/// <summary>A rectangular danger zone, in world yards.</summary>
public sealed record DangerRectangle(string Name, float MinX, float MinY, float MaxX, float MaxY, float Penalty,
    ZoneMode Mode = ZoneMode.Avoid);

/// <summary>The danger zones authored for one zone map.</summary>
public sealed record DangerZones(DangerCircle[] Circles, DangerRectangle[] Rectangles);

/// <summary>Every authored zone of one continent, by the uiMap each was drawn on.</summary>
public sealed record CostZoneSet(string Continent, Dictionary<int, RoadZones> Roads, Dictionary<int, DangerZones> Danger);
