using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using SharedLib.Data;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharedLib.Quests;

/// <summary>
/// A client's quests and what they point at: the quests (<c>Json/quests/&lt;client&gt;</c>, written by
/// <c>scripts/extract-questiedb.py</c>), the objects and items they name, every creature
/// (<c>dbc/&lt;client&gt;/creatures.json</c>) and every creature's spawns
/// (<c>npcspawnlocations/&lt;client&gt;</c>), all in world yards. Read once and kept; a client
/// without the files has an empty book. <see cref="PlacesOf"/> turns a quest into the places it
/// sends a character to, the one answer the Pathing Service and the bot both give.
/// </summary>
public sealed class QuestBook
{
    public const string QuestsFile = "quests.json";
    public const string ObjectsFile = "objects.json";
    public const string ItemsFile = "items.json";
    public const string CreaturesFile = "creatures.json";

    private static readonly JsonSerializerSettings Settings = new() { Converters = { new StringEnumConverter() } };

    public static QuestBook Empty { get; } = new([], [], [], [], new Dictionary<int, WorldSpot[]>());

    public QuestBook(IEnumerable<QuestRecord> quests, IEnumerable<QuestObject> objects, IEnumerable<QuestItem> items,
        IEnumerable<Creature> creatures, IReadOnlyDictionary<int, WorldSpot[]> npcSpawns)
    {
        Quests = quests.ToFrozenDictionary(q => q.Id);
        Objects = objects.ToFrozenDictionary(o => o.Id);
        Items = items.ToFrozenDictionary(i => i.Id);
        Creatures = creatures.GroupBy(c => c.Entry).ToFrozenDictionary(g => g.Key, g => g.First());
        NpcSpawns = npcSpawns.ToFrozenDictionary();
    }

    public FrozenDictionary<int, QuestRecord> Quests { get; }
    public FrozenDictionary<int, QuestObject> Objects { get; }
    public FrozenDictionary<int, QuestItem> Items { get; }
    public FrozenDictionary<int, Creature> Creatures { get; }

    /// <summary>Every spawn of every creature, by creature id.</summary>
    public FrozenDictionary<int, WorldSpot[]> NpcSpawns { get; }

    /// <summary>
    /// The book of <paramref name="data"/>'s client. A missing file is an empty part, said once at
    /// information level; a file that does not read throws, as a half-read book would mislead.
    /// </summary>
    public static QuestBook Load(DataConfig data, ILogger logger)
    {
        QuestRecord[] quests = Read<QuestRecord[]>(Path.Join(data.Quests, QuestsFile), logger) ?? [];
        QuestObject[] objects = Read<QuestObject[]>(Path.Join(data.Quests, ObjectsFile), logger) ?? [];
        QuestItem[] items = Read<QuestItem[]>(Path.Join(data.Quests, ItemsFile), logger) ?? [];
        Creature[] creatures = Read<Creature[]>(Path.Join(data.ExpDbc, CreaturesFile), logger) ?? [];

        Dictionary<int, List<WorldSpot>> spawns = [];
        if (Directory.Exists(data.NpcSpawnLocations))
        {
            foreach (string file in Directory.EnumerateFiles(data.NpcSpawnLocations, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int map))
                {
                    continue;
                }

                Dictionary<int, SpawnFilePoint[]>? read = Read<Dictionary<int, SpawnFilePoint[]>>(file, logger);
                foreach ((int entry, SpawnFilePoint[] points) in read ?? [])
                {
                    if (!spawns.TryGetValue(entry, out List<WorldSpot>? list))
                    {
                        spawns[entry] = list = [];
                    }

                    foreach (SpawnFilePoint p in points)
                    {
                        list.Add(new WorldSpot(map, p.X, p.Y));
                    }
                }
            }
        }

        return new QuestBook(quests, objects, items, creatures, spawns.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    /// <summary>The name of creature <paramref name="id"/>, or "creature <paramref name="id"/>" when the book has none.</summary>
    public string CreatureName(int id)
    {
        return Creatures.TryGetValue(id, out Creature c) && !string.IsNullOrEmpty(c.Name) ? c.Name : $"creature {id}";
    }

    public string ObjectName(int id)
    {
        return Objects.TryGetValue(id, out QuestObject? o) && o.Name.Length > 0 ? o.Name : $"object {id}";
    }

    public string ItemName(int id)
    {
        return Items.TryGetValue(id, out QuestItem? i) && i.Name.Length > 0 ? i.Name : $"item {id}";
    }

    public WorldSpot[] SpawnsOfCreature(int id)
    {
        return NpcSpawns.TryGetValue(id, out WorldSpot[]? at) ? at : [];
    }

    public WorldSpot[] SpawnsOfObject(int id)
    {
        return Objects.TryGetValue(id, out QuestObject? o) ? o.Spawns : [];
    }

    /// <summary>
    /// Every place <paramref name="quest"/> sends a character to: where it is given and taken back,
    /// each creature to kill, object to use, and for an item every creature that drops it, object
    /// that holds it and vendor that sells it, and each place to explore. An item that starts the
    /// quest is placed where it drops. Places the book has no spawns for are left out.
    /// </summary>
    public List<QuestPlace> PlacesOf(QuestRecord quest)
    {
        List<QuestPlace> places = [];

        void Npcs(QuestPlaceKind kind, IEnumerable<int> ids, int objective, string? item)
        {
            foreach (int id in ids)
            {
                string name = CreatureName(id);
                foreach (WorldSpot at in SpawnsOfCreature(id))
                {
                    places.Add(new QuestPlace(kind, name, id, objective, item, at));
                }
            }
        }

        void Objects(QuestPlaceKind kind, IEnumerable<int> ids, int objective, string? item)
        {
            foreach (int id in ids)
            {
                string name = ObjectName(id);
                foreach (WorldSpot at in SpawnsOfObject(id))
                {
                    places.Add(new QuestPlace(kind, name, id, objective, item, at));
                }
            }
        }

        void ItemSources(int itemId, int objective)
        {
            if (!Items.TryGetValue(itemId, out QuestItem? item))
            {
                return;
            }

            Npcs(QuestPlaceKind.Loot, item.NpcDrops, objective, item.Name);
            Objects(QuestPlaceKind.Gather, item.ObjectDrops, objective, item.Name);
            Npcs(QuestPlaceKind.Buy, item.Vendors, objective, item.Name);
        }

        Npcs(QuestPlaceKind.Start, quest.Starts.Npcs, -1, null);
        Objects(QuestPlaceKind.Start, quest.Starts.Objects, -1, null);
        foreach (int item in quest.Starts.Items)
        {
            ItemSources(item, -1);
        }

        for (int i = 0; i < quest.Objectives.Length; i++)
        {
            QuestGoal goal = quest.Objectives[i];
            switch (goal.Kind)
            {
                case QuestGoalKind.Kill:
                    Npcs(QuestPlaceKind.Kill, goal.Ids.Length > 0 ? goal.Ids : [goal.Id], i, null);
                    break;
                case QuestGoalKind.Object:
                    Objects(QuestPlaceKind.Use, [goal.Id], i, null);
                    break;
                case QuestGoalKind.Item:
                    ItemSources(goal.Id, i);
                    break;
            }
        }

        for (int i = 0; i < quest.Explore.Length; i++)
        {
            QuestExplore explore = quest.Explore[i];
            foreach (WorldSpot at in explore.Places)
            {
                places.Add(new QuestPlace(QuestPlaceKind.Explore, explore.Text ?? quest.Name, 0, i, null, at));
            }
        }

        Npcs(QuestPlaceKind.End, quest.Ends.Npcs, -1, null);
        Objects(QuestPlaceKind.End, quest.Ends.Objects, -1, null);
        return places;
    }

    /// <summary>Whether a character of <paramref name="side"/> may take <paramref name="quest"/> by its side alone.</summary>
    public static bool ForSide(QuestRecord quest, QuestSide side)
    {
        return quest.Side == QuestSide.Both || side == QuestSide.Both || quest.Side == side;
    }

    private static T? Read<T>(string path, ILogger logger) where T : class
    {
        if (!File.Exists(path))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{Path} not found: no quests from it", path);
            }
            return null;
        }

        return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), Settings);
    }

    // A spawn as npcspawnlocations keeps it; z is not kept here (0 means find the ground).
    private sealed record SpawnFilePoint(float X, float Y, float Z);
}
