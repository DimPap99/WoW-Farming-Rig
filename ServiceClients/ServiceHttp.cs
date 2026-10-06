using Contracts;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients;

/// <summary>
/// The HTTP plumbing every typed client shares: bodies in the contract's JSON shape
/// (<see cref="ContractJson"/>), a refusal's problem details read into a sentence, and nothing
/// thrown for a service that is down or refuses; only the caller's own cancellation is.
/// </summary>
/// <param name="service">The service's name in a message, such as "pathing service".</param>
public sealed class ServiceHttp(HttpClient http, string service)
{
    public async Task<ServiceResult<T>> GetAsync<T>(string uri, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, token).ConfigureAwait(false);
            return await ResultAsync<T>(response, token).ConfigureAwait(false);
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

    /// <summary>A body as it is, not JSON: a file's bytes exactly as the service stores them.</summary>
    public async Task<ServiceResult<byte[]>> GetBytesAsync(string uri, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false), (int)response.StatusCode, null)
                : new(default, (int)response.StatusCode, await ErrorAsync(response, token).ConfigureAwait(false));
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

    public async Task<ServiceResult<T>> SendAsync<T>(HttpMethod method, string uri, object? body, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = Request(method, uri, body);
            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            return await ResultAsync<T>(response, token).ConfigureAwait(false);
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

    public async Task<ServiceResult> SendAsync(HttpMethod method, string uri, object? body, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = Request(method, uri, body);
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

    /// <summary>Why the service could not be reached, for a person.</summary>
    public string Unreachable(Exception ex)
    {
        return $"the {service} could not be reached: {ex.Message}";
    }

    /// <summary>The body as a <typeparamref name="T"/>, or default when it is not JSON of that shape.</summary>
    public static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken token)
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

    /// <summary>
    /// The sentence a refusal carries: the services answer RFC 9457 problem details, whose
    /// <c>detail</c> is meant for a person; the status otherwise.
    /// </summary>
    public static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        string fallback = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        Problem? problem = await TryReadAsync<Problem>(response, token).ConfigureAwait(false);
        return problem?.Detail ?? problem?.Title ?? fallback;
    }

    private static async Task<ServiceResult<T>> ResultAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new(default, (int)response.StatusCode, await ErrorAsync(response, token).ConfigureAwait(false));
        }

        T? value = await TryReadAsync<T>(response, token).ConfigureAwait(false);
        return value is null
            ? new(default, (int)response.StatusCode, "the service answered without a readable body")
            : new(value, (int)response.StatusCode, null);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, object? body)
    {
        HttpRequestMessage request = new(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
        }

        return request;
    }

    private sealed record Problem(string? Title, string? Detail);
}
