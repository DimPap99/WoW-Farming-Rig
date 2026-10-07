using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System.IO;

namespace Core.Forever;

/// <summary>Opens the disk-only Forever navmesh that the guarded runtime walks on.</summary>
public static class ForeverNavmesh
{
    /// <summary>
    /// Tile cache that a default-settings Forever bake writes for
    /// <paramref name="continent"/>. Only continents without a world-Z floor.
    /// </summary>
    public static string CacheDirectory(DataConfig data, string continent)
    {
        NavmeshBakeOptions bake = new();
        string version = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        string hash = NavmeshSettings.ComputeSettingsHash("forever", version,
            bake.AgentRadius, bake.AgentMaxClimb, bake.WalkableSlope);
        return Path.Combine(data.Navmesh, continent, hash);
    }

    /// <summary>
    /// A pathfinder over already-baked tiles only; it never bakes. The client
    /// reports no player height, so endpoints resolve to the ground under them.
    /// It plans with <paramref name="zones"/>: none unless the run's route
    /// applies them (<see cref="CostZonesOf"/>).
    /// </summary>
    public static NavmeshPathfinder OpenDiskOnly(ILogger logger, string cacheDirectory, CostZones? zones = null) =>
        new(logger, null, cacheDirectory, new NavmeshBakeOptions(),
            new NavmeshQueryOptions { PreferGroundUnderPoint = true })
        { Zones = zones ?? CostZones.Empty };

    /// <summary>
    /// The authored roads and danger zones of <paramref name="continent"/> for
    /// Forever (<c>Json/road/forever</c>, which the agent's data sync brings),
    /// read as the Pathing Service's planner reads them, so a route that
    /// applies them is walked as the Control Center previews it.
    /// </summary>
    public static CostZones CostZonesOf(ILogger logger, DataConfig data, string continent) =>
        CostZoneLoader.Load(logger, data.Road, continent, roadCoreHalfWidth: new NavmeshQueryOptions().RoadCore);
}
