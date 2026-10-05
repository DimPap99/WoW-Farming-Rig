using PPather;
using PPather.Planning;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace PathingService.Status;

/// <summary>
/// Baked tiles per continent, counted at most every <see cref="Ttl"/>: counting walks every tile
/// directory, and status pages ask every few seconds. A bake or a cache clear drops the count at
/// once.
/// </summary>
public sealed class CoverageCache(RoutePlanner planner)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private readonly Lock gate = new();
    private PPatherService.NavmeshCoverage[] coverage = [];
    private long countedAt;

    public PPatherService.NavmeshCoverage[] Read()
    {
        lock (gate)
        {
            if (countedAt == 0 || GetElapsedTime(countedAt) > Ttl)
            {
                coverage = planner.Coverage();
                countedAt = GetTimestamp();
            }

            return coverage;
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            countedAt = 0;
        }
    }
}
