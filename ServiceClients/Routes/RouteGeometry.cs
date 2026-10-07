using Contracts;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Routes;

/// <summary>Lines across the ground: lengths, points along a line, a drawn line made sparse. Heights are carried, not measured.</summary>
public static class RouteGeometry
{
    public static float Length(IReadOnlyList<Point3> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    public static float Distance(Point3 a, Point3 b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The middle of <paramref name="points"/>, with no height.</summary>
    public static Point3 Middle(IReadOnlyCollection<Point3> points)
    {
        return new Point3(points.Average(p => p.X), points.Average(p => p.Y), 0);
    }

    /// <summary>
    /// Points evenly along the line from <paramref name="from"/> to <paramref name="to"/>, at most
    /// <paramref name="spacing"/> apart, the ends left out. Their heights run between the ends', or are 0
    /// (unknown) where either end's is.
    /// </summary>
    public static IEnumerable<Point3> Between(Point3 from, Point3 to, float spacing)
    {
        int pieces = (int)MathF.Ceiling(Distance(from, to) / spacing);
        bool heights = from.Z != 0 && to.Z != 0;
        for (int i = 1; i < pieces; i++)
        {
            float t = (float)i / pieces;
            yield return new Point3(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t), heights ? from.Z + ((to.Z - from.Z) * t) : 0);
        }
    }

    /// <summary>How far <paramref name="point"/> is from the segment <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static float FromSegment(Point3 point, Point3 a, Point3 b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float length = (dx * dx) + (dy * dy);
        float t = length > 0 ? Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / length, 0, 1) : 0;
        return Distance(point, new Point3(a.X + (t * dx), a.Y + (t * dy), 0));
    }

    /// <summary>
    /// The points of a drawn line that keep every other within <paramref name="tolerance"/> yards of the
    /// line through them (Douglas-Peucker); the first and last are kept.
    /// </summary>
    public static Point3[] Simplify(IReadOnlyList<Point3> points, float tolerance)
    {
        if (points.Count < 3)
        {
            return [.. points];
        }

        bool[] keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        Split(0, points.Count - 1);
        return [.. points.Where((_, i) => keep[i])];

        void Split(int first, int last)
        {
            int farthest = -1;
            float deviation = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float d = FromSegment(points[i], points[first], points[last]);
                if (d > deviation)
                {
                    deviation = d;
                    farthest = i;
                }
            }

            if (farthest < 0)
            {
                return;
            }

            keep[farthest] = true;
            Split(first, farthest);
            Split(farthest, last);
        }
    }
}
