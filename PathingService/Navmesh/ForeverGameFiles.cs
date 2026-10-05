using ForeverCasc;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using PPather.Storage;

using System;
using System.IO;

namespace PathingService.Navmesh;

/// <summary>
/// WoW: Forever's geometry, read from the installed client's local CASC storage for bakes and
/// terrain. Classic clients read <c>Json/MPQ</c> instead and need nothing configured here.
/// </summary>
public static class ForeverGameFiles
{
    public const string Section = "ForeverCasc";

    public const string ForeverClient = "forever";

    public const string DefaultProduct = "wow_classic_beta";

    /// <summary>
    /// The CASC source for a Forever instance whose <see cref="Section"/> names an install, or
    /// null: another client, or a Forever instance that serves baked tiles only.
    /// </summary>
    public static Func<IGameFileSource>? From(IConfiguration configuration, string client, ILogger logger)
    {
        if (!client.Equals(ForeverClient, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        IConfigurationSection casc = configuration.GetSection(Section);
        string? install = casc["Install"];
        string? listfile = casc["Listfile"];

        if (string.IsNullOrWhiteSpace(install) != string.IsNullOrWhiteSpace(listfile))
        {
            throw new InvalidOperationException($"{Section}:Install and {Section}:Listfile must be configured together.");
        }

        if (string.IsNullOrWhiteSpace(install))
        {
            logger.LogInformation("Forever CASC geometry is not configured: bakes and terrain are unavailable, routes use baked tiles");
            return null;
        }

        string product = string.IsNullOrWhiteSpace(casc["Product"]) ? DefaultProduct : casc["Product"]!;
        string cache = string.IsNullOrWhiteSpace(casc["Cache"])
            ? Path.Combine(Path.GetTempPath(), "WoWFarmingRig", "forever-casc")
            : casc["Cache"]!;

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Forever CASC geometry enabled from {Install}", install);
        }

        return () => new CascGameFileSource(install, product, listfile!, cache);
    }
}
