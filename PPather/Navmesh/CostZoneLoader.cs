#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using IOPath = System.IO.Path;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using PPather.Graph;

namespace PPather.Navmesh;

/// <summary>
/// Reads the authored road and danger-zone files for a continent.
///
/// Layout (as produced by the Road/DangerZone controllers):
///   {DataConfig.Road}/{continent}/{uiMapId}.json            - RoadData
///   {DataConfig.Road}/{continent}/dangerzone/{uiMapId}.json - DangerZoneData
/// </summary>
public static class CostZoneLoader
{
    public const string DangerZoneFolder = "dangerzone";

    /// <summary>
    /// Loads every zone file for a continent, and reports whether *all* of them
    /// were readable and valid.
    ///
    /// The distinction matters for hot reload. These files are hand-editable and
    /// the watcher can fire while one is still being written, so a partial read
    /// is normal rather than exceptional - and silently applying it would drop
    /// the user's roads on the floor. Callers that have a working set already
    /// should keep it unless this returns true.
    /// </summary>
    public static bool TryLoad(ILogger logger, string roadRoot, string continent,
        out CostZones zones, float roadFactor = CostZones.DefaultRoadFactor,
        float roadCoreHalfWidth = CostZones.DefaultRoadCoreHalfWidth)
    {
        zones = Load(logger, roadRoot, continent, roadFactor, roadCoreHalfWidth, out bool complete);
        return complete;
    }

    public static CostZones Load(ILogger logger, string roadRoot, string continent,
        float roadFactor = CostZones.DefaultRoadFactor,
        float roadCoreHalfWidth = CostZones.DefaultRoadCoreHalfWidth) =>
        Load(logger, roadRoot, continent, roadFactor, roadCoreHalfWidth, out _);

    private static CostZones Load(ILogger logger, string roadRoot, string continent,
        float roadFactor, float roadCoreHalfWidth, out bool complete)
    {
        complete = true;

        string continentDir = IOPath.Join(roadRoot, continent);
        if (!Directory.Exists(continentDir))
        {
            return CostZones.Empty;
        }

        List<RoadData> roads = [];
        List<DangerZoneData> dangerZones = [];

        foreach (string file in Directory.GetFiles(continentDir, "*.json"))
        {
            if (TryRead(logger, file, out RoadData? road) && road != null && Validate(logger, file, road))
            {
                roads.Add(road);
            }
            else
            {
                complete = false;
            }
        }

        string dangerDir = IOPath.Join(continentDir, DangerZoneFolder);
        if (Directory.Exists(dangerDir))
        {
            foreach (string file in Directory.GetFiles(dangerDir, "*.json"))
            {
                if (TryRead(logger, file, out DangerZoneData? danger) && danger != null && Validate(logger, file, danger))
                {
                    dangerZones.Add(danger);
                }
                else
                {
                    complete = false;
                }
            }
        }

        CostZones zones = CostZones.Build(roads, dangerZones, roadFactor, roadCoreHalfWidth);

        if (logger.IsEnabled(LogLevel.Information) && !zones.IsEmpty)
        {
            logger.LogInformation(
                "Cost zones for {Continent}: {Roads} road files, {Danger} danger files -> {CostChunks} priced chunks, {BlockedChunks} blocked",
                continent, roads.Count, dangerZones.Count, zones.CostChunkCount, zones.BlockedChunkCount);
        }

        return zones;
    }

    // --- Validation -----------------------------------------------------
    //
    // These files are hand-editable, so "it parsed as JSON" is not enough. Two
    // classes of bad data are actively dangerous rather than merely wrong:
    //
    //   NaN / Infinity - the penalty clamp in CostZones uses comparisons, and
    //   every comparison against NaN is false, so a NaN sails through the clamp
    //   and turns a path's cost into NaN. Coordinates behave the same way.
    //
    //   Absurd extents - CostZones rasterizes a shape's bounding box into 33yd
    //   chunks. A radius of 1e9 is 10^15 chunk iterations, which hangs whichever
    //   thread is reloading rather than failing.

    /// <summary>Widest coordinate the WoW map can hold, plus slack.</summary>
    private const float CoordLimit = 40000f;

    /// <summary>Largest authored shape we will rasterize, in yards.</summary>
    private const float ExtentLimit = 5000f;

    /// <summary>Road width is a chunk multiplier: 64 chunks is over 2000yd.</summary>
    private const int MaxRoadWidthChunks = 64;

    private const int MaxPointsPerSegment = 50_000;

    private static bool IsSaneCoord(float v) => float.IsFinite(v) && MathF.Abs(v) <= CoordLimit;

    private static bool Validate(ILogger logger, string file, RoadData data)
    {
        string? problem = Problem(data);
        if (problem != null)
        {
            logger.LogWarning("Cost zone file {File} rejected: {Problem}.", file, problem);
        }
        return problem == null;
    }

    private static bool Validate(ILogger logger, string file, DangerZoneData data)
    {
        string? problem = Problem(data);
        if (problem != null)
        {
            logger.LogWarning("Cost zone file {File} rejected: {Problem}.", file, problem);
        }
        return problem == null;
    }

    /// <summary>
    /// Why <paramref name="data"/> would be rejected when loaded, or null when it is valid.
    /// A writer checks with this before saving, so a bad edit is refused at once instead of
    /// being skipped later with only a warning.
    /// </summary>
    public static string? Problem(RoadData data)
    {
        if (data.Roads == null)
        {
            return "Roads array is null";
        }

        foreach (RoadSegment segment in data.Roads)
        {
            if (segment.Width <= 0 || segment.Width > MaxRoadWidthChunks)
            {
                return $"road '{segment.Name}' has width {segment.Width}, expected 1..{MaxRoadWidthChunks}";
            }

            if (segment.Points == null || segment.Points.Length == 0 || segment.Points.Length > MaxPointsPerSegment)
            {
                return $"road '{segment.Name}' has {segment.Points?.Length ?? 0} points, expected 1..{MaxPointsPerSegment}";
            }

            foreach (Vector2 p in segment.Points)
            {
                if (!IsSaneCoord(p.X) || !IsSaneCoord(p.Y))
                {
                    return $"road '{segment.Name}' has an out-of-range point ({p.X}, {p.Y})";
                }
            }
        }

        return null;
    }

    /// <inheritdoc cref="Problem(RoadData)"/>
    public static string? Problem(DangerZoneData data)
    {
        if (data.Circles == null || data.Rectangles == null)
        {
            return "Circles or Rectangles array is null";
        }

        foreach (CircleDangerZone c in data.Circles)
        {
            if (!IsSaneCoord(c.CenterX) || !IsSaneCoord(c.CenterY))
            {
                return $"circle '{c.Name}' centre ({c.CenterX}, {c.CenterY}) is out of range";
            }

            if (!float.IsFinite(c.Radius) || c.Radius <= 0f || c.Radius > ExtentLimit)
            {
                return $"circle '{c.Name}' radius {c.Radius}, expected 0..{ExtentLimit}";
            }

            if (!float.IsFinite(c.Penalty))
            {
                return $"circle '{c.Name}' penalty is not a finite number";
            }
        }

        foreach (RectangleDangerZone r in data.Rectangles)
        {
            if (!IsSaneCoord(r.MinX) || !IsSaneCoord(r.MinY) || !IsSaneCoord(r.MaxX) || !IsSaneCoord(r.MaxY))
            {
                return $"rectangle '{r.Name}' has out-of-range bounds";
            }

            if (r.MaxX < r.MinX || r.MaxY < r.MinY)
            {
                return $"rectangle '{r.Name}' has inverted bounds";
            }

            if (r.MaxX - r.MinX > ExtentLimit || r.MaxY - r.MinY > ExtentLimit)
            {
                return $"rectangle '{r.Name}' is larger than {ExtentLimit}yd a side";
            }

            if (!float.IsFinite(r.Penalty))
            {
                return $"rectangle '{r.Name}' penalty is not a finite number";
            }
        }

        return null;
    }

    private static bool TryRead<T>(ILogger logger, string file, out T? value) where T : class
    {
        try
        {
            value = JsonConvert.DeserializeObject<T>(File.ReadAllText(file));
            return value != null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading cost zone file {File}", file);
            value = null;
            return false;
        }
    }
}
