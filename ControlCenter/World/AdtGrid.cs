using Contracts;

using System;

namespace ControlCenter.World;

/// <summary>
/// The world's ADT grid as the bake API counts it: 64 by 64 squares of 533 yards, x counting down
/// from the world's edge along world x, y along world y. Each ADT holds 4 by 4 navmesh tiles.
/// PPather keeps a transposed convention inside; the service reconciles them, so nothing here may.
/// </summary>
public static class AdtGrid
{
    /// <summary>One ADT's side, in yards.</summary>
    public const float AdtYd = 1600f / 3f;

    /// <summary>The world's edge from its middle: 32 ADTs.</summary>
    public const float EdgeYd = 32 * AdtYd;

    /// <summary>Navmesh tiles in one ADT.</summary>
    public const int TilesPerAdt = 16;

    /// <summary>The ADT a world point is in.</summary>
    public static (int X, int Y) Of(float worldX, float worldY)
    {
        return ((int)MathF.Floor((EdgeYd - worldX) / AdtYd), (int)MathF.Floor((EdgeYd - worldY) / AdtYd));
    }

    /// <summary>An ADT's two opposite corners, in world yards.</summary>
    public static (Point3 Min, Point3 Max) Bounds(int x, int y)
    {
        float maxX = EdgeYd - (x * AdtYd);
        float maxY = EdgeYd - (y * AdtYd);
        return (new Point3(maxX - AdtYd, maxY - AdtYd, 0), new Point3(maxX, maxY, 0));
    }
}
