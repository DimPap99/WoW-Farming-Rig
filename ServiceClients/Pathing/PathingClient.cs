using Contracts;
using Contracts.Pathing;

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Pathing;

/// <summary>
/// <see cref="IPathingClient"/> over HTTP. Registered as a typed client by
/// <see cref="PathingClientRegistration.AddPathingClient"/>, which also sets timeouts and retries.
/// </summary>
public sealed class PathingClient(HttpClient http) : IPathingClient
{
    public async Task<RouteResponse> FindRouteAsync(RouteRequest request, CancellationToken token = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(PathingApi.FindRoute, request, ContractJson.Options, token)
                .ConfigureAwait(false);

            // Every status the route endpoint answers carries a RouteResponse; anything else
            // (a proxy, a crash page) is reported by status.
            if (await TryReadAsync<RouteResponse>(response, token).ConfigureAwait(false) is RouteResponse body)
            {
                return body;
            }

            RouteStatus status = response.StatusCode == HttpStatusCode.BadRequest ? RouteStatus.BadRequest : RouteStatus.Unavailable;
            return RouteResponse.Failed(status, await ErrorAsync(response, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RouteResponse.Failed(RouteStatus.Unavailable, Unreachable(ex));
        }
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
        string uri = string.Create(CultureInfo.InvariantCulture, $"{PathingApi.Area}?mapId={mapId}&x={worldX}&y={worldY}");
        return GetAsync<AreaAtPoint>(uri, token);
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

    private async Task<ServiceResult<T>> GetAsync<T>(string uri, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(default, (int)response.StatusCode, await ErrorAsync(response, token).ConfigureAwait(false));
            }

            T? value = await TryReadAsync<T>(response, token).ConfigureAwait(false);
            return value is null
                ? new(default, (int)response.StatusCode, "the service answered without a readable body")
                : new(value, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(default, 0, Unreachable(ex));
        }
    }

    private async Task<ServiceResult> SendAsync(HttpMethod method, string uri, object? body, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(method, uri);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
            }

            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new((int)response.StatusCode, null)
                : new((int)response.StatusCode, await ErrorAsync(response, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(0, Unreachable(ex));
        }
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json"))
        {
            return default;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // The service answers refusals as RFC 9457 problem details; detail is the sentence meant
    // for a person.
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        string fallback = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        Problem? problem = await TryReadAsync<Problem>(response, token).ConfigureAwait(false);
        return problem?.Detail ?? problem?.Title ?? fallback;
    }

    private static string Unreachable(Exception ex)
    {
        return $"the pathing service could not be reached: {ex.Message}";
    }

    // Each segment escaped on its own, so a subfolder's '/' stays a separator.
    private static string PathUri(string name)
    {
        string[] segments = name.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return $"{PathingApi.Paths}/{string.Join('/', segments)}";
    }

    private static string CostZoneUri(string continent, string kind, int uiMapId)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{PathingApi.CostZones}/{Uri.EscapeDataString(continent)}/{kind}/{uiMapId}");
    }

    private sealed record Problem(string? Title, string? Detail);
}
