using Contracts.Pathing;

using Microsoft.Extensions.Configuration;

using ServiceClients.Security;

using System;

namespace ServiceClients.Pathing;

/// <summary>
/// Where a host finds the Pathing Service: <c>PathingService:Url</c> in its configuration,
/// <see cref="PathingApi.DefaultUrl"/> when that is not set; and the key it sends
/// (<c>PathingService:ApiKey</c>, else the shared <c>ApiKey</c>), which a service on another
/// machine needs.
/// </summary>
public sealed record PathingServiceOptions(Uri Url, string? ApiKey = null)
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

        return new PathingServiceOptions(uri, ServiceKeyConfig.For(configuration, Position));
    }

    /// <summary>
    /// The options when <c>PathingService:Url</c> is set, else null: an agent with no service
    /// configured syncs nothing and runs on its own copies.
    /// </summary>
    public static PathingServiceOptions? FromIfConfigured(IConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(configuration[UrlKey]) ? null : From(configuration);
    }

    /// <summary>The address only: the key never reaches a log.</summary>
    public override string ToString()
    {
        return Url.ToString();
    }
}
