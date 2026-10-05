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
    private ZoneInfo[]? zones;

    public WorldInfo Info()
    {
        ContinentDB.Init(maps.Values);

        PPatherService.NavmeshCoverage[] counted = coverage.Read();
        ContinentInfo[] continents = new ContinentInfo[counted.Length];
        for (int i = 0; i < counted.Length; i++)
        {
            continents[i] = new ContinentInfo(counted[i].Continent, (int)ContinentDB.NameToId[counted[i].Continent],
                counted[i].Tiles);
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

        WorldMapArea hitbox = maps.GetByAreaIdHit(zone.AreaID);
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
            return creatures ??= ReadOrEmpty<Creature[]>(Path.Join(data.ExpDbc, CreaturesFile)) ?? [];
        }
    }

    private FrozenDictionary<int, Vector3[]> Spawns(int mapId)
    {
        lock (gate)
        {
            if (!spawnsByMap.TryGetValue(mapId, out FrozenDictionary<int, Vector3[]>? spawns))
            {
                Dictionary<int, Vector3[]>? read = ReadOrEmpty<Dictionary<int, Vector3[]>>(
                    Path.Join(data.NpcSpawnLocations, $"{mapId}.json"));
                spawns = (read ?? []).ToFrozenDictionary();
                spawnsByMap[mapId] = spawns;
            }

            return spawns;
        }
    }

    // Both files are optional enrichment generated per client; a client without them has no NPCs here.
    private T? ReadOrEmpty<T>(string file) where T : class
    {
        if (!File.Exists(file))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{File} not found; NPC routes have no data for it", file);
            }
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "{File} is not readable", file);
            return null;
        }
    }
}
