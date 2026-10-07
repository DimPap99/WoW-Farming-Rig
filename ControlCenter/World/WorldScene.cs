using Contracts;

using System;
using System.Collections.Generic;

namespace ControlCenter.World;

/// <summary>
/// What a page shows on the world view (<c>Components/Shared/WorldView</c>), sent whole whenever it
/// changes; the ground under it the view fetches itself. Positions are world yards.
/// </summary>
/// <param name="Continent">The continent shown; its relief and tiles are the ground.</param>
/// <param name="Frame">Move the view to frame the routes, markers and shapes when this scene arrives.</param>
/// <param name="Shapes">Areas, lines and points, drawn under the routes; editable ones can be changed with the pointer.</param>
/// <param name="Selected">The shape whose handles show, when it is editable; null for none.</param>
/// <param name="FrameOn">x, y, z triples to frame when this scene arrives, instead of what it holds: a zone's corners.</param>
public sealed record WorldScene(string Continent, WorldRoute[] Routes, WorldMarker[] Markers, WorldLabel[] Labels, bool Frame = false,
    WorldShape[]? Shapes = null, string? Selected = null, float[]? FrameOn = null)
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
        return new WorldRoute(id, kind, WorldPoints.Flatten(points), label);
    }
}

/// <summary>A point marked on the view, with an optional label always shown, and a title shown on hover.</summary>
public sealed record WorldMarker(string Id, string Kind, float X, float Y, float Z, string? Label = null, string? Title = null)
{
    public static WorldMarker At(string id, string kind, Point3 at, string? label = null, string? title = null)
    {
        return new WorldMarker(id, kind, at.X, at.Y, at.Z, label, title);
    }
}

/// <summary>A name shown at a place while the view is zoomed out, such as a zone's.</summary>
/// <param name="Size">How much ground it names, in yards (a zone's: the side of a square as large as
/// its map). Where two names would overlap on the screen, the larger shows.</param>
public sealed record WorldLabel(string Text, float X, float Y, float Size = 0);

/// <summary>
/// A shape on the view: an area, a line or points. While it is the scene's selected shape and
/// <paramref name="Editable"/>, it shows handles: drag a vertex to move it, drag the dot between two
/// to insert one, right-click one to remove it; the result comes back as a <see cref="WorldEdited"/>.
/// </summary>
/// <param name="Type">What it is: one of <see cref="WorldShapes"/>.</param>
/// <param name="Kind">How it looks: one of the shape kinds in <see cref="WorldKinds"/>.</param>
/// <param name="Points">x, y, z triples: a rectangle's two opposite corners, a circle's centre. A height of 0 is unknown.</param>
/// <param name="Radius">A circle's, in yards.</param>
public sealed record WorldShape(string Id, string Type, string Kind, float[] Points, float Radius = 0, bool Editable = false,
    string? Label = null)
{
    public static WorldShape Of(string id, string type, string kind, IEnumerable<Point3> points, bool editable = false,
        string? label = null, float radius = 0)
    {
        return new WorldShape(id, type, kind, WorldPoints.Flatten(points), radius, editable, label);
    }
}

/// <summary>A drawing tool the view offers: the shape a click draws, and what the page calls it.</summary>
/// <param name="Shape">One of <see cref="WorldShapes"/>.</param>
/// <param name="Title">How to use it, shown when the pointer rests on its button: "Add waypoints: each click adds one".</param>
/// <param name="Label">Its name on the button, a word or two: "Waypoints".</param>
public sealed record WorldTool(string Shape, string Title, string Label);

/// <summary>A shape the pointer finished drawing with a tool.</summary>
/// <param name="Shape">The tool's: one of <see cref="WorldShapes"/>.</param>
/// <param name="Points">A rectangle's two opposite corners, a circle's centre; a height of 0 is unknown.</param>
/// <param name="Radius">A circle's, in yards.</param>
public sealed record WorldDrawn(string Shape, Point3[] Points, float Radius);

/// <summary>An editable shape the pointer changed: its whole geometry as it is now.</summary>
public sealed record WorldEdited(string Id, Point3[] Points, float Radius);

/// <summary>A point clicked on the view: world yards; <see cref="Z"/> is 0 where the height of the ground under it is not known.</summary>
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

/// <summary>Points as the view's script takes them: x, y, z one after another.</summary>
public static class WorldPoints
{
    public static float[] Flatten(IEnumerable<Point3> points)
    {
        List<float> flat = [];
        foreach (Point3 p in points)
        {
            flat.Add(p.X);
            flat.Add(p.Y);
            flat.Add(p.Z);
        }

        return [.. flat];
    }

    public static Point3[] Unflatten(float[] flat)
    {
        Point3[] points = new Point3[flat.Length / 3];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Point3(flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]);
        }

        return points;
    }
}

/// <summary>The shapes the view draws, and the tools that draw them (a tool is named by its shape).</summary>
public static class WorldShapes
{
    /// <summary>One point; as a tool, every click is one.</summary>
    public const string Point = "point";

    /// <summary>An open line, two vertices or more.</summary>
    public const string Line = "line";

    /// <summary>A closed area, three vertices or more.</summary>
    public const string Polygon = "polygon";

    /// <summary>Points in order, joined by a faint dashed line, such as waypoints.</summary>
    public const string Points = "points";

    /// <summary>An area from two opposite corners.</summary>
    public const string Rectangle = "rectangle";

    /// <summary>A centre and a radius.</summary>
    public const string Circle = "circle";
}

/// <summary>How the view draws a route, a marker or a shape; the colours live in <c>world-view.js</c> and <c>world-edit.js</c>.</summary>
public static class WorldKinds
{
    public const string Found = "found";
    public const string Partial = "partial";

    /// <summary>A dashed line where a route is missing: from where a partial route stops to its destination.</summary>
    public const string Gap = "gap";

    /// <summary>A dashed line where no route was found at all.</summary>
    public const string Failed = "failed";

    /// <summary>A dashed line where a route is still being planned.</summary>
    public const string Pending = "pending";

    /// <summary>A route shown faintly beside the one that matters, such as the ones planned before it.</summary>
    public const string Recent = "recent";

    /// <summary>A place to pick, such as an NPC to route to.</summary>
    public const string Npc = "npc";

    public const string Start = "start";
    public const string End = "end";

    /// <summary>Where a partial route stops.</summary>
    public const string Stop = "stop";

    // Quests (the layers' panel, WorldLayers).

    /// <summary>Where a quest is given or handed in: a zone's quest givers, a quest's start and end.</summary>
    public const string Quest = "quest";

    /// <summary>A creature a quest asks to kill.</summary>
    public const string QuestKill = "quest-kill";

    /// <summary>A creature that drops an item a quest needs.</summary>
    public const string QuestLoot = "quest-loot";

    /// <summary>An object a quest asks to use, or one that holds an item it needs.</summary>
    public const string QuestObject = "quest-object";

    /// <summary>A vendor who sells an item a quest needs.</summary>
    public const string QuestBuy = "quest-buy";

    /// <summary>A place a quest asks to go to.</summary>
    public const string QuestExplore = "quest-explore";

    // Shapes.

    /// <summary>Points a route is planned through, numbered.</summary>
    public const string Waypoints = "waypoints";

    public const string Path = "path";
    public const string Road = "road";
    public const string DangerAvoid = "danger-avoid";
    public const string DangerBlock = "danger-block";

    /// <summary>An area to roam, such as a Forever route's.</summary>
    public const string Area = "area";

    public const string Poi = "poi";

    /// <summary>What the map page's marks are drawn as: a point marked, or a shape drawn to measure.</summary>
    public const string Mark = "mark";
}
