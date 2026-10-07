using Contracts;
using Contracts.Pathing;

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Pathing;

/// <summary>
/// <see cref="IPathingClient"/> over HTTP. Registered as a typed client by
/// <see cref="PathingClientRegistration.AddPathingClient"/>, which also sets timeouts and retries.
/// </summary>
public sealed class PathingClient(HttpClient http) : IPathingClient
{
    private readonly ServiceHttp service = new(http, "pathing service");

    public async Task<RouteResponse> FindRouteAsync(RouteRequest request, CancellationToken token = default)
    {
        // The serializer refuses these, which would read as the service being out of reach.
        if (!IsFinite(request.From) || !IsFinite(request.To) || request.Tuning is RouteTuning tuning &&
            (!float.IsFinite(tuning.JitterYards) || tuning.EdgeMarginYards is float margin && !float.IsFinite(margin)))
        {
            return RouteResponse.Failed(RouteStatus.BadRequest, "points and tuning must be finite numbers");
        }

        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(PathingApi.FindRoute, request, ContractJson.Options, token)
                .ConfigureAwait(false);

            // The route endpoint answers every status it has with a RouteResponse as application/json.
            // A refusal from in front of it (the key guard, body binding, an exception) is problem+json,
            // and anything else (a proxy, a crash page) is reported by its status.
            if (response.Content.Headers.ContentType?.MediaType == "application/json" &&
                await ServiceHttp.TryReadAsync<RouteResponse>(response, token).ConfigureAwait(false) is RouteResponse body &&
                IsRoute(body))
            {
                return body;
            }

            if (response.IsSuccessStatusCode)
            {
                return RouteResponse.Failed(RouteStatus.Unavailable,
                    $"the pathing service answered a route this client cannot read: {await ExcerptAsync(response, token).ConfigureAwait(false)}");
            }

            RouteStatus status = response.StatusCode == HttpStatusCode.BadRequest ? RouteStatus.BadRequest : RouteStatus.Unavailable;
            return RouteResponse.Failed(status, await ServiceHttp.ErrorAsync(response, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RouteResponse.Failed(RouteStatus.Unavailable, service.Unreachable(ex));
        }
    }

    // A status this client knows, and points, at least one when the route was found or ends short.
    private static bool IsRoute(RouteResponse route)
    {
        return route.Points is not null && Enum.IsDefined(route.Status) &&
               (route.Points.Length > 0 || route.Status is not (RouteStatus.Found or RouteStatus.Partial));
    }

    private static bool IsFinite(Point3 p)
    {
        return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    }

    /// <summary>The start of a body that is not what was expected, for a person to read.</summary>
    private static async Task<string> ExcerptAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int MaxExcerpt = 200;
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "...";
    }

    public Task<ServiceResult<PathingCapabilities>> GetCapabilitiesAsync(CancellationToken token = default)
    {
        return GetAsync<PathingCapabilities>(PathingApi.Capabilities, token);
    }

    public Task<ServiceResult<PathingHealth>> GetHealthAsync(CancellationToken token = default)
    {
        return GetAsync<PathingHealth>(PathingApi.Health, token);
    }

    public Task<ServiceResult<RecordedPathInfo[]>> ListPathsAsync(string? filter = null, CancellationToken token = default)
    {
        string uri = string.IsNullOrEmpty(filter) ? PathingApi.Paths : $"{PathingApi.Paths}?filter={Uri.EscapeDataString(filter)}";
        return GetAsync<RecordedPathInfo[]>(uri, token);
    }

    public Task<ServiceResult<RecordedPath>> GetPathAsync(string name, CancellationToken token = default)
    {
        return GetAsync<RecordedPath>(PathUri(name), token);
    }

    public Task<ServiceResult> SavePathAsync(string name, Point3[] points, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Put, PathUri(name), points, token);
    }

    public Task<ServiceResult> DeletePathAsync(string name, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Delete, PathUri(name), null, token);
    }

    public Task<ServiceResult<AreaAtPoint>> GetAreaAsync(int mapId, float worldX, float worldY, CancellationToken token = default)
    {
        // Invariant: a server in a decimal-comma culture must still read 12.5 as twelve and a half.
        // Escaped: a large number is written 3E+11, and a + in a query reads as a space.
        string uri = string.Create(CultureInfo.InvariantCulture, $"{PathingApi.Area}?mapId={mapId}&x={Number(worldX)}&y={Number(worldY)}");
        return GetAsync<AreaAtPoint>(uri, token);

        static string Number(float value)
        {
            return Uri.EscapeDataString(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    public Task<ServiceResult<ForeverRouteInfo[]>> ListForeverRoutesAsync(CancellationToken token = default)
    {
        return GetAsync<ForeverRouteInfo[]>(PathingApi.ForeverRoutes, token);
    }

    public Task<ServiceResult<ForeverRouteDocument>> GetForeverRouteAsync(string name, CancellationToken token = default)
    {
        return GetAsync<ForeverRouteDocument>(ForeverRouteUri(name), token);
    }

    public Task<ServiceResult> SaveForeverRouteAsync(string name, string text, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Put, ForeverRouteUri(name), new ForeverRouteText(text), token);
    }

    public Task<ServiceResult> DeleteForeverRouteAsync(string name, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Delete, ForeverRouteUri(name), null, token);
    }

    public Task<ServiceResult<ForeverRouteInfo>> CheckForeverRouteAsync(ForeverRouteDraft draft, CancellationToken token = default)
    {
        return SendAsync<ForeverRouteInfo>(HttpMethod.Post, PathingApi.ForeverRouteCheck, draft, token);
    }

    public Task<ServiceResult<CostZoneSet>> GetCostZonesAsync(string continent, CancellationToken token = default)
    {
        return GetAsync<CostZoneSet>($"{PathingApi.CostZones}/{Uri.EscapeDataString(continent)}", token);
    }

    public Task<ServiceResult> SaveRoadsAsync(string continent, int uiMapId, RoadZones roads, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Put, CostZoneUri(continent, PathingApi.Roads, uiMapId), roads, token);
    }

    public Task<ServiceResult> SaveDangerAsync(string continent, int uiMapId, DangerZones danger, CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Put, CostZoneUri(continent, PathingApi.Danger, uiMapId), danger, token);
    }

    public Task<ServiceResult> ReloadCostZonesAsync(CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Post, PathingApi.CostZonesReload, null, token);
    }

    public Task<ServiceResult<SyncManifest>> GetSyncManifestAsync(string set, string? era = null, CancellationToken token = default)
    {
        return GetAsync<SyncManifest>($"{PathingApi.Sync}/{Uri.EscapeDataString(set)}{EraQuery(era)}", token);
    }

    public Task<ServiceResult<byte[]>> GetSyncFileAsync(string set, string name, string? era = null, CancellationToken token = default)
    {
        return service.GetBytesAsync(
            $"{PathingApi.Sync}/{Uri.EscapeDataString(set)}/{PathingApi.SyncFiles}/{EscapeSegments(name)}{EraQuery(era)}", token);
    }

    public Task<ServiceResult<WorldInfo>> GetWorldAsync(CancellationToken token = default)
    {
        return GetAsync<WorldInfo>(PathingApi.World, token);
    }

    public Task<ServiceResult<NpcInfo[]>> SearchNpcsAsync(int mapId, string name, CancellationToken token = default)
    {
        string uri = string.Create(CultureInfo.InvariantCulture,
            $"{PathingApi.NpcSearch}?mapId={mapId}&name={Uri.EscapeDataString(name)}");
        return GetAsync<NpcInfo[]>(uri, token);
    }

    public Task<ServiceResult<SpawnPoint[]>> GetSpawnsAsync(int uiMapId, string layer, CancellationToken token = default)
    {
        string uri = string.Create(CultureInfo.InvariantCulture,
            $"{PathingApi.Spawns}?uiMapId={uiMapId}&layer={Uri.EscapeDataString(layer)}");
        return GetAsync<SpawnPoint[]>(uri, token);
    }

    public Task<ServiceResult<QuestSummary[]>> GetQuestsAsync(int uiMapId, CancellationToken token = default)
    {
        return GetAsync<QuestSummary[]>(string.Create(CultureInfo.InvariantCulture, $"{PathingApi.Quests}?uiMapId={uiMapId}"), token);
    }

    public Task<ServiceResult<QuestSummary[]>> SearchQuestsAsync(string name, CancellationToken token = default)
    {
        return GetAsync<QuestSummary[]>($"{PathingApi.QuestSearch}?name={Uri.EscapeDataString(name)}", token);
    }

    public Task<ServiceResult<QuestDetail>> GetQuestAsync(int id, CancellationToken token = default)
    {
        return GetAsync<QuestDetail>(string.Create(CultureInfo.InvariantCulture, $"{PathingApi.Quests}/{id}"), token);
    }

    public Task<ServiceResult<NpcInfo[]>> GetNpcsAsync(int uiMapId, string npcType, CancellationToken token = default)
    {
        string uri = string.Create(CultureInfo.InvariantCulture,
            $"{PathingApi.Npcs}?uiMapId={uiMapId}&flag={Uri.EscapeDataString(npcType)}");
        return GetAsync<NpcInfo[]>(uri, token);
    }

    public Task<ServiceResult<NamedLocation[]>> GetLocationsAsync(CancellationToken token = default)
    {
        return GetAsync<NamedLocation[]>(PathingApi.Locations, token);
    }

    public Task<ServiceResult<TestRoute[]>> GetTestRoutesAsync(CancellationToken token = default)
    {
        return GetAsync<TestRoute[]>(PathingApi.TestRoutes, token);
    }

    public Task<ServiceResult<NavmeshTile[]>> GetNavmeshTilesAsync(string continent, CancellationToken token = default)
    {
        return GetAsync<NavmeshTile[]>($"{NavmeshUri(continent)}/{PathingApi.Tiles}", token);
    }

    public Task<ServiceResult<CacheCleared>> ClearNavmeshAsync(string continent, CancellationToken token = default)
    {
        return SendAsync<CacheCleared>(HttpMethod.Delete, NavmeshUri(continent), null, token);
    }

    public Task<ServiceResult<BakeStatus>> GetBakeStatusAsync(CancellationToken token = default)
    {
        return GetAsync<BakeStatus>(PathingApi.Bake, token);
    }

    public Task<ServiceResult<BakeStatus>> StartBakeAsync(BakeRequest request, CancellationToken token = default)
    {
        return SendAsync<BakeStatus>(HttpMethod.Post, PathingApi.Bake, request, token);
    }

    public Task<ServiceResult<BakeStatus>> CancelBakeAsync(CancellationToken token = default)
    {
        return SendAsync<BakeStatus>(HttpMethod.Post, PathingApi.BakeCancel, null, token);
    }

    public Task<ServiceResult> BuildAreaGridAsync(string? continent = null, CancellationToken token = default)
    {
        string uri = string.IsNullOrEmpty(continent)
            ? PathingApi.AreaGrids
            : $"{PathingApi.AreaGrids}?continent={Uri.EscapeDataString(continent)}";
        return SendAsync(HttpMethod.Post, uri, null, token);
    }

    public Task<ServiceResult> ClearWatchAsync(CancellationToken token = default)
    {
        return SendAsync(HttpMethod.Post, PathingApi.WatchClear, null, token);
    }

    private Task<ServiceResult<T>> GetAsync<T>(string uri, CancellationToken token)
    {
        return service.GetAsync<T>(uri, token);
    }

    private Task<ServiceResult<T>> SendAsync<T>(HttpMethod method, string uri, object? body, CancellationToken token)
    {
        return service.SendAsync<T>(method, uri, body, token);
    }

    private Task<ServiceResult> SendAsync(HttpMethod method, string uri, object? body, CancellationToken token)
    {
        return service.SendAsync(method, uri, body, token);
    }

    private static string PathUri(string name)
    {
        return $"{PathingApi.Paths}/{EscapeSegments(name)}";
    }

    // Each segment escaped on its own, so a subfolder's '/' stays a separator.
    private static string EscapeSegments(string name)
    {
        string[] segments = name.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    private static string EraQuery(string? era)
    {
        return era is null ? string.Empty : $"?era={Uri.EscapeDataString(era)}";
    }

    private static string ForeverRouteUri(string name)
    {
        return $"{PathingApi.ForeverRoutes}/{Uri.EscapeDataString(name)}";
    }

    private static string NavmeshUri(string continent)
    {
        return $"{PathingApi.Navmesh}/{Uri.EscapeDataString(continent)}";
    }

    private static string CostZoneUri(string continent, string kind, int uiMapId)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{PathingApi.CostZones}/{Uri.EscapeDataString(continent)}/{kind}/{uiMapId}");
    }

}
