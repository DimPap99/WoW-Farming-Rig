using SharedLib;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace PathingAPI.Catalog;

/// <summary>
/// The recorded grind paths under <see cref="DataConfig.Path"/>: listing, reading,
/// the screenshot a recording may have beside it, and a guess at its zone.
/// </summary>
public sealed class RecordedPaths(DataConfig dataConfig, WorldMapAreaDB worldMapAreas)
{
    public const string PathPattern = "*.json";
    public const string ThumbnailExtension = ".jpg";

    /// <summary>Static route Frontend serves <see cref="DataConfig.Path"/> on.</summary>
    public const string RequestPath = "path";

    // "2_Elwynn" before "10_Elwynn". Portable, unlike WinAPI's StrCmpLogicalW the page
    // used before, which P/Invokes shlwapi.dll and throws on Linux.
    private static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

    private string Root => Path.GetFullPath(dataConfig.Path);

    /// <summary>Paths relative to the folder, '/'-separated, naturally ordered.</summary>
    public IReadOnlyList<string> Files()
    {
        string root = Root;
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, PathPattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(Natural)
            .ToArray();
    }

    /// <summary>
    /// The points as recorded: zone map coordinates (0-100, z = 0) for most files,
    /// world coordinates for the few recorded with a height.
    /// </summary>
    public Vector3[] Read(string file)
    {
        return Newtonsoft.Json.JsonConvert.DeserializeObject<Vector3[]>(File.ReadAllText(Resolve(file))) ?? [];
    }

    /// <summary>The URL of the screenshot saved with the recording, if there is one.</summary>
    public string? Thumbnail(string file)
    {
        string image = Path.ChangeExtension(file, ThumbnailExtension);
        return File.Exists(Resolve(image)) ? $"{RequestPath}/{Uri.EscapeDataString(image).Replace("%2F", "/")}" : null;
    }

    /// <summary>The zone whose name the file name contains (the longest match), e.g. "16_Loch Modan".</summary>
    public WorldMapArea? GuessZone(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        WorldMapArea? best = null;

        foreach (WorldMapArea area in worldMapAreas.Values)
        {
            if (!string.IsNullOrEmpty(area.AreaName) &&
                name.Contains(area.AreaName, StringComparison.OrdinalIgnoreCase) &&
                (best == null || area.AreaName.Length > best.Value.AreaName.Length))
            {
                best = area;
            }
        }

        return best;
    }

    /// <summary>Resolves under the folder only: the name comes from the browser.</summary>
    private string Resolve(string file)
    {
        string root = Root;
        string full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{file}' is outside the path folder.", nameof(file));

        return full;
    }
}
