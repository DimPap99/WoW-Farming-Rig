using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace SharedLib.Forever;

/// <summary>
/// An area of one zone map, in the coordinates a player reads (0 to 100): a
/// polygon of three or more corners in order, or a rectangle given by two
/// opposite corners (<see cref="Rectangle"/>).
/// </summary>
public sealed record ForeverArea(int UIMapId, IReadOnlyList<(double X, double Y)> Corners)
{
    /// <summary>The rectangle with opposite corners <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static ForeverArea Rectangle(int uiMapId, (double X, double Y) a, (double X, double Y) b) =>
        new(uiMapId, [(a.X, a.Y), (b.X, a.Y), (b.X, b.Y), (a.X, b.Y)]);

    /// <summary>Two corners make a rectangle, three or more a polygon, as route files give them.</summary>
    public static ForeverArea FromCorners(int uiMapId, IReadOnlyList<(double X, double Y)> corners) =>
        corners.Count == 2 ? Rectangle(uiMapId, corners[0], corners[1]) : new(uiMapId, corners);

    /// <summary>The area in world yards on a verified map, with the instance map it is on, or false with why.</summary>
    public bool TryToWorld(WorldMapAreaDB maps, out ForeverWorldArea world, out int instanceMapId, out string reason)
    {
        world = null!;
        instanceMapId = 0;
        if (Corners.Count < 3)
        {
            reason = $"{this} needs three corners, or two for a rectangle";
            return false;
        }
        List<Vector2> corners = new(Corners.Count);
        foreach ((double x, double y) in Corners)
        {
            if (!new ForeverMapPoint(UIMapId, x, y).TryToWorld(maps, out Vector2 corner, out instanceMapId, out reason))
                return false;
            corners.Add(corner);
        }
        world = new ForeverWorldArea(corners);
        if (world.Width < 1 || world.Height < 1)
        {
            reason = $"{this} has no width";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"area around {Corners.Average(c => c.X):0.#}, {Corners.Average(c => c.Y):0.#} on map {UIMapId}");
}

/// <summary>An area's polygon in world yards.</summary>
public sealed class ForeverWorldArea
{
    // Rejection sampling of a point inside: tries before the centre stands in.
    private const int SampleTries = 32;

    private readonly Vector2[] corners;

    public ForeverWorldArea(IReadOnlyList<Vector2> corners)
    {
        this.corners = [.. corners];
        Min = new(this.corners.Min(c => c.X), this.corners.Min(c => c.Y));
        Max = new(this.corners.Max(c => c.X), this.corners.Max(c => c.Y));
        Centre = new(this.corners.Average(c => c.X), this.corners.Average(c => c.Y));
    }

    public Vector2 Min { get; }
    public Vector2 Max { get; }
    public Vector2 Centre { get; }
    public float Width => Max.X - Min.X;
    public float Height => Max.Y - Min.Y;

    /// <summary>Whether <paramref name="point"/> lies inside, by the even-odd rule.</summary>
    public bool Contains(Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>A point drawn evenly over the area; the centre when the draws keep missing a thin shape.</summary>
    public Vector2 RandomPoint(Random random)
    {
        for (int i = 0; i < SampleTries; i++)
        {
            Vector2 point = new(Min.X + random.NextSingle() * Width, Min.Y + random.NextSingle() * Height);
            if (Contains(point))
                return point;
        }
        return Centre;
    }
}
