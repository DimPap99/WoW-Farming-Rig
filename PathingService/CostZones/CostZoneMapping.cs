using Contracts;
using Contracts.Pathing;

using PPather.Graph;

using System;
using System.Numerics;

namespace PathingService.CostZones;

/// <summary>
/// Wire shapes to the engine's models and back. The engine models stay the file format (they
/// are what <see cref="PPather.Navmesh.CostZoneLoader"/> reads), and they carry engine
/// behaviour the wire has no use for. A null array is passed through as null, so the loader's
/// own validation reports it.
/// </summary>
public static class CostZoneMapping
{
    public static RoadZones ToContract(RoadData data)
    {
        RoadLine[] roads = new RoadLine[data.Roads?.Length ?? 0];
        for (int i = 0; i < roads.Length; i++)
        {
            RoadSegment segment = data.Roads![i];
            Point2[] points = new Point2[segment.Points?.Length ?? 0];
            for (int j = 0; j < points.Length; j++)
            {
                points[j] = new Point2(segment.Points![j].X, segment.Points[j].Y);
            }
            roads[i] = new RoadLine(segment.Name, segment.Width, points);
        }

        return new RoadZones(roads);
    }

    public static RoadData ToModel(RoadZones zones)
    {
        if (zones.Roads is null)
        {
            return new RoadData { Roads = null! };
        }

        RoadSegment[] roads = new RoadSegment[zones.Roads.Length];
        for (int i = 0; i < roads.Length; i++)
        {
            RoadLine line = zones.Roads[i];
            Vector2[]? points = null;
            if (line.Points is not null)
            {
                points = new Vector2[line.Points.Length];
                for (int j = 0; j < points.Length; j++)
                {
                    points[j] = new Vector2(line.Points[j].X, line.Points[j].Y);
                }
            }
            roads[i] = new RoadSegment(line.Name, line.Width, points!);
        }

        return new RoadData { Roads = roads };
    }

    public static DangerZones ToContract(DangerZoneData data)
    {
        DangerCircle[] circles = new DangerCircle[data.Circles?.Length ?? 0];
        for (int i = 0; i < circles.Length; i++)
        {
            CircleDangerZone c = data.Circles![i];
            circles[i] = new DangerCircle(c.Name, c.CenterX, c.CenterY, c.Radius, c.Penalty, (ZoneMode)c.Mode);
        }

        DangerRectangle[] rectangles = new DangerRectangle[data.Rectangles?.Length ?? 0];
        for (int i = 0; i < rectangles.Length; i++)
        {
            RectangleDangerZone r = data.Rectangles![i];
            rectangles[i] = new DangerRectangle(r.Name, r.MinX, r.MinY, r.MaxX, r.MaxY, r.Penalty, (ZoneMode)r.Mode);
        }

        return new DangerZones(circles, rectangles);
    }

    public static DangerZoneData ToModel(DangerZones zones)
    {
        CircleDangerZone[]? circles = null;
        if (zones.Circles is not null)
        {
            circles = new CircleDangerZone[zones.Circles.Length];
            for (int i = 0; i < circles.Length; i++)
            {
                DangerCircle c = zones.Circles[i];
                circles[i] = new CircleDangerZone(c.Name, c.CenterX, c.CenterY, c.Radius, c.Penalty)
                {
                    Mode = (DangerZoneMode)c.Mode
                };
            }
        }

        RectangleDangerZone[]? rectangles = null;
        if (zones.Rectangles is not null)
        {
            rectangles = new RectangleDangerZone[zones.Rectangles.Length];
            for (int i = 0; i < rectangles.Length; i++)
            {
                DangerRectangle r = zones.Rectangles[i];
                rectangles[i] = new RectangleDangerZone(r.Name, r.MinX, r.MinY, r.MaxX, r.MaxY, r.Penalty)
                {
                    Mode = (DangerZoneMode)r.Mode
                };
            }
        }

        return new DangerZoneData { Circles = circles!, Rectangles = rectangles! };
    }

    /// <summary>A mode the engine does not know, which a numeric enum on the wire could carry; null when all are known.</summary>
    public static string? UnknownMode(DangerZones zones)
    {
        foreach (DangerCircle c in zones.Circles ?? [])
        {
            if (!Enum.IsDefined(c.Mode))
            {
                return $"circle '{c.Name}' has an unknown mode {(int)c.Mode}";
            }
        }

        foreach (DangerRectangle r in zones.Rectangles ?? [])
        {
            if (!Enum.IsDefined(r.Mode))
            {
                return $"rectangle '{r.Name}' has an unknown mode {(int)r.Mode}";
            }
        }

        return null;
    }
}
