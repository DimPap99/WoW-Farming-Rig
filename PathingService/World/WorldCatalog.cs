using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using PathingService.Hosting;
using PathingService.Status;

using PPather;

using SharedLib;
using SharedLib.Data;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace PathingService.World;

/// <summary>
/// What the served client's world is made of: continents, zone maps with their bounds, and the
/// NPCs of a type in a zone. Read from the client's DBC exports and spawn dumps, the files the
/// bot reads, and cached once read.
/// </summary>
public sealed class WorldCatalog(DataConfig data, WorldMapAreaDB maps, CoverageCache coverage, ILogger<WorldCatalog> logger)
{
    public const string CreaturesFile = "creatures.json";

    private readonly Lock gate = new();
    private Creature[]? creatures;
    private readonly Dictionary<int, FrozenDictionary<int, Vector3[]>> spawnsByMap = [];
    private readonly Dictionary<int, AreaNodes?> areaFiles = [];
    private readonly Dictionary<int, Vector3[]> mailboxesByMap = [];
    private ZoneInfo[]? zones;

    public WorldInfo Info()
    {
        ContinentDB.Init(maps.Values);

        PPatherService.NavmeshCoverage[] counted = coverage.Read();
        ContinentInfo[] continents = new ContinentInfo[counted.Length];
        for (int i = 0; i < counted.Length; i++)
        {
            continents[i] = new ContinentInfo(counted[i].Continent, (int)ContinentDB.NameToId[counted[i].Continent],
                counted[i].Tiles, MapArtTable.For(data, counted[i].Continent));
        }

        return new WorldInfo(data.Exp, DataConfig.ClientEra(data.Exp), DataConfig.HasLeaflet(data.Exp), continents, Zones(),
            new Dictionary<string, uint>(NpcFlagsExtensions.ToDictionary()));
    }

    /// <summary>Every zone map of the client, by name.</summary>
    public ZoneInfo[] Zones()
    {
        lock (gate)
        {
            return zones ??= [.. maps.Values
                .OrderBy(a => a.AreaName, StringComparer.OrdinalIgnoreCase)
                .Select(a => new ZoneInfo(a.UIMapId, a.AreaID, a.AreaName ?? string.Empty, a.Continent ?? string.Empty, a.MapID,
                    a.LocLeft, a.LocRight, a.LocTop, a.LocBottom))];
        }
    }

    /// <summary>
    /// NPCs of <paramref name="type"/> whose first spawn lies inside the zone's area, by name: the
    /// rule the bot's NPC routing uses (the zone's hit box, so a city's NPCs count for the city).
    /// </summary>
    public StoreResult<NpcInfo[]> Npcs(int uiMapId, string? type)
    {
        if (!maps.TryGet(uiMapId, out WorldMapArea zone))
        {
            return StoreResult<NpcInfo[]>.Invalid($"uiMap {uiMapId} is not in the {data.Exp} map data");
        }

        if (!TryParseType(type, out NpcFlags flag))
        {
            return StoreResult<NpcInfo[]>.Invalid(
                $"'{type}' is not an NPC type; use one of {string.Join(", ", NpcFlagsExtensions.ToDictionary().Keys.Order())}");
        }

        WorldMapArea hitbox = maps.HitBoxOf(zone);
        FrozenDictionary<int, Vector3[]> spawns = Spawns(zone.MapID);

        List<NpcInfo> found = [];
        foreach (Creature npc in Creatures())
        {
            if ((npc.NpcFlag & flag) == 0 || string.IsNullOrEmpty(npc.Name) ||
                !spawns.TryGetValue(npc.Entry, out Vector3[]? at) || at.Length == 0)
            {
                continue;
            }

            Vector3 map = WorldMapAreaDB.ToMap_FlipXY(at[0], hitbox);
            if (map.X is > 0 and < 100 && map.Y is > 0 and < 100)
            {
                found.Add(new NpcInfo(npc.Entry, npc.Name, new Point3(at[0].X, at[0].Y, at[0].Z)));
            }
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return StoreResult<NpcInfo[]>.Done([.. found]);
    }

    /// <summary>Creatures searched for by name are listed up to this many.</summary>
    public const int MaxFound = 50;

    /// <summary>
    /// Creatures on the continent <paramref name="mapId"/> whose name holds <paramref name="name"/>,
    /// where each first spawns, by name; at most <see cref="MaxFound"/>.
    /// </summary>
    public StoreResult<NpcInfo[]> Search(int mapId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
        {
            return StoreResult<NpcInfo[]>.Invalid("a name to search for needs two letters or more");
        }

        string text = name.Trim();
        FrozenDictionary<int, Vector3[]> spawns = Spawns(mapId);
        NpcInfo[] found = [.. Creatures()
            .Where(c => !string.IsNullOrEmpty(c.Name) && c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                        spawns.TryGetValue(c.Entry, out Vector3[]? at) && at.Length > 0)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFound)
            .Select(c => new NpcInfo(c.Entry, c.Name, Point(spawns[c.Entry][0])))];
        return StoreResult<NpcInfo[]>.Done(found);
    }

    /// <summary>One of the <see cref="SpawnLayers"/> inside the zone's area, every spawn or node of it, in world yards.</summary>
    public StoreResult<SpawnPoint[]> Spawns(int uiMapId, string? layer)
    {
        if (!maps.TryGet(uiMapId, out WorldMapArea zone))
        {
            return StoreResult<SpawnPoint[]>.Invalid($"uiMap {uiMapId} is not in the {data.Exp} map data");
        }

        WorldMapArea hitbox = maps.HitBoxOf(zone);
        bool Inside(Vector3 at)
        {
            Vector3 map = WorldMapAreaDB.ToMap_FlipXY(at, hitbox);
            return map.X is > 0 and < 100 && map.Y is > 0 and < 100;
        }

        List<SpawnPoint> found = [];
        switch (layer)
        {
            case SpawnLayers.Elite:
                AddCreatures(found, zone.MapID, Creatures().Where(c => c.Rank > 0), Inside, Rank);
                break;
            case SpawnLayers.Skinnable:
                HashSet<int> skinnable = [.. AreaFile(zone.AreaID)?.skinnable ?? []];
                AddCreatures(found, zone.MapID, Creatures().Where(c => skinnable.Contains(c.Entry)), Inside, _ => "Skinnable");
                break;
            case SpawnLayers.Herb or SpawnLayers.Vein:
                AreaNodes? nodes = AreaFile(zone.AreaID);
                foreach ((string kind, List<AreaNode> list) in (layer == SpawnLayers.Herb ? nodes?.herb : nodes?.vein) ?? [])
                {
                    foreach (AreaNode node in list)
                    {
                        foreach (List<float> xy in node.coords ?? [])
                        {
                            if (xy.Count >= 2)
                            {
                                Vector3 at = WorldMapAreaDB.ToWorld_FlipXY(new Vector3(xy[0], xy[1], 0), zone);
                                found.Add(new SpawnPoint(node.id, node.name ?? kind, Point(at), string.Create(CultureInfo.InvariantCulture, $"Skill {node.level}")));
                            }
                        }
                    }
                }
                break;
            case SpawnLayers.Mailbox:
                foreach (Vector3 at in Mailboxes(zone.MapID).Where(Inside))
                {
                    found.Add(new SpawnPoint(0, "Mailbox", Point(at)));
                }
                break;
            default:
                return StoreResult<SpawnPoint[]>.Invalid($"'{layer}' is not a layer; use one of {string.Join(", ", SpawnLayers.All)}");
        }

        return StoreResult<SpawnPoint[]>.Done([.. found]);
    }

    private void AddCreatures(List<SpawnPoint> found, int mapId, IEnumerable<Creature> wanted, Func<Vector3, bool> inside,
        Func<Creature, string> what)
    {
        FrozenDictionary<int, Vector3[]> spawns = Spawns(mapId);
        foreach (Creature creature in wanted)
        {
            if (string.IsNullOrEmpty(creature.Name) || !spawns.TryGetValue(creature.Entry, out Vector3[]? at))
            {
                continue;
            }

            string detail = string.Create(CultureInfo.InvariantCulture,
                $"{what(creature)}, level {creature.MinLevel}{(creature.MaxLevel > creature.MinLevel ? $"-{creature.MaxLevel}" : string.Empty)}");
            foreach (Vector3 spawn in at.Where(inside))
            {
                found.Add(new SpawnPoint(creature.Entry, creature.Name, Point(spawn), detail));
            }
        }
    }

    // The creature template's rank: 1 elite, 2 rare elite, 3 boss, 4 rare.
    private static string Rank(Creature creature) => creature.Rank switch
    {
        2 => "Rare elite",
        3 => "Boss",
        4 => "Rare",
        _ => "Elite",
    };

    private static Point3 Point(Vector3 v) => new(v.X, v.Y, v.Z);


    // The zone's Wowhead file: its herbs, veins and skinnable creatures; null when the client has none.
    private AreaNodes? AreaFile(int areaId)
    {
        lock (gate)
        {
            if (!areaFiles.TryGetValue(areaId, out AreaNodes? nodes) &&
                TryRead(Path.Join(data.ExpArea, $"{areaId}.json"), out nodes))
            {
                areaFiles[areaId] = nodes;
            }

            return nodes;
        }
    }

    private Vector3[] Mailboxes(int mapId)
    {
        lock (gate)
        {
            if (mailboxesByMap.TryGetValue(mapId, out Vector3[]? mailboxes))
            {
                return mailboxes;
            }

            if (!TryRead(Path.Join(data.MailboxLocations, $"{mapId}.json"), out mailboxes))
            {
                return [];
            }

            return mailboxesByMap[mapId] = mailboxes ?? [];
        }
    }

    // The fields of WowheadDB's Area this reads, named as the files name them (Newtonsoft binds fields by name).
#pragma warning disable IDE1006, CS0649
    private sealed class AreaNodes
    {
        public Dictionary<string, List<AreaNode>>? herb;
        public Dictionary<string, List<AreaNode>>? vein;
        public int[]? skinnable;
    }

    private sealed class AreaNode
    {
        public List<List<float>>? coords;
        public int level;
        public string? name;
        public int id;
    }
#pragma warning restore IDE1006, CS0649

    private static bool TryParseType(string? type, out NpcFlags flag)
    {
        flag = NpcFlags.None;
        return !string.IsNullOrWhiteSpace(type) &&
               Enum.TryParse(type, ignoreCase: true, out flag) &&
               flag != NpcFlags.None &&
               Enum.IsDefined(flag);
    }

    private Creature[] Creatures()
    {
        lock (gate)
        {
            if (creatures is null && TryRead(Path.Join(data.ExpDbc, CreaturesFile), out Creature[]? read))
            {
                creatures = read ?? [];
            }

            return creatures ?? [];
        }
    }

    private FrozenDictionary<int, Vector3[]> Spawns(int mapId)
    {
        lock (gate)
        {
            if (spawnsByMap.TryGetValue(mapId, out FrozenDictionary<int, Vector3[]>? spawns))
            {
                return spawns;
            }

            if (!TryRead(Path.Join(data.NpcSpawnLocations, $"{mapId}.json"), out Dictionary<int, Vector3[]>? read))
            {
                return FrozenDictionary<int, Vector3[]>.Empty;
            }

            return spawnsByMap[mapId] = (read ?? []).ToFrozenDictionary();
        }
    }

    // The files are optional enrichment generated per client: a client without one has nothing from
    // it, and that is kept (true, null). One that is there but cannot be read - a script rewriting it -
    // is not kept (false): the next request reads it again.
    private bool TryRead<T>(string file, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(file))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{File} not found; NPC routes have no data for it", file);
            }
            return true;
        }

        try
        {
            value = JsonConvert.DeserializeObject<T>(File.ReadAllText(file));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "{File} could not be read; it is read again on the next request", file);
            return false;
        }
    }
}
