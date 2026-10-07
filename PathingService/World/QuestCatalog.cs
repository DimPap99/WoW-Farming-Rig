using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.Logging;

using PathingService.Hosting;

using SharedLib;
using SharedLib.Quests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PathingService.World;

/// <summary>
/// The served client's quests (<see cref="QuestBook"/>, <c>Json/quests/&lt;client&gt;</c>): those
/// given in a zone, those found by name, and one quest with every place it sends a character to.
/// Read once; a read that fails is not kept, so the next request reads again. A client without
/// quest files has none.
/// </summary>
public sealed class QuestCatalog(DataConfig data, WorldMapAreaDB maps, ILogger<QuestCatalog> logger)
{
    /// <summary>Quests found by name are listed up to this many.</summary>
    public const int MaxFound = 50;

    private readonly Lazy<Loaded> loaded = new(() => Load(data, logger), LazyThreadSafetyMode.PublicationOnly);

    public QuestBook Book => loaded.Value.Book;

    /// <summary>The quests given inside zone <paramref name="uiMapId"/>'s area, by level.</summary>
    public StoreResult<QuestSummary[]> InZone(int uiMapId)
    {
        if (!maps.TryGet(uiMapId, out WorldMapArea zone))
        {
            return StoreResult<QuestSummary[]>.Invalid($"uiMap {uiMapId} is not in the {data.Exp} map data");
        }

        WorldMapArea hitbox = maps.HitBoxOf(zone);
        List<QuestSummary> found = [];
        foreach (Giving giving in loaded.Value.Givings)
        {
            foreach (QuestPlace place in giving.Places)
            {
                if (place.At.Map == zone.MapID && WorldMapAreaDB.Holds(hitbox, place.At.X, place.At.Y))
                {
                    found.Add(Summary(giving.Quest, place));
                    break;
                }
            }
        }

        return StoreResult<QuestSummary[]>.Done([.. found.OrderBy(q => q.MinLevel).ThenBy(q => q.Level).ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Quests whose name holds <paramref name="name"/>, at most <see cref="MaxFound"/>, by name.</summary>
    public StoreResult<QuestSummary[]> Search(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
        {
            return StoreResult<QuestSummary[]>.Invalid("a name to search for needs two letters or more");
        }

        string text = name.Trim();
        QuestSummary[] found = [.. loaded.Value.Givings
            .Where(g => g.Quest.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Quest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Quest.MinLevel)
            .Take(MaxFound)
            .Select(g => Summary(g.Quest, g.Places.Count > 0 ? g.Places[0] : null))];
        return StoreResult<QuestSummary[]>.Done(found);
    }

    /// <summary>Quest <paramref name="id"/> with every place it sends a character to, by part.</summary>
    public StoreResult<QuestDetail> Get(int id)
    {
        if (!Book.Quests.TryGetValue(id, out QuestRecord? quest))
        {
            return StoreResult<QuestDetail>.NotFound;
        }

        // One part per who-or-what and what for, in the order the places come: start, objectives, end.
        List<(QuestPart Part, List<QuestSpot> Spots)> parts = [];
        Dictionary<(QuestPlaceKind, int, int, string?, string), int> partOf = [];
        foreach (QuestPlace place in Book.PlacesOf(quest))
        {
            (QuestPlaceKind, int, int, string?, string) key = (place.Kind, place.Source, place.Objective, place.Item, place.Name);
            if (!partOf.TryGetValue(key, out int index))
            {
                partOf[key] = index = parts.Count;
                parts.Add((new QuestPart(Kind(place.Kind), place.Name, place.Source, place.Item, place.Objective, []), []));
            }

            parts[index].Spots.Add(new QuestSpot(place.At.Map, new Point3(place.At.X, place.At.Y, 0)));
        }

        QuestPart[] filled = [.. parts.Select(p => p.Part with { Places = [.. p.Spots] })];

        return StoreResult<QuestDetail>.Done(new QuestDetail(quest.Id, quest.Name, quest.Level, quest.MinLevel, quest.MaxLevel,
            quest.Side.ToString(), quest.Races, quest.Classes, quest.Text, filled, quest.PreAll, quest.PreAny, quest.Next,
            quest.Repeatable));
    }

    private static QuestSummary Summary(QuestRecord quest, QuestPlace? giver)
    {
        return new QuestSummary(quest.Id, quest.Name, quest.Level, quest.MinLevel, quest.Side.ToString(), giver?.Name,
            giver is QuestPlace at ? new Point3(at.At.X, at.At.Y, 0) : null, giver?.At.Map ?? -1);
    }

    private static QuestPartKind Kind(QuestPlaceKind kind) => kind switch
    {
        QuestPlaceKind.Start => QuestPartKind.Start,
        QuestPlaceKind.End => QuestPartKind.End,
        QuestPlaceKind.Kill => QuestPartKind.Kill,
        QuestPlaceKind.Use => QuestPartKind.Use,
        QuestPlaceKind.Loot => QuestPartKind.Loot,
        QuestPlaceKind.Gather => QuestPartKind.Gather,
        QuestPlaceKind.Buy => QuestPartKind.Buy,
        _ => QuestPartKind.Explore,
    };

    private static Loaded Load(DataConfig data, ILogger logger)
    {
        QuestBook book = QuestBook.Load(data, logger);
        List<Giving> givings = new(book.Quests.Count);
        foreach (QuestRecord quest in book.Quests.Values.OrderBy(q => q.Id))
        {
            givings.Add(new Giving(quest, [.. book.PlacesOf(quest with { Objectives = [], Explore = [], Ends = new() })
                .Where(p => p.Kind == QuestPlaceKind.Start)]));
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Quests of {Client}: {Quests} quests, {Objects} objects, {Items} items", data.Exp,
                book.Quests.Count, book.Objects.Count, book.Items.Count);
        }

        return new Loaded(book, givings);
    }

    // A quest and where it is given, kept so a zone's list need not place every objective.
    private sealed record Giving(QuestRecord Quest, List<QuestPlace> Places);

    private sealed record Loaded(QuestBook Book, List<Giving> Givings);
}
