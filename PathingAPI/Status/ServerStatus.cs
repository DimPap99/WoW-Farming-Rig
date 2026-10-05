using PathingAPI.Pathing;
using PathingAPI.Watch;

using PPather;
using PPather.Navmesh;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Linq;

using static System.Diagnostics.Stopwatch;

namespace PathingAPI.Status;

/// <summary>What the status bar shows, read in one go.</summary>
public sealed record StatusSnapshot(
    string Exp,
    string Era,
    PathingEngine Engine,
    int Tiles,
    IReadOnlyList<PPatherService.NavmeshCoverage> Coverage,
    PPatherService.NavmeshBakeStatus Bake,
    bool Busy,
    int Viewers,
    double LastQueryMs,
    int LastQueryPoints)
{
    public IEnumerable<string> ContinentsWithTiles => Coverage.Where(c => c.Tiles > 0).Select(c => c.Continent);

    /// <summary>
    /// Whether a route on <paramref name="continent"/> can be answered. Only the
    /// navmesh engine has a tile cache to judge by; SpotAStar is not second-guessed.
    /// </summary>
    public bool CanRoute(string? continent) =>
        Engine != PathingEngine.Navmesh ||
        (continent != null && Coverage.Any(c => c.Tiles > 0 && c.Continent.Equals(continent, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Server state for the UI. The tile count walks the cache directories, so it is
/// cached briefly; everything else is read live and costs nothing.
/// </summary>
public sealed class ServerStatus(PPatherService service, DataConfig dataConfig, PatherGate gate, WatchSettings watch)
{
    private static readonly TimeSpan CoverageTtl = TimeSpan.FromSeconds(15);

    private readonly object coverageLock = new();
    private PPatherService.NavmeshCoverage[] coverage = [];
    private long coverageAt;

    public StatusSnapshot Read()
    {
        PPatherService.NavmeshCoverage[] tiles = Coverage();
        NavmeshPathfinder.NavmeshStats stats = service.NavmeshPathfinder?.LastStats ?? default;

        return new StatusSnapshot(
            dataConfig.Exp,
            DataConfig.ClientEra(dataConfig.Exp),
            service.Engine,
            tiles.Sum(c => c.Tiles),
            tiles,
            service.BakeStatus,
            gate.IsBusy,
            watch.Viewers,
            stats.EnsureMs + stats.ResolveMs + stats.FindMs + stats.SmoothMs,
            stats.PointCount);
    }

    /// <summary>Drops the cached tile count, e.g. after a bake or a cache clear.</summary>
    public void Invalidate()
    {
        lock (coverageLock)
            coverageAt = 0;
    }

    private PPatherService.NavmeshCoverage[] Coverage()
    {
        lock (coverageLock)
        {
            if (coverageAt == 0 || GetElapsedTime(coverageAt) > CoverageTtl)
            {
                coverage = service.GetNavmeshCoverage();
                coverageAt = GetTimestamp();
            }

            return coverage;
        }
    }
}
