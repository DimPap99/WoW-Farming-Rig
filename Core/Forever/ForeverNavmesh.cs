using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System.IO;

namespace Core.Forever;

/// <summary>Opens the disk-only Forever navmesh that the guarded runtime walks on.</summary>
public static class ForeverNavmesh
{
    /// <summary>A verified map's area, provided the map table places it on the verified continent.</summary>
    public static bool TryGetVerifiedArea(WorldMapAreaDB maps, int uiMapId, out WorldMapArea area) =>
        maps.TryGet(uiMapId, out area) &&
        ForeverVerifiedSetup.IsVerifiedMap(uiMapId, area.MapID);

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
    /// </summary>
    public static NavmeshPathfinder OpenDiskOnly(ILogger logger, string cacheDirectory) =>
        new(logger, null, cacheDirectory, new NavmeshBakeOptions(),
            new NavmeshQueryOptions { PreferGroundUnderPoint = true });
}
