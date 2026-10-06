#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
        return TryLoad(logger, roadRoot, continent, out zones, out _, roadFactor, roadCoreHalfWidth);
    }

    /// <summary>
    /// <see cref="TryLoad(ILogger, string, string, out CostZones, float, float)"/>, saying what
    /// failed: the first file that was unreadable or invalid, relative to the road root, and why.
    /// </summary>
    public static bool TryLoad(ILogger logger, string roadRoot, string continent,
        out CostZones zones, out string? failure, float roadFactor = CostZones.DefaultRoadFactor,
        float roadCoreHalfWidth = CostZones.DefaultRoadCoreHalfWidth)
    {
        zones = Load(logger, roadRoot, continent, roadFactor, roadCoreHalfWidth, out failure);
        return failure is null;
    }

    public static CostZones Load(ILogger logger, string roadRoot, string continent,
        float roadFactor = CostZones.DefaultRoadFactor,
        float roadCoreHalfWidth = CostZones.DefaultRoadCoreHalfWidth) =>
        Load(logger, roadRoot, continent, roadFactor, roadCoreHalfWidth, out _);

    private static CostZones Load(ILogger logger, string roadRoot, string continent,
        float roadFactor, float roadCoreHalfWidth, out string? failure)
    {
        failure = null;

        string continentDir = IOPath.Join(roadRoot, continent);
        if (!Directory.Exists(continentDir))
        {
            return CostZones.Empty;
        }

        List<RoadData> roads = [];
        List<DangerZoneData> dangerZones = [];

        foreach (string file in Directory.GetFiles(continentDir, "*.json"))
        {
            string? problem = Read(logger, file, out RoadData? road) ?? Rejected(logger, file, Problem(road!, roadCoreHalfWidth));
            if (problem is null)
            {
                roads.Add(road!);
            }
            else
            {
                failure ??= $"{IOPath.GetRelativePath(roadRoot, file)}: {problem}";
            }
        }

        string dangerDir = IOPath.Join(continentDir, DangerZoneFolder);
        if (Directory.Exists(dangerDir))
        {
            foreach (string file in Directory.GetFiles(dangerDir, "*.json"))
            {
                string? problem = Read(logger, file, out DangerZoneData? danger) ?? Rejected(logger, file, Problem(danger!));
                if (problem is null)
                {
                    dangerZones.Add(danger!);
                }
                else
                {
                    failure ??= $"{IOPath.GetRelativePath(roadRoot, file)}: {problem}";
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
    //   thread is reloading rather than failing. Sane shapes add up the same way:
    //   a road's every leg walks its own box, so the file's total is capped too.

    /// <summary>
    /// The most cells one file may make <see cref="CostZones.Build"/> visit, counted as it walks
    /// them (<see cref="CostZones.RasterCells(in RoadSegment, float)"/>): a fraction of a second.
    /// The largest file in the repository takes 31,525 and all 67 together 592,395, while a road
    /// zig-zagging corner to corner would take about 1.15e12 and hang every load of its continent,
    /// in the service and on the bot.
    /// </summary>
    public const long MaxRasterCells = 2_000_000;

    /// <summary>Widest coordinate the WoW map can hold, plus slack.</summary>
    private const float CoordLimit = 40000f;

    /// <summary>Largest authored shape we will rasterize, in yards.</summary>
    private const float ExtentLimit = 5000f;

    /// <summary>Road width is a chunk multiplier: 64 chunks is over 2000yd.</summary>
    private const int MaxRoadWidthChunks = 64;

    private const int MaxPointsPerSegment = 50_000;

    private static bool IsSaneCoord(float v) => float.IsFinite(v) && MathF.Abs(v) <= CoordLimit;

    // Logs why a file that read is not used; the problem passes through.
    private static string? Rejected(ILogger logger, string file, string? problem)
    {
        if (problem != null)
        {
            logger.LogWarning("Cost zone file {File} rejected: {Problem}.", file, problem);
        }
        return problem;
    }

    /// <summary>
    /// Why <paramref name="data"/> would be rejected when loaded, or null when it is valid.
    /// A writer checks with this before saving, so a bad edit is refused at once instead of
    /// being skipped later with only a warning.
    /// </summary>
    /// <param name="data">The file's roads.</param>
    /// <param name="roadCoreHalfWidth">The loader's road core, which sets how far a narrow road reaches.</param>
    public static string? Problem(RoadData data, float roadCoreHalfWidth = CostZones.DefaultRoadCoreHalfWidth)
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

        // Counted last: the count needs sane widths and coordinates.
        double cells = 0;
        double mostCells = 0;
        string most = string.Empty;
        foreach (RoadSegment segment in data.Roads)
        {
            double roadCells = CostZones.RasterCells(segment, roadCoreHalfWidth);
            cells += roadCells;
            if (roadCells > mostCells)
            {
                mostCells = roadCells;
                most = segment.Name;
            }
        }

        return cells > MaxRasterCells
            ? TooMuchWork("roads", cells, $"road '{most}'", mostCells) +
              ": a long diagonal leg costs its whole bounding box, so add points along it"
            : null;
    }

    private static string TooMuchWork(string what, double cells, string most, double mostCells)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"the {what} would take {cells:N0} steps to load, more than the {MaxRasterCells:N0} allowed; the most is {most} with {mostCells:N0}");
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

        // Each shape is capped, but not how many a file holds.
        double cells = 0;
        double mostCells = 0;
        string most = string.Empty;
        foreach (CircleDangerZone c in data.Circles)
        {
            double shapeCells = CostZones.RasterCells(c);
            cells += shapeCells;
            if (shapeCells > mostCells)
            {
                mostCells = shapeCells;
                most = $"circle '{c.Name}'";
            }
        }

        foreach (RectangleDangerZone r in data.Rectangles)
        {
            double shapeCells = CostZones.RasterCells(r);
            cells += shapeCells;
            if (shapeCells > mostCells)
            {
                mostCells = shapeCells;
                most = $"rectangle '{r.Name}'";
            }
        }

        return cells > MaxRasterCells ? TooMuchWork("danger zones", cells, most, mostCells) : null;
    }

    // Why the file could not be read, or null when value holds it.
    private static string? Read<T>(ILogger logger, string file, out T? value) where T : class
    {
        try
        {
            value = JsonConvert.DeserializeObject<T>(File.ReadAllText(file));
            return value != null ? null : "it is empty";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading cost zone file {File}", file);
            value = null;
            return $"it could not be read: {ex.Message}";
        }
    }
}
