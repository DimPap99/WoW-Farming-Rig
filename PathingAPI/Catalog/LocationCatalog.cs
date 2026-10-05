using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace PathingAPI.Catalog;

/// <summary>A place the search panel can route from or to.</summary>
public sealed record NamedLocation(string Name, Vector4 World)
{
    public float MapId => World.W;
}

/// <summary>A continent with at least one known location.</summary>
public sealed record ContinentChoice(float MapId, string Name, int Locations);

/// <summary>
/// The search panel's From/To list: hand-picked test spots plus every named
/// teleport in <c>wwwroot/teleport_locations.txt</c>. Read once and shared; each page
/// used to re-read and re-parse the 1500-line file on its first render.
/// </summary>
public sealed class LocationCatalog
{
    public const string TeleportFile = "teleport_locations.txt";

    // id x y z orientation mapId name - positional; the name may contain spaces.
    private const int TeleportFields = 7;

    private readonly IReadOnlyList<NamedLocation> locations;

    public LocationCatalog(IWebHostEnvironment env, WorldMapAreaDB worldMapAreas, ILogger<LocationCatalog> logger)
    {
        // WorldMapAreaDB fills ContinentDB; it is injected for that ordering only.
        _ = worldMapAreas;

        List<NamedLocation> all = [.. Curated()];
        string file = Path.Combine(env.WebRootPath, TeleportFile);
        if (File.Exists(file))
            all.AddRange(ReadTeleports(file, logger));
        else
            logger.LogWarning("{File} not found; the search panel offers only the curated spots", file);

        locations = all;
        Continents = all
            .Where(l => ContinentDB.IdToName.ContainsKey(l.MapId))
            .GroupBy(l => l.MapId)
            .OrderBy(g => g.Key)
            .Select(g => new ContinentChoice(g.Key, ContinentDB.IdToName[g.Key], g.Count()))
            .ToArray();
    }

    /// <summary>Continents (not instances) that have locations, by map id.</summary>
    public IReadOnlyList<ContinentChoice> Continents { get; }

    public IEnumerable<NamedLocation> On(float mapId) => locations.Where(l => l.MapId == mapId);

    public NamedLocation? Find(float mapId, string name) =>
        locations.FirstOrDefault(l => l.MapId == mapId && l.Name == name);

    private static List<NamedLocation> ReadTeleports(string file, ILogger logger)
    {
        List<NamedLocation> read = [];
        int line = 0;
        foreach (string text in File.ReadLines(file))
        {
            line++;
            ReadOnlySpan<char> trim = text.AsSpan().Trim();
            if (trim.IsEmpty)
                continue;

            Span<Range> fields = stackalloc Range[TeleportFields];
            if (trim.Split(fields, ' ') < TeleportFields ||
                !TryFloat(trim[fields[1]], out float x) ||
                !TryFloat(trim[fields[2]], out float y) ||
                !TryFloat(trim[fields[3]], out float z) ||
                !TryFloat(trim[fields[5]], out float mapId))
            {
                logger.LogWarning("{File}:{Line} is not 'id x y z orientation mapId name'", TeleportFile, line);
                continue;
            }

            read.Add(new NamedLocation(trim[fields[6]].ToString(), new Vector4(x, y, z, mapId)));
        }

        return read;
    }

    // Invariant: the file uses '.', and a server in a decimal-comma culture misread every coordinate.
    private static bool TryFloat(ReadOnlySpan<char> s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Spots used while tuning the engine: towns, choke points and known trouble.</summary>
    private static IEnumerable<NamedLocation> Curated()
    {
        foreach ((string continent, (float x, float y, float z, string name)[] spots) in CuratedSpots)
        {
            if (!ContinentDB.NameToId.TryGetValue(continent, out float mapId))
                continue;

            foreach ((float x, float y, float z, string name) in spots)
                yield return new NamedLocation(name, new Vector4(x, y, z, mapId));
        }
    }

    private static readonly (string Continent, (float X, float Y, float Z, string Name)[] Spots)[] CuratedSpots =
    [
        ("Kalimdor",
        [
            (-7899, -3563, 50, "Tanaris, South to Gadgetzan"),
            (-7200, -3770, 8, "Tanaris, Gadgetzan"),
            (-7217, 907, -3, "Silithus, The calling"),
            (-6861, 754, 40, "Silithus, Repair"),
            (1381, -4368, 26, "Durotar, Orgrimmar gates"),
            (-896, -3770, 11, "Barrens, Ratchet"),
            (-441, -2596, 96, "Barrens, Crossroads"),
            (-2380, -1882, 95, "Barrens, Camp Taurajo"),
            (965, 1040, 104, "Stonetalon, Sun Rock Retreat"),
            (-1718, 3198, 4, "Desolace, Shadowprey village"),
            (2750, -3907, 91, "Azshara, Alliance FP"),
            (2565, -6380, 108.701f, "Azshara, Manor before bridge"),
            (2537, -6672, 104.5f, "Azshara, Manor after bridge"),
        ]),
        ("Azeroth",
        [
            (-8264, -2689, 134, "Burning Steppes, Flight path"),
            (-7558, -2167, 154, "Burning Steppes, Ragged John"),
            (-6236, 134, 428, "Dun Morogh, Coldridge Valley"),
            (-5880, -116, 362, "Dun Morogh, Coldridge pass"),
            (-8997.167f, -54.24005f, 88.86176f, "Elwynn Forest, Northshire spot 1"),
            (-8948.777f, -46.50463f, 91.68633f, "Elwynn Forest, Northshire spot 2"),
            (-8903.322f, -69.84078f, 86.58018f, "Elwynn Forest, Northshire spot 3"),
            (-8846.563f, -50.88147f, 85.94726f, "Elwynn Forest, Northshire spot 4"),
            (-8801.825f, -71.98528f, 88.48592f, "Elwynn Forest, Northshire spot 5"),
            (-8819.195f, -133.7280f, 81.17621f, "Elwynn Forest, Northshire spot 6"),
            (-8866.953f, -118.7995f, 80.66679f, "Elwynn Forest, Northshire spot 7"),
            (-8939.639f, -80.44841f, 87.70438f, "Elwynn Forest, Northshire spot 8"),
            (-8987.028f, -57.00874f, 90.75562f, "Elwynn Forest, Northshire spot 9"),
            (-8851.57f, -188.234f, 89.31455f, "Elwynn Forest, Northshire Khelden Bremen"),
            (-8853.587f, -193.336f, 81.9331f, "Elwynn Forest, Northshire Priestess Anetta"),
            (-8926.743f, -195.5889f, 80.58859f, "Elwynn Forest, Northshire Drusilla La Salle"),
            (-8863.468f, -210.9055f, 80.57187f, "Elwynn Forest, Northshire Jorik Kerridan"),
            (-8914.567f, -215.0163f, 82.11676f, "Elwynn Forest, Northshire Brother Sammuel"),
            (-8918.364f, -208.4109f, 82.12556f, "Elwynn Forest, Northshire Llane Beshere"),
            (-8924.651f, -149.9028f, 81.36338f, "Elwynn Forest, Northshire Ashley Blank"),
            (-8908.27f, -129.904f, 80.91549f, "Elwynn Forest, Northshire Mailbox"),
        ]),
        ("Expansion01",
        [
            (-1863, 5419, -10, "Terokkar, Shattrath"),
            (-2623, 4445, 36, "Terokkar, Stonebreaker Hold"),
            (-2712, 3743, -2, "Terokkar, Grind spot"),
            (-3561.1f, 639.1f, 3.1f, "Shadowmoon Valley, Black Temple"),
        ]),
        ("Northrend",
        [
            (5565, 5757, -75, "Sholazar, Nesingwary Base"),
            (6033, 5520, -77, "Sholazar, Spot 1"),
            (5649, 5956, -47, "Sholazar, Spot 2"),
            (5241, 5922, -60, "Sholazar, Spot 3"),
            (5194, 5709, -85, "Sholazar, Spot 4"),
        ]),
    ];
}
