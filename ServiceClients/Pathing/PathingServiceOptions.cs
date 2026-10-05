using Contracts.Pathing;

using Microsoft.Extensions.Configuration;

using System;

namespace ServiceClients.Pathing;

/// <summary>
/// Where a host finds the Pathing Service: <c>PathingService:Url</c> in its configuration,
/// <see cref="PathingApi.DefaultUrl"/> when that is not set.
/// </summary>
public sealed record PathingServiceOptions(Uri Url)
{
    public const string Position = "PathingService";

    public const string UrlKey = Position + ":Url";

    public static PathingServiceOptions From(IConfiguration configuration)
    {
        string? configured = configuration[UrlKey];
        string url = string.IsNullOrWhiteSpace(configured) ? PathingApi.DefaultUrl : configured;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{UrlKey} must be an http or https URL, not '{url}'.");
        }

        return new PathingServiceOptions(uri);
    }
}
