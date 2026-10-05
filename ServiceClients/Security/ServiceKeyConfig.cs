using Contracts;

using Microsoft.Extensions.Configuration;

namespace ServiceClients.Security;

/// <summary>Where a client finds the key of the service it calls.</summary>
public static class ServiceKeyConfig
{
    /// <summary>
    /// <c>{position}:ApiKey</c>, else the shared <see cref="ServiceKey.ConfigKey"/>: one key set on
    /// every machine serves every service, and one service can still be given its own.
    /// </summary>
    public static string? For(IConfiguration configuration, string position)
    {
        string? own = configuration[$"{position}:{ServiceKey.ConfigKey}"];
        string? shared = configuration[ServiceKey.ConfigKey];
        string? key = string.IsNullOrWhiteSpace(own) ? shared : own;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
