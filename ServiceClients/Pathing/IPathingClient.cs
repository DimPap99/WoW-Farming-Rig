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

    /// <summary>Every WoW: Forever route file, each checked as a run would load it.</summary>
    Task<ServiceResult<ForeverRouteInfo[]>> ListForeverRoutesAsync(CancellationToken token = default);

    /// <param name="name">A file name in the route folder, ending in <c>.json</c>.</param>
    Task<ServiceResult<ForeverRouteDocument>> GetForeverRouteAsync(string name, CancellationToken token = default);

    /// <summary>Creates or replaces a route file with <paramref name="text"/>; refused (400, with the step) when a run would refuse it.</summary>
    Task<ServiceResult> SaveForeverRouteAsync(string name, string text, CancellationToken token = default);

    Task<ServiceResult> DeleteForeverRouteAsync(string name, CancellationToken token = default);

    /// <summary>What a run would make of <paramref name="draft"/>, without saving it.</summary>
    Task<ServiceResult<ForeverRouteInfo>> CheckForeverRouteAsync(ForeverRouteDraft draft, CancellationToken token = default);

    Task<ServiceResult<CostZoneSet>> GetCostZonesAsync(string continent, CancellationToken token = default);

    Task<ServiceResult> SaveRoadsAsync(string continent, int uiMapId, RoadZones roads, CancellationToken token = default);

    Task<ServiceResult> SaveDangerAsync(string continent, int uiMapId, DangerZones danger, CancellationToken token = default);

    Task<ServiceResult> ReloadCostZonesAsync(CancellationToken token = default);

    /// <summary>Every file of a sync set with its size and hash, for an agent to compare its copies with.</summary>
    /// <param name="set">One of <see cref="SyncSets"/>.</param>
    /// <param name="era">The era of <see cref="SyncSets.CostZones"/>; null for the other sets.</param>
    Task<ServiceResult<SyncManifest>> GetSyncManifestAsync(string set, string? era = null, CancellationToken token = default);

    /// <summary>One file of a sync set, its bytes exactly as the service stores them.</summary>
    /// <param name="name">Its path in the set's folder, <c>/</c> between subfolders.</param>
    Task<ServiceResult<byte[]>> GetSyncFileAsync(string set, string name, string? era = null, CancellationToken token = default);

    Task<ServiceResult<WorldInfo>> GetWorldAsync(CancellationToken token = default);

    /// <param name="uiMapId">The zone.</param>
    /// <param name="npcType">A key of <see cref="WorldInfo.NpcFlags"/>, such as <c>vendor</c>.</param>
    Task<ServiceResult<NpcInfo[]>> GetNpcsAsync(int uiMapId, string npcType, CancellationToken token = default);

    /// <summary>Creatures on the continent <paramref name="mapId"/> whose name holds <paramref name="name"/>, where each first spawns.</summary>
    Task<ServiceResult<NpcInfo[]>> SearchNpcsAsync(int mapId, string name, CancellationToken token = default);

    /// <summary>One of the <see cref="SpawnLayers"/> in a zone.</summary>
    Task<ServiceResult<SpawnPoint[]>> GetSpawnsAsync(int uiMapId, string layer, CancellationToken token = default);

    /// <summary>The quests given in a zone, by level.</summary>
    Task<ServiceResult<QuestSummary[]>> GetQuestsAsync(int uiMapId, CancellationToken token = default);

    /// <summary>Quests whose name holds <paramref name="name"/>, at most 50.</summary>
    Task<ServiceResult<QuestSummary[]>> SearchQuestsAsync(string name, CancellationToken token = default);

    /// <summary>One quest with every place it sends a character to; 404 for a quest the service does not know.</summary>
    Task<ServiceResult<QuestDetail>> GetQuestAsync(int id, CancellationToken token = default);

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

    /// <summary>Forgets the remembered routes and tells every route watcher to.</summary>
    Task<ServiceResult> ClearWatchAsync(CancellationToken token = default);
}
