using Newtonsoft.Json;

using System.IO;

using static Newtonsoft.Json.JsonConvert;
using static System.IO.File;
using static System.IO.Path;

public static class DataConfigMeta
{
    public const int Version = 14;
    public const string DefaultFileName = "data_config.json";
}

public sealed class DataConfig
{
    /// <summary>The navmesh tile folder under <see cref="PathInfo"/>.</summary>
    public const string NavmeshFolder = "navmesh";

    /// <summary>The named places (teleport targets and landmarks) in <see cref="Locations"/>.</summary>
    public const string TeleportLocationsFile = "teleport_locations.txt";

    /// <summary>WoW: Forever's route files, a folder of <see cref="Path"/> in a format of their own.</summary>
    public const string ForeverRouteFolder = Contracts.Pathing.SyncSets.ForeverRouteFolder;

    /// <summary>The folder under <see cref="Root"/> where an agent keeps what it last synced, and its backups.</summary>
    public const string SyncFolder = ".sync";

    /// <summary>
    /// Every era <see cref="ClientEra"/> gives a known client. A folder is named after an era, so a
    /// name from outside (a request) is taken only when it is one of these.
    /// </summary>
    public static readonly string[] Eras = ["precata", "cata", "mop", "forever"];

    public int Version = DataConfigMeta.Version;
    // Spelled as the repo folder is: "json" only resolves on a case-insensitive filesystem.
    public string Root { get; set; } = Join("..", "Json");

    [JsonIgnore]
    public string Class => Join(Root, "class");
    [JsonIgnore]
    public string Path => Join(Root, "path");
    /// <summary>WoW: Forever's route files (<see cref="ForeverRouteFolder"/> in <see cref="Path"/>).</summary>
    [JsonIgnore]
    public string ForeverRoutes => Join(Path, ForeverRouteFolder);
    [JsonIgnore]
    public string ExpDbc => Join(Root, "dbc", Exp);
    [JsonIgnore]
    public string PathInfo => Join(Root, "PathInfo");
    [JsonIgnore]
    public string Navmesh => Join(Root, "PathInfo", NavmeshFolder, ClientEra(Exp));
    [JsonIgnore]
    public string MPQ => Join(Root, "MPQ");
    [JsonIgnore]
    public string Road => RoadOf(ClientEra(Exp));
    [JsonIgnore]
    public string ExpArea => Join(Root, "area", Exp);
    [JsonIgnore]
    public string PPather => Join(Root, "PPather");
    [JsonIgnore]
    public string Screenshot => Join(Root, "cap");
    [JsonIgnore]
    public string ExpHistory => Join(Root, "History", Exp);
    [JsonIgnore]
    public string ExpExperience => Join(Root, "experience", Exp);
    [JsonIgnore]
    public string Leaflet => Join(Root, "leaflet", ClientEra(Exp));
    [JsonIgnore]
    public string AreaGrid => Join(Root, "area_grid", ClientEra(Exp));
    /// <summary>
    /// Named places in world coordinates (<see cref="TeleportLocationsFile"/>), for every client:
    /// each line carries its own map id, and a reader keeps the continents it knows.
    /// </summary>
    [JsonIgnore]
    public string Locations => Join(Root, "locations");

    /// <summary>What an agent last synced from the Pathing Service, and the local changes it kept aside.</summary>
    [JsonIgnore]
    public string Sync => Join(Root, SyncFolder);

    /// <summary>The authored roads and danger zones of <paramref name="era"/> (one of <see cref="Eras"/>), whatever client this is.</summary>
    public string RoadOf(string era) => Join(Root, "road", era);

    /// <summary>
    /// Groups clients whose art/geometry is shared, so geometry-derived assets
    /// (leaflet tiles, navmesh, area grids, cost zones) live in one folder per
    /// era instead of one per expansion. Mirrors PPather NavmeshSettings.MeshEra:
    /// vanilla..wotlk (incl. their legacy_* clients) read the same pre-Cataclysm
    /// world -> "precata"; Cataclysm rewrote it -> "cata".
    ///
    /// Mists is its OWN era rather than sharing Cataclysm's. The two are close but
    /// not equal - comparing 40 Azeroth ADTs between a 4.3.4 and a 5.4.8 client,
    /// 39 hash identically and one does not - and Mists adds a continent
    /// (HawaiiMainLand / Pandaria) that Cataclysm has no geometry for at all.
    /// Sharing the era would put both clients' bakes in the same
    /// `<era>/<continent>/<hash>/` directory, so a Mists-baked tile could be
    /// served to a Cataclysm user and vice versa.
    ///
    /// An unrecognized client gets its own era.
    /// </summary>
    public static string ClientEra(string client)
    {
        return client.ToLowerInvariant() switch
        {
            "vanilla" or "classic" or "som" or "tbc" or "bcc" or "wrath" or "wotlk"
                or "legacy_vanilla" or "legacy_tbc" or "legacy_wrath" => "precata",
            "cata" or "legacy_cata" => "cata",
            "mop" or "legacy_mop" => "mop",
            "forever" => "forever",
            _ => client.ToLowerInvariant(),
        };
    }
    /// <summary>
    /// Continents whose minimap art a later era reuses from an earlier one
    /// instead of regenerating it. See <see cref="TileEra"/>.
    /// </summary>
    public static readonly string[] TileSharedContinents = ["Northrend", "Expansion01"];

    /// <summary>
    /// Which era's leaflet tiles a continent is served from. Cataclysm rebuilt
    /// the old world but left Northrend and Outland essentially as they were, so
    /// those two read the precata tiles rather than duplicating ~1950 minimap
    /// blocks per era.
    ///
    /// Measured against a 4.3.4 client with 3.3.5 as the control: Northrend has
    /// the same 1131 blocks with 34/40 sampled images pixel-identical, and
    /// Expansion01 shares 800 blocks with 38/40 identical. Azeroth managed 8/40
    /// with 152 blocks that exist only in Cata, which is why the old world is
    /// not on this list.
    ///
    /// TILES ONLY - deliberately not applied to the navmesh. The same comparison
    /// found real geometry drift behind that near-identical art: Northrend ADTs
    /// 29_11 and 30_11 carry thousands of M2 collision triangles the pre-Cata
    /// client has no obstacle for, and most Outland ADTs differ in liquid. A
    /// stale minimap block just looks slightly dated; a stale navmesh routes the
    /// bot through something solid, so <see cref="Navmesh"/> stays per-era.
    ///
    /// Only the cata era inherits. Mists is deliberately excluded: its minimap art
    /// has not been compared against precata the way Cataclysm's was, and guessing
    /// would serve the wrong world's art. Measure first, then add "mop" here.
    /// </summary>
    public static string TileEra(string client, string continent)
    {
        string era = ClientEra(client);
        if (era != "cata")
        {
            return era;
        }

        foreach (string shared in TileSharedContinents)
        {
            if (shared.Equals(continent, System.StringComparison.OrdinalIgnoreCase))
                return "precata";
        }

        return era;
    }

    /// <summary>Leaflet tile directory for one continent, honouring <see cref="TileEra"/>.</summary>
    public string LeafletFor(string continent) =>
        Join(Root, "leaflet", TileEra(Exp, continent), continent);

    /// <summary>
    /// The continents whose tiles this client reads from an earlier era's folder, with that
    /// folder. A host serving tiles registers these before its general tile route, so the more
    /// specific route wins.
    /// </summary>
    public System.Collections.Generic.IEnumerable<(string Continent, string Folder)> SharedTileFolders()
    {
        foreach (string continent in TileSharedContinents)
        {
            if (TileEra(Exp, continent) != ClientEra(Exp))
            {
                yield return (continent, LeafletFor(continent));
            }
        }
    }

    /// <summary>
    /// Geometry eras that have minimap tiles and a leaflet map config. Mirrors the
    /// Configs blocks in MapAssets/wwwroot/script/leaflet-watch.js: a client whose
    /// era is missing there resolves to no config and renders a blank map, so the
    /// UI gates on this rather than on a hand-listed set of client versions.
    /// </summary>
    public static readonly string[] LeafletEras = ["precata", "cata", "mop"];

    /// <summary>
    /// Whether this client has a leaflet map. Era-based on purpose - listing client
    /// versions instead goes stale every time an era is added, which is exactly how
    /// the map stayed gated to vanilla+TBC long after wrath, Cataclysm and Mists
    /// worked. Only Retail and an unset version have no era here.
    /// </summary>
    public static bool HasLeaflet(string client) =>
        System.Array.IndexOf(LeafletEras, ClientEra(client)) >= 0;

    [JsonIgnore]
    public string Subzones => Join(Root, "subzones", Exp);

    [JsonIgnore]
    public string NpcSpawnLocations => Join(Root, "npcspawnlocations", Exp);

    [JsonIgnore]
    public string MailboxLocations => Join(Root, "mailboxlocations", Exp);

    /// <summary>
    /// The client's quests, the objects with their spawns and the items quests need
    /// (<c>quests.json</c>, <c>objects.json</c>, <c>items.json</c>), as
    /// <c>scripts/extract-questiedb.py</c> writes them.
    /// </summary>
    [JsonIgnore]
    public string Quests => Join(Root, "quests", Exp);

    [JsonIgnore]
    public string Mail => Join(Root, "mail");

    // at runtime - determined from the running exe file version
    [JsonIgnore]
    public string Exp { get; set; } = "wrath"; // hardcoded default

    public static DataConfig Load()
    {
        if (File.Exists(DataConfigMeta.DefaultFileName))
        {
            var loaded = DeserializeObject<DataConfig>(ReadAllText(DataConfigMeta.DefaultFileName));
            if (loaded.Version == DataConfigMeta.Version)
                return loaded;
        }

        return new DataConfig().Save();
    }

    public static DataConfig Load(string client)
    {
        if (File.Exists(DataConfigMeta.DefaultFileName))
        {
            var loaded = DeserializeObject<DataConfig>(ReadAllText(DataConfigMeta.DefaultFileName));
            if (loaded.Version == DataConfigMeta.Version)
            {
                loaded.Exp = client.ToLowerInvariant();
                return loaded;
            }
        }

        DataConfig newConfig = new DataConfig().Save();
        newConfig.Exp = client.ToLowerInvariant();
        return newConfig;
    }

    private DataConfig Save()
    {
        WriteAllText(DataConfigMeta.DefaultFileName, SerializeObject(this));

        return this;
    }

    /// <summary>
    /// Deletes the spot-graph cache (<c>PathInfo/&lt;era&gt;/&lt;continent&gt;</c>), which the
    /// legacy engine rebuilds as it searches. The navmesh tiles beside it are kept:
    /// they took hours to bake or a download to get, and deleting them is the bake
    /// API's explicit <c>DELETE Bake/Cache</c>, not a cache clear.
    /// </summary>
    public void DeletePPatherCache()
    {
        if (!Directory.Exists(PathInfo))
        {
            return;
        }

        foreach (string directory in Directory.GetDirectories(PathInfo))
        {
            if (GetFileName(directory).Equals(NavmeshFolder, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.Delete(directory, true);
        }
    }
}
