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
}
