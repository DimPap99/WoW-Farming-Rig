using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ServiceClients.Pathing;

/// <summary>
/// The path the Path Editor records and edits, in map coordinates: its points in
/// order, the selected point, and a preview point the selection can be moved to.
/// A point is found by its value, so points that are equal move and go together.
/// Shared by BlazorServer's recorder and the Control Center's editor, which saves
/// the result through <see cref="IPathingClient.SavePathAsync"/>.
/// </summary>
public sealed class PathEditor
{
    // Map coordinates are scaled up for the recording distance, as SharedLib's
    // MapDistanceXY does, so a spacing means the same here as anywhere else.
    private const float MapDistanceScale = 100f;

    private readonly List<Vector3> points;

    // Where the selected point was last seen. A path that goes there and back
    // repeats points, so the search for the selected one starts here.
    private int selectedIndex;

    public PathEditor(IEnumerable<Vector3> route)
    {
        points = [.. route];
    }

    /// <summary>The points themselves, not a copy: a route source follows the edits.</summary>
    public IReadOnlyList<Vector3> Points => points;

    public int Count => points.Count;

    public Vector3 Selected { get; private set; }

    public Vector3 Preview { get; set; }

    public ReadOnlySpan<Vector3> AsSpan() => CollectionsMarshal.AsSpan(points);

    public Vector3[] ToArray() => [.. points];

    public void Clear()
    {
        points.Clear();
        selectedIndex = 0;
        Selected = default;
        Preview = default;
    }

    /// <summary>Adds <paramref name="position"/> when it is the first point or further than <paramref name="spacing"/> from the last.</summary>
    public bool TryRecord(Vector3 position, float spacing)
    {
        if (points.Count == 0 || MapDistanceXY(position, points[^1]) > spacing)
        {
            points.Add(position);
            return true;
        }

        return false;
    }

    /// <summary>Selects <paramref name="point"/>, found at <paramref name="index"/>, and previews where it is.</summary>
    public void Select(Vector3 point, int index)
    {
        Selected = point;
        Preview = new Vector3(point.X, point.Y, 0);
        selectedIndex = index;
    }

    /// <summary>The point <paramref name="step"/> places from the selected one, clamped to the path's ends.</summary>
    public bool TryGetNeighbour(int step, out Vector3 point, out int index)
    {
        point = default;
        index = -1;

        if (Selected == default)
            return false;

        int found = FindSelected();
        if (found < 0)
            return false;

        index = Math.Clamp(found + step, 0, points.Count - 1);
        point = points[index];
        return true;
    }

    /// <summary>Moves the selected point, and every point equal to it, to <paramref name="target"/>.</summary>
    public void MoveSelectedTo(Vector3 target)
    {
        if (Selected == default)
            return;

        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] == Selected)
                points[i] = target;
        }

        Selected = target;
    }

    /// <summary>
    /// Removes the selected point, and every point equal to it, and selects the
    /// point now at its place. The removed position stays as the preview.
    /// </summary>
    public void RemoveSelected()
    {
        Vector3 removedPoint = Selected;
        int index = FindSelected();
        int removed = points.RemoveAll(p => p == removedPoint);
        if (removed == 0)
            return;

        Preview = removedPoint;

        if (index > -1 && points.Count > 0)
        {
            if (index >= points.Count)
                index -= removed;

            // Equal points ahead of it can leave the place past either end.
            index = Math.Clamp(index, 0, points.Count - 1);
            Selected = points[index];
            selectedIndex = index;
        }
        else
        {
            Selected = default;
            Preview = default;
        }
    }

    /// <summary>Inserts <paramref name="position"/>, on the ground plane, after the selected point and selects it.</summary>
    public void InsertAfterSelected(Vector3 position)
    {
        if (Selected == default)
            return;

        Vector3 selected = Selected;
        int index = points.FindIndex(p => p == selected);
        if (index == -1)
            return;

        position.Z = 0;
        points.Insert(index + 1, position);
        Selected = position;
    }

    /// <summary>Keeps every other point, the first included.</summary>
    public void Halve()
    {
        int kept = 0;
        for (int i = 0; i < points.Count; i += 2)
        {
            points[kept++] = points[i];
        }
        points.RemoveRange(kept, points.Count - kept);
    }

    /// <summary>Puts a point halfway between each pair of neighbours.</summary>
    public void Double()
    {
        for (int i = 1; i < points.Count; i += 2)
        {
            Vector3 current = points[i];
            Vector3 previous = points[i - 1];

            points.Insert(i, new((current.X + previous.X) / 2f, (current.Y + previous.Y) / 2f, 0));
        }
    }

    private static float MapDistanceXY(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.X, a.Y) * MapDistanceScale, new Vector2(b.X, b.Y) * MapDistanceScale);
    }

    // Halving can leave the last seen place past the end; the search starts over then.
    private int FindSelected()
    {
        Vector3 selected = Selected;
        int start = selectedIndex <= points.Count ? selectedIndex : 0;
        return points.FindIndex(start, p => p == selected);
    }
}
