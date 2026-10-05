using Contracts;

using System;
using System.Collections.Generic;

namespace ControlCenter.World;

/// <summary>
/// What a page shows on the world view (<c>Components/Shared/WorldView</c>), sent whole whenever it
/// changes; the ground under it the view fetches itself. Positions are world yards.
/// </summary>
/// <param name="Continent">The continent shown; its relief and tiles are the ground.</param>
/// <param name="Frame">Move the view to frame the routes and markers when this scene arrives.</param>
public sealed record WorldScene(string Continent, WorldRoute[] Routes, WorldMarker[] Markers, WorldLabel[] Labels, bool Frame = false)
{
    public static WorldScene Empty(string continent)
    {
        return new WorldScene(continent, [], [], []);
    }
}

/// <summary>A line through points, drawn as its <see cref="WorldKinds"/> says.</summary>
/// <param name="Points">x, y, z triples.</param>
public sealed record WorldRoute(string Id, string Kind, float[] Points, string? Label = null)
{
    public static WorldRoute Of(string id, string kind, IEnumerable<Point3> points, string? label = null)
    {
        List<float> flat = [];
        foreach (Point3 p in points)
        {
            flat.Add(p.X);
            flat.Add(p.Y);
            flat.Add(p.Z);
        }

        return new WorldRoute(id, kind, [.. flat], label);
    }
}

/// <summary>A point marked on the view, with an optional label.</summary>
public sealed record WorldMarker(string Id, string Kind, float X, float Y, float Z, string? Label = null)
{
    public static WorldMarker At(string id, string kind, Point3 at, string? label = null)
    {
        return new WorldMarker(id, kind, at.X, at.Y, at.Z, label);
    }
}

/// <summary>A name shown at a place while the view is zoomed out, such as a zone's.</summary>
public sealed record WorldLabel(string Text, float X, float Y);

/// <summary>A point clicked on the view: world yards; <see cref="Z"/> is 0 where the ground under it is not drawn.</summary>
public readonly record struct WorldPoint(float X, float Y, float Z)
{
    public Point3 ToPoint3()
    {
        return new Point3(X, Y, Z);
    }

    public override string ToString()
    {
        return FormattableString.Invariant($"({X:F1}, {Y:F1})");
    }
}

/// <summary>How the view draws a route or a marker; the colours live in <c>world-view.js</c>.</summary>
public static class WorldKinds
{
    public const string Found = "found";
    public const string Partial = "partial";

    /// <summary>A dashed line where a route is missing: from where a partial route stops to its destination.</summary>
    public const string Gap = "gap";

    public const string Start = "start";
    public const string End = "end";

    /// <summary>Where a partial route stops.</summary>
    public const string Stop = "stop";
}
