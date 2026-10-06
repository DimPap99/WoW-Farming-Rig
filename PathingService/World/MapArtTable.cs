using Contracts.Pathing;

using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PathingService.World;

/// <summary>
/// Where each era's map art lies on its continents (<see cref="MapArt"/>): the stitched minimap
/// pictures <c>scripts/extract-minimap.py</c> makes, which the Leaflet map draws and the Control
/// Center's world view lays under its routes. The numbers are the <c>Configs</c> of
/// <c>MapAssets/wwwroot/script/leaflet-watch.js</c>, BlazorServer's map, which keeps its own copy;
/// <c>ServiceTests</c> holds the two in step.
/// </summary>
public static class MapArtTable
{
    /// <summary>The coarsest zoom the extraction makes: a continent in a few dozen tiles.</summary>
    public const int MinZoom = 2;

    /// <summary>The finest: an ADT in 512 pixels, as the minimap draws it.</summary>
    public const int MaxZoom = 6;

    /// <summary>Every era's art by era and continent. Column is Leaflet's offset.min.y, Row its offset.min.x.</summary>
    public static FrozenDictionary<(string Era, string Continent), MapArt> All { get; } = new Dictionary<(string, string), MapArt>
    {
        [("precata", "Azeroth")] = Of(24, 20, 10752, 21504),
        [("precata", "Kalimdor")] = Of(0, 0, 26112, 28672),
        [("precata", "Expansion01")] = Of(12, 6, 25088, 19968),
        [("precata", "Northrend")] = Of(11, 9, 19968, 14848),

        [("cata", "Azeroth")] = Of(17, 20, 14848, 21504),
        [("cata", "Kalimdor")] = Of(0, 0, 29184, 28672),
        [("cata", "Deephome")] = Of(26, 25, 5120, 5120),
        [("cata", "LostIsles")] = Of(23, 24, 5120, 14336),
        [("cata", "Gilneas2")] = Of(24, 31, 4608, 3584),
        [("cata", "MaelstromZone")] = Of(28, 28, 2560, 2560),

        [("mop", "Azeroth")] = Of(17, 20, 14848, 21504),
        [("mop", "Kalimdor")] = Of(0, 0, 29184, 28672),
        [("mop", "Expansion01")] = Of(12, 6, 25088, 19968),
        [("mop", "Northrend")] = Of(11, 9, 19968, 14848),
        [("mop", "HawaiiMainLand")] = Of(16, 18, 15360, 12288),
        [("mop", "Deephome")] = Of(26, 25, 5120, 5120),
        [("mop", "LostIsles")] = Of(23, 24, 5120, 14336),
        [("mop", "Gilneas2")] = Of(24, 31, 4608, 3584),
        [("mop", "MaelstromZone")] = Of(28, 28, 2560, 2560),
        [("mop", "NewRaceStartZone")] = Of(20, 26, 5120, 5120),
    }.ToFrozenDictionary();

    /// <summary>
    /// The art of a continent as this client sees it - from the era its tiles are served from
    /// (<see cref="DataConfig.TileEra"/>: Cataclysm reads Outland's and Northrend's from precata) -
    /// when those tiles are on disk; null otherwise, as for every Forever continent.
    /// </summary>
    public static MapArt? For(DataConfig data, string continent)
    {
        if (!All.TryGetValue((DataConfig.TileEra(data.Exp, continent), continent), out MapArt? art))
        {
            return null;
        }

        string folder = data.LeafletFor(continent);
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.webp").Any() ? art : null;
    }

    private static MapArt Of(int column, int row, int width, int height)
    {
        return new MapArt(column, row, width, height, MinZoom, MaxZoom);
    }
}
