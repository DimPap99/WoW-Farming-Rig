#nullable enable
using DotRecast.Detour;
using DotRecast.Detour.Io;

using System.Globalization;
using System.IO;

namespace PPather.Navmesh;

/// <summary>How baked tiles are named and read in a cache directory: one file per tile, <c>tile_{x}_{z}.dnm</c>.</summary>
public static class NavmeshTileFiles
{
    /// <summary>Every tile file of a cache directory, for <see cref="Directory.EnumerateFiles(string, string)"/>.</summary>
    public const string Pattern = "tile_*.dnm";

    public static string PathOf(string cacheDir, int tileX, int tileZ)
    {
        return Path.Combine(cacheDir, string.Create(CultureInfo.InvariantCulture, $"tile_{tileX}_{tileZ}.dnm"));
    }

    /// <summary>The tile a file of <see cref="Pattern"/> holds.</summary>
    public static bool TryTileOf(string file, out int tileX, out int tileZ)
    {
        tileX = tileZ = 0;
        string[] parts = Path.GetFileNameWithoutExtension(file).Split('_');
        return parts.Length == 3 &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out tileX) &&
               int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tileZ);
    }

    /// <summary>A baked tile as Detour reads it, straight from its file; null when it is not baked.</summary>
    public static DtMeshData? Read(string cacheDir, int tileX, int tileZ)
    {
        string file = PathOf(cacheDir, tileX, tileZ);
        if (!File.Exists(file))
        {
            return null;
        }

        using BinaryReader reader = new(File.OpenRead(file));
        return new DtMeshDataReader().Read(reader, NavmeshSettings.VertsPerPoly);
    }
}
