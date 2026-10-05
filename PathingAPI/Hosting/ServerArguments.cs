using Microsoft.Extensions.Configuration;

using PPather;

using SharedLib;

using System;
using System.Globalization;

namespace PathingAPI.Hosting;

/// <summary>
/// The command line (or same-named environment variables), read in one place.
/// <c>--exp</c> used to be resolved twice with its own default each time.
/// </summary>
public sealed record ServerArguments(string Exp, PathingEngine? Engine, string? Bake, string? BakeArea)
{
    public const string ExpKey = "exp";
    public const string BakeKey = "bake";
    public const string BakeAreaKey = "bake-area";

    /// <summary>Classic is the default client; only the data folders depend on it.</summary>
    public static readonly string DefaultExp = ClientVersion.SoM.ToString().ToLower(CultureInfo.InvariantCulture);

    public static ServerArguments From(IConfiguration configuration)
    {
        PathingEngine? engine =
            Enum.TryParse(configuration.GetSection(StartupConfigPathing.Position)["Engine"], out PathingEngine parsed)
                ? parsed
                : null;

        return new ServerArguments(
            Read(configuration, ExpKey) ?? DefaultExp,
            engine,
            Read(configuration, BakeKey),
            Read(configuration, BakeAreaKey));
    }

    /// <summary><c>all</c> or <c>*</c> means every continent (null); otherwise the continent named.</summary>
    public static string? Continent(string scope) => scope is "all" or "*" ? null : scope;

    private static string? Read(IConfiguration configuration, string key)
    {
        string? value = configuration[key] ?? Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
