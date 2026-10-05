using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Contracts;
using Contracts.Pathing;

using Serilog;

using ServiceClients.Pathing;

namespace Benchmarks;

/// <summary>
/// End-to-end benchmark of the Pathing Service's route endpoint. For each route it measures:
/// - the first query of the run vs the repeats (the first is cold only on a freshly started
///   service: a lane keeps the tiles it loaded)
/// - path quality: total length, corner count, sharpest corner, endpoint reached
/// - consistency across multiple runs
///
/// Usage: dotnet run --project Benchmarks -- --pather-benchmark [BaseUrl] [Iterations] [Label]
/// Example: dotnet run --project Benchmarks -- --pather-benchmark http://localhost:5010 4 navmesh
/// </summary>
public class PathingServiceBenchmark
{
    // A path whose last point is farther than this from the requested target did not
    // actually arrive.
    private const float ReachedDistance = 5f;

    // Heading change (degrees, XY plane) above which a waypoint counts as a corner.
    private const float CornerAngleDeg = 25f;

    private static RouteRequest World(int uiMap, float x1, float y1, float z1, float x2, float y2, float z2)
    {
        return new RouteRequest(null, uiMap, CoordinateSpace.World, new Point3(x1, y1, z1), new Point3(x2, y2, z2));
    }

    private static RouteRequest Map(int uiMap, float x1, float y1, float x2, float y2)
    {
        return new RouteRequest(null, uiMap, CoordinateSpace.Map, new Point3(x1, y1, 0), new Point3(x2, y2, 0));
    }

    // World routes name a zone only to pick the continent.
    private static readonly List<PathTest> TestRoutes =
    [
        new("Elwynn vendor to path", World(1429, -8898.32f, -117.35608f, 81.840546f, -8779.58f, -106.12012f, 0)),
        new("Z Elwynn vendor to path", World(1429, -8898.32f, -117.35608f, 0, -8779.58f, -106.12012f, 0)),
        new("Z Coldridge to 5_gnome", World(1426, -6120.8084f, 542.90857f, 0, -5880.7373f, -116.15503f, 0)),

        new("Redridge Grave to North", Map(1433, 30, 60, 54.3f, 43.1f)),

        new("Stormwind city to trainer", World(1429, -8915.693f, -132.94092f, 102.475426f, -8688.56f, 325.76f, 109.52f)),

        new("Alterac Mountains Horde grave", World(1416, -17.51f, -986.82f, 55.83f, 305.33337f, -364.6667f, 168.28902f)),

        new("Z Durotar Sen'jin village to grind", World(1411, -779.3728f, -4926.2754f, 0, -575.14014f, -4298.2f, 0)),
        new("Durotar Sen'jin village to grind", World(1411, -779.3728f, -4926.2754f, 22.3297f, -575.14014f, -4298.2f, 0)),

        new("Z Orgrimmar to vendor", World(1454, 1618.5112f, -4433.742f, 0, 1633.98f, -4439.37f, 15.51f)),

        new("Z Teldrassil to vendor", World(1438, 10477.419f, 659.36426f, 1326.2318f, 10442.9f, 783.989f, 1337.37f)),

        new("Tanaris FP to ZF", Map(1446, 51.0f, 29.3f, 38.7f, 20.1f)),

        new("Silithus Inn to AQ", Map(1451, 51.4f, 37.8f, 29, 92)),

        new("Kalimdor Search Barrens", World(1413, -896, -3770, 11, -441, -2596, 96)),

        new("Azeroth Dun morogh 1", World(1426, -6238.128f, 139.40344f, 430.9192f, -5884.185f, -118.66675f, 364.64783f)),
        new("Dun morogh Vendor to grind", World(1426, -6101.417f, 390.9181f, 395.626f, -6154.18f, 609.84973f, 395.626f)),
        new("Azeroth Dun morogh 2", World(1426, -5609.00f, -479.00f, 397.49f, -5884.185f, -118.66675f, 364.64783f)),
        new("Z Dun morogh Vendor Rybrad Coldbank", World(1426, -6200.1924f, 700.5774f, 384.6462f, -6103.1836f, 393.5332f, 0)),
        new("Azeroth Dun morogh Vendor Rybrad Coldbank", World(1426, -6200.1924f, 700.5774f, 384.6462f, -6103.1836f, 393.5332f, 396.0979f)),

        new("Z Azshara issue no result", World(1447, 2293.7908f, -6636.0283f, 120.13607f, 2309.54f, -6666.62f, 0)),
        new("Z Honor hold issue no result", World(1944, -732.031f, 2448.2805f, 58.940506f, -755.79004f, 2491.16f, 0)),
        new("Duskwood issue", Map(1431, 43.097f, 18.38f, 45.34f, 16.438f)),

        new("Loch Modan building Yanni Stoutheart", Map(1432, 35.2f, 46.9f, 34.8f, 48.6f)),
        new("Loch Modan building innkeeper", Map(1432, 35.2f, 46.9f, 35.5f, 48.5f)),
        new("Loch Modan building Vidra Heartstove", Map(1432, 35.2f, 46.9f, 34.8f, 49.1f)),
        new("Dun morogh building Grundel Harkin", Map(1426, 28.7f, 70.1f, 28.8f, 67.9f)),
        new("Dun morogh building Grundel Harkin reverse", Map(1426, 28.8f, 67.9f, 28.7f, 70.1f)),
        new("Dun morogh Coldridge pass - unable to find", Map(1426, 33.90f, 71.86f, 38.94f, 61.0f)),
        new("Dun morogh Coldridge pass", Map(1426, 33.76f, 71.91f, 39.0f, 61.13f)),

        // Cross-zone long haul
        new("Cross-zone Elwynn to Redridge", World(1429, -9170.5f, 355.4f, 81.05f, -9230.0f, -2211.0f, 0)),

        // Other zones
        new("Hinterlands 1 water issue", Map(1425, 82.2771f, 48.698803f, 81.65922f, 49.87935f)),
        new("Hinterlands 2 water issue", Map(1425, 80.7258f, 64.2714f, 78.321304f, 64.0448f)),
        new("Azshara 1", Map(1447, 67.25f, 82.88f, 66.68f, 90.23f)),

        // TBC
        new("Ammen Vale", Map(1943, 81.05f, 45.33f, 78.92f, 44.14f)),
        new("Hellfire 1", Map(1944, 60.0762f, 43.4372f, 61.1473f, 39.955284f)),
        new("Hellfire 2", Map(1944, 60.0762f, 43.4372f, 61.14f, 38.38f)),

        // Wotlk
        new("Zul'Drak stairs", Map(121, 40.4058f, 63.024403f, 40.3816f, 64.259705f)),
        new("Zul'Drak stairs reverse", Map(121, 40.3816f, 64.259705f, 40.4058f, 63.024403f)),
    ];

    private sealed record PathTest(string Name, RouteRequest Request);

    public sealed class BenchmarkResult
    {
        public string TestName { get; set; } = string.Empty;
        public double ColdMs { get; set; } = -1;
        public double[] WarmMs { get; set; } = [];
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int PointCount { get; set; }
        public double PathLengthYd { get; set; }
        public int CornerCount { get; set; }
        public double MaxCornerDeg { get; set; }

        // true/false = endpoint check performed (world-coord routes);
        // null = not applicable (map-coord routes have no world-space target).
        public bool? Reached { get; set; }

        public bool HasWarm => Success && WarmMs.Length > 0;
        public double WarmMinMs => HasWarm ? WarmMs.Min() : -1;
        public double WarmMaxMs => HasWarm ? WarmMs.Max() : -1;
        public double WarmAvgMs => HasWarm ? WarmMs.Average() : -1;
        public double WarmMedianMs => HasWarm ? Median(WarmMs.OrderBy(x => x).ToList()) : -1;
    }

    public static async Task<List<BenchmarkResult>> RunBenchmark(string baseUrl, int iterations = 3,
        ILogger? logger = null, string label = "navmesh", string outputDir = "local/benchmark_results")
    {
        logger ??= Log.Logger;

        int nameWidth = TestRoutes.Max(t => t.Name.Length) + 2;
        int separatorWidth = nameWidth + 96;

        string progressOkFmt = $"[{{0:D2}}/{{1:D2}}] {{2,-{nameWidth}}} ... OK Cold: {{3,8:F1}}ms | Warm Avg: {{4,8:F1}}ms | Pts: {{5,5}} | Len: {{6,7:F1}}yd | Crn: {{7,3}} | {{8}}";
        string progressFailFmt = $"[{{0:D2}}/{{1:D2}}] {{2,-{nameWidth}}} ... FAIL {{3}}";
        string tableHeaderFmt = $"{{0,-{nameWidth}}} | {{1,8}} | {{2,8}} | {{3,8}} | {{4,8}} | {{5,8}} | {{6,5}} | {{7,8}} | {{8,4}} | {{9,7}} | {{10,7}}";
        string tableRowFmt = $"{{0,-{nameWidth}}} | {{1,8:F1}} | {{2,8:F1}} | {{3,8:F1}} | {{4,8:F1}} | {{5,8:F1}} | {{6,5}} | {{7,8:F1}} | {{8,4}} | {{9,7:F1}} | {{10,7}}";

        logger.Information("=== Pathing Service End-to-End Benchmark Suite ===\n");
        logger.Information(string.Format("Base URL: {0}", baseUrl));
        logger.Information(string.Format("Label: {0}", label));
        logger.Information(string.Format("Test Routes: {0}", TestRoutes.Count));
        logger.Information(string.Format("Iterations per route: {0} (1 first + {1} repeats)", iterations, Math.Max(0, iterations - 1)));
        logger.Information("");

        using HttpClient http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(2) };
        PathingClient client = new(http);
        List<BenchmarkResult> results = [];

        long begin = Stopwatch.GetTimestamp();

        for (int i = 0; i < TestRoutes.Count; i++)
        {
            PathTest test = TestRoutes[i];

            BenchmarkResult result = new()
            {
                TestName = test.Name
            };

            try
            {
                List<double> warm = new(Math.Max(0, iterations - 1));

                for (int iter = 0; iter < iterations; iter++)
                {
                    long startTime = Stopwatch.GetTimestamp();

                    RouteResponse response = await client.FindRouteAsync(test.Request);

                    double elapsedMs = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;

                    if (response.Status != RouteStatus.Found)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"{response.Status}: {response.Detail}";
                        break;
                    }

                    if (iter == 0)
                    {
                        result.ColdMs = elapsedMs;
                        result.PointCount = response.Points.Length;
                        AnalyzePath(response.Points, test.Request, result);
                    }
                    else
                    {
                        warm.Add(elapsedMs);
                    }
                }

                if (result.ErrorMessage == null)
                {
                    result.Success = true;
                    result.WarmMs = [.. warm];
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            if (result.Success)
            {
                logger.Information(string.Format(progressOkFmt,
                    i + 1, TestRoutes.Count, test.Name, result.ColdMs,
                    result.HasWarm ? result.WarmAvgMs : double.NaN,
                    result.PointCount, result.PathLengthYd, result.CornerCount,
                    FormatReached(result.Reached)));
            }
            else
            {
                logger.Information(string.Format(progressFailFmt,
                    i + 1, TestRoutes.Count, test.Name, result.ErrorMessage));
            }

            results.Add(result);
        }

        double totalSeconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;

        logger.Information("");
        logger.Information("=== Summary ===\n");
        logger.Information(string.Format("Total elapsed time: {0:F2}s", totalSeconds));

        List<BenchmarkResult> successfulResults = results.Where(r => r.Success).ToList();
        List<BenchmarkResult> failedResults = results.Where(r => !r.Success).ToList();
        List<BenchmarkResult> notReached = successfulResults.Where(r => r.Reached == false).ToList();

        logger.Information(string.Format("Successful tests: {0}/{1}", successfulResults.Count, results.Count));

        if (failedResults.Count > 0)
        {
            logger.Information(string.Format("Failed tests: {0}", failedResults.Count));
            foreach (BenchmarkResult failed in failedResults)
            {
                logger.Information(string.Format("  - {0}: {1}", failed.TestName, failed.ErrorMessage));
            }
        }

        if (notReached.Count > 0)
        {
            logger.Information(string.Format("Endpoint NOT reached (HTTP OK but last point > {0}yd from target): {1}", ReachedDistance, notReached.Count));
            foreach (BenchmarkResult miss in notReached)
            {
                logger.Information(string.Format("  - {0}: {1} points", miss.TestName, miss.PointCount));
            }
        }

        List<double> coldTimes = successfulResults.Where(r => r.ColdMs >= 0).Select(r => r.ColdMs).OrderBy(t => t).ToList();
        List<double> warmTimes = successfulResults.SelectMany(r => r.WarmMs).OrderBy(t => t).ToList();

        if (coldTimes.Count > 0)
        {
            logger.Information("");
            logger.Information(string.Format("First-query Statistics ({0} measurements - cold only on a freshly started service):", coldTimes.Count));
            LogStats(logger, coldTimes);
        }

        if (warmTimes.Count > 0)
        {
            logger.Information("");
            logger.Information(string.Format("Warm Statistics ({0} measurements):", warmTimes.Count));
            LogStats(logger, warmTimes);
        }

        logger.Information("\n=== Detailed Results ===\n");
        logger.Information(string.Format(tableHeaderFmt,
            "Test Name", "Cold", "WarmMin", "WarmAvg", "WarmMed", "WarmMax", "Pts", "LenYd", "Crn", "MaxCrn", "Reached"));
        logger.Information(new string('-', separatorWidth));

        foreach (BenchmarkResult result in successfulResults.OrderBy(r => r.HasWarm ? r.WarmAvgMs : r.ColdMs))
        {
            logger.Information(string.Format(tableRowFmt,
                result.TestName, result.ColdMs, result.WarmMinMs, result.WarmAvgMs,
                result.WarmMedianMs, result.WarmMaxMs, result.PointCount,
                result.PathLengthYd, result.CornerCount, result.MaxCornerDeg,
                FormatReached(result.Reached)));
        }

        string reportPath = WriteMarkdownReport(outputDir, label, baseUrl, iterations,
            totalSeconds, results, coldTimes, warmTimes, notReached);
        logger.Information(string.Format("\nReport written: {0}", reportPath));

        logger.Information("\nBenchmark complete!");

        return results;
    }

    private static void LogStats(ILogger logger, List<double> sortedTimes)
    {
        logger.Information(string.Format("  Min:    {0:F1}ms", sortedTimes[0]));
        logger.Information(string.Format("  Max:    {0:F1}ms", sortedTimes[^1]));
        logger.Information(string.Format("  Avg:    {0:F1}ms", sortedTimes.Average()));
        logger.Information(string.Format("  Median: {0:F1}ms", Median(sortedTimes)));
        logger.Information(string.Format("  P95:    {0:F1}ms", Percentile(sortedTimes, 95)));
        logger.Information(string.Format("  P99:    {0:F1}ms", Percentile(sortedTimes, 99)));
    }

    private static string FormatReached(bool? reached)
    {
        return reached switch
        {
            true => "yes",
            false => "NO",
            null => "n/a",
        };
    }

    /// <summary>
    /// Computes path length, corner metrics and - for world-coordinate routes -
    /// whether the path actually arrives at the requested target.
    /// </summary>
    private static void AnalyzePath(Point3[] points, RouteRequest request, BenchmarkResult result)
    {
        double length = 0;
        int corners = 0;
        double maxCornerDeg = 0;

        for (int i = 1; i < points.Length; i++)
        {
            float dx = points[i].X - points[i - 1].X;
            float dy = points[i].Y - points[i - 1].Y;
            float dz = points[i].Z - points[i - 1].Z;
            length += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        // Corner detection on the XY plane - Z is height-guessed and noisy.
        double prevHeading = double.NaN;
        for (int i = 1; i < points.Length; i++)
        {
            float dx = points[i].X - points[i - 1].X;
            float dy = points[i].Y - points[i - 1].Y;

            if ((dx * dx) + (dy * dy) < 0.0001f)
                continue;

            double heading = Math.Atan2(dy, dx);

            if (!double.IsNaN(prevHeading))
            {
                double deltaDeg = Math.Abs(NormalizeAngle(heading - prevHeading)) * (180.0 / Math.PI);
                if (deltaDeg > CornerAngleDeg)
                    corners++;
                if (deltaDeg > maxCornerDeg)
                    maxCornerDeg = deltaDeg;
            }

            prevHeading = heading;
        }

        result.PathLengthYd = length;
        result.CornerCount = corners;
        result.MaxCornerDeg = maxCornerDeg;
        result.Reached = ComputeReached(points, request);
    }

    private static double NormalizeAngle(double radians)
    {
        while (radians > Math.PI) radians -= 2 * Math.PI;
        while (radians < -Math.PI) radians += 2 * Math.PI;
        return radians;
    }

    /// <summary>
    /// World-coordinate routes are compared against their target on the XY plane (Z is
    /// resolved by the service). Map-coordinate routes answer in map units, where yards have
    /// no fixed size: null (not applicable).
    /// </summary>
    private static bool? ComputeReached(Point3[] points, RouteRequest request)
    {
        if (request.Space != CoordinateSpace.World)
            return null;

        if (points.Length == 0)
            return false;

        Point3 last = points[^1];
        float dx = last.X - request.To.X;
        float dy = last.Y - request.To.Y;
        return ((dx * dx) + (dy * dy)) <= (ReachedDistance * ReachedDistance);
    }

    private static string WriteMarkdownReport(string outputDir, string label,
        string baseUrl, int iterations, double totalSeconds,
        List<BenchmarkResult> results, List<double> coldTimes, List<double> warmTimes,
        List<BenchmarkResult> notReached)
    {
        Directory.CreateDirectory(outputDir);

        string fileName = string.Create(CultureInfo.InvariantCulture,
            $"pathing_{Sanitize(label)}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        string path = Path.Combine(outputDir, fileName);

        StringBuilder sb = new();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"# Pathing Service Benchmark - {label}"));
        sb.AppendLine();
        sb.AppendLine("| Setting | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Date | {DateTime.Now:yyyy-MM-dd HH:mm:ss} |"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Base URL | {baseUrl} |"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Routes | {results.Count} |"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Iterations | {iterations} (1 first + {Math.Max(0, iterations - 1)} repeats) |"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Total duration | {totalSeconds:F1}s |"));
        sb.AppendLine();

        AppendStatsSection(sb, "First query (cold only on a freshly started service)", coldTimes);
        AppendStatsSection(sb, "Repeats", warmTimes);

        List<BenchmarkResult> failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## Failed");
            sb.AppendLine();
            foreach (BenchmarkResult f in failed)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {f.TestName}: {f.ErrorMessage}"));
            sb.AppendLine();
        }

        if (notReached.Count > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## Endpoint not reached (last point > {ReachedDistance}yd from target)"));
            sb.AppendLine();
            foreach (BenchmarkResult miss in notReached)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {miss.TestName}: {miss.PointCount} points"));
            sb.AppendLine();
        }

        sb.AppendLine("## Per-route results");
        sb.AppendLine();
        sb.AppendLine("| Route | Cold ms | Warm Min | Warm Avg | Warm Med | Warm Max | Pts | Len yd | Corners | Max Crn deg | Reached |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (BenchmarkResult r in results)
        {
            if (r.Success)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.TestName} | {r.ColdMs:F1} | {r.WarmMinMs:F1} | {r.WarmAvgMs:F1} | {r.WarmMedianMs:F1} | {r.WarmMaxMs:F1} | {r.PointCount} | {r.PathLengthYd:F1} | {r.CornerCount} | {r.MaxCornerDeg:F1} | {FormatReached(r.Reached)} |"));
            }
            else
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.TestName} | FAIL | - | - | - | - | - | - | - | - | {r.ErrorMessage} |"));
            }
        }

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void AppendStatsSection(StringBuilder sb, string title, List<double> sortedTimes)
    {
        if (sortedTimes.Count == 0)
            return;

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## {title}"));
        sb.AppendLine();
        sb.AppendLine("| Min | Max | Avg | Median | P95 | P99 | Samples |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {sortedTimes[0]:F1}ms | {sortedTimes[^1]:F1}ms | {sortedTimes.Average():F1}ms | {Median(sortedTimes):F1}ms | {Percentile(sortedTimes, 95):F1}ms | {Percentile(sortedTimes, 99):F1}ms | {sortedTimes.Count} |"));
        sb.AppendLine();
    }

    private static string Sanitize(string label)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            label = label.Replace(c, '-');
        return label.Replace(' ', '-').ToLowerInvariant();
    }

    private static double Median(List<double> values)
    {
        return values.Count % 2 == 0
            ? (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2
            : values[values.Count / 2];
    }

    private static double Percentile(List<double> values, int p)
    {
        int index = (int)Math.Ceiling(values.Count * (p / 100.0)) - 1;
        return values[Math.Max(0, Math.Min(index, values.Count - 1))];
    }
}
