using Contracts.Bots;

using Microsoft.Extensions.Configuration;

using ServiceClients.Security;

using System;

namespace ServiceClients.Bots;

/// <summary>
/// Where a host finds the Bot Manager: <c>BotManager:Url</c>, <see cref="BotManagerApi.DefaultUrl"/>
/// when that is not set; and the key it sends (<c>BotManager:ApiKey</c>, else the shared
/// <c>ApiKey</c>), which a manager on another machine needs.
/// </summary>
public sealed record BotManagerOptions(Uri Url, string? ApiKey = null)
{
    public const string Position = "BotManager";

    public const string UrlKey = Position + ":Url";

    /// <summary>The options from <paramref name="configuration"/>, with the default URL when none is set.</summary>
    public static BotManagerOptions From(IConfiguration configuration)
    {
        string? configured = configuration[UrlKey];
        string url = string.IsNullOrWhiteSpace(configured) ? BotManagerApi.DefaultUrl : configured;
        return new BotManagerOptions(Parse(url), ServiceKeyConfig.For(configuration, Position));
    }

    /// <summary>The options when <c>BotManager:Url</c> is set, else null: an agent with no manager configured runs on its own.</summary>
    public static BotManagerOptions? FromIfConfigured(IConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(configuration[UrlKey]) ? null : From(configuration);
    }

    /// <summary>The address only: the key never reaches a log.</summary>
    public override string ToString()
    {
        return Url.ToString();
    }

    private static Uri Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{UrlKey} must be an http or https URL, not '{url}'.");
        }

        return uri;
    }
}
