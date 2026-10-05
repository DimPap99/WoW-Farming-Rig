using Contracts;
using Contracts.Pathing;

using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Pathing;

/// <summary>
/// The Pathing Service, as its callers see it. Nothing here throws for a service that is down
/// or refuses a request: routes answer <see cref="RouteStatus.Unavailable"/> or
/// <see cref="RouteStatus.BadRequest"/>, everything else a failed <see cref="ServiceResult"/>.
/// Only the caller's own cancellation surfaces as an exception.
/// </summary>
public interface IPathingClient
{
    Task<RouteResponse> FindRouteAsync(RouteRequest request, CancellationToken token = default);

    Task<ServiceResult<PathingCapabilities>> GetCapabilitiesAsync(CancellationToken token = default);

    Task<ServiceResult<PathingHealth>> GetHealthAsync(CancellationToken token = default);

    Task<ServiceResult<RecordedPathInfo[]>> ListPathsAsync(string? filter = null, CancellationToken token = default);

    /// <param name="name">Relative to the path folder, <c>/</c> between subfolders.</param>
    Task<ServiceResult<RecordedPath>> GetPathAsync(string name, CancellationToken token = default);

    Task<ServiceResult> SavePathAsync(string name, Point3[] points, CancellationToken token = default);

    Task<ServiceResult> DeletePathAsync(string name, CancellationToken token = default);

    Task<ServiceResult<AreaAtPoint>> GetAreaAsync(int mapId, float worldX, float worldY, CancellationToken token = default);

    Task<ServiceResult<CostZoneSet>> GetCostZonesAsync(string continent, CancellationToken token = default);

    Task<ServiceResult> SaveRoadsAsync(string continent, int uiMapId, RoadZones roads, CancellationToken token = default);

    Task<ServiceResult> SaveDangerAsync(string continent, int uiMapId, DangerZones danger, CancellationToken token = default);

    Task<ServiceResult> ReloadCostZonesAsync(CancellationToken token = default);

    Task<ServiceResult<WorldInfo>> GetWorldAsync(CancellationToken token = default);

    /// <param name="uiMapId">The zone.</param>
    /// <param name="npcType">A key of <see cref="WorldInfo.NpcFlags"/>, such as <c>vendor</c>.</param>
    Task<ServiceResult<NpcInfo[]>> GetNpcsAsync(int uiMapId, string npcType, CancellationToken token = default);

    Task<ServiceResult<NamedLocation[]>> GetLocationsAsync(CancellationToken token = default);

    Task<ServiceResult<TestRoute[]>> GetTestRoutesAsync(CancellationToken token = default);

    Task<ServiceResult<NavmeshTile[]>> GetNavmeshTilesAsync(string continent, CancellationToken token = default);

    /// <summary>Deletes every baked tile of <paramref name="continent"/>; refused while a bake runs.</summary>
    Task<ServiceResult<CacheCleared>> ClearNavmeshAsync(string continent, CancellationToken token = default);

    Task<ServiceResult<BakeStatus>> GetBakeStatusAsync(CancellationToken token = default);

    /// <summary>Starts a bake in the background; refused (409) while another one runs.</summary>
    Task<ServiceResult<BakeStatus>> StartBakeAsync(BakeRequest request, CancellationToken token = default);

    /// <summary>Cancels the running bake; refused (409) when none runs.</summary>
    Task<ServiceResult<BakeStatus>> CancelBakeAsync(CancellationToken token = default);

    /// <summary>Starts extracting the area-id grid of <paramref name="continent"/>, or of every continent.</summary>
    Task<ServiceResult> BuildAreaGridAsync(string? continent = null, CancellationToken token = default);

    Task<ServiceResult> DrawPathAsync(WatchPath path, CancellationToken token = default);

    Task<ServiceResult> ShowMarkerAsync(WatchMarker marker, CancellationToken token = default);

    Task<ServiceResult> ClearWatchAsync(CancellationToken token = default);
}
