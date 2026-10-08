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
        return new PathingServiceOptions(ServiceClientRegistration.Url(configuration, Position, PathingApi.DefaultUrl),
            ServiceKeyConfig.For(configuration, Position));
    }

    /// <summary>
    /// The options when <c>PathingService:Url</c> is set, else null: an agent with no service
    /// configured syncs nothing and runs on its own copies.
    /// </summary>
    public static PathingServiceOptions? FromIfConfigured(IConfiguration configuration)
    {
        return ServiceClientRegistration.IsConfigured(configuration, Position) ? From(configuration) : null;
    }

    /// <summary>The address only: the key never reaches a log.</summary>
    public override string ToString()
    {
        return Url.ToString();
    }
}
