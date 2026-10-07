using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.Logging;

using PathingService.Hosting;

using ServiceClients.Pathing;

using SharedLib;
using SharedLib.Quests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PathingService.World;

/// <summary>
/// The served client's quests (<see cref="QuestBook"/>, <c>Json/quests/&lt;client&gt;</c>): those
/// of a zone, those found by name, and one quest with every place it sends a character to. Read
/// once; a read that fails is not kept, so the next request reads again. A client without quest
/// files has none.
/// </summary>
/// <remarks>
/// A quest belongs to the zone its quest log lists it under, a sub-area's (Northshire Valley)
/// counting for its zone; a class, profession or event quest (a sort, not a zone) to the zone its
/// giver stands in, by the rule the Control Center names a point's zone with
/// (<see cref="ZoneMath.ZoneAt"/>). Not to every zone whose map holds its giver: Elwynn Forest's
/// map holds all of Stormwind, and listed Stormwind's quests too.
/// </remarks>
public sealed class QuestCatalog(DataConfig data, WorldMapAreaDB maps, WorldCatalog world, ILogger<QuestCatalog> logger)
{
    /// <summary>Quests found by name are listed up to this many.</summary>
    public const int MaxFound = 50;

    private readonly Lazy<Loaded> loaded = new(() => Load(data, maps, world, logger), LazyThreadSafetyMode.PublicationOnly);

    public QuestBook Book => loaded.Value.Book;

    /// <summary>The quests of zone <paramref name="uiMapId"/>, by level; a continent's map lists every quest given on it.</summary>
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
            QuestPlace? inside = null;
            foreach (QuestPlace place in giving.Places)
            {
                if (place.At.Map == zone.MapID && WorldMapAreaDB.Holds(hitbox, place.At.X, place.At.Y))
                {
                    inside = place;
                    break;
                }
            }

            if (zone.AreaID == 0 ? inside is not null : giving.ZoneArea == zone.AreaID)
            {
                // Where it is given in the zone, else where it is given at all.
                found.Add(Summary(giving.Quest, inside ?? (giving.Places.Count > 0 ? giving.Places[0] : null)));
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
        if (!loaded.Value.ById.TryGetValue(id, out Giving? giving))
        {
            return StoreResult<QuestDetail>.NotFound;
        }

        // One part per who-or-what and what for, in the order the places come: start, objectives, end.
        QuestRecord quest = giving.Quest;
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
        int uiMapId = giving.ZoneArea == 0 ? 0 : maps.GetByAreaId(giving.ZoneArea).UIMapId;

        return StoreResult<QuestDetail>.Done(new QuestDetail(quest.Id, quest.Name, uiMapId, quest.Level, quest.MinLevel, quest.MaxLevel,
            quest.Side.ToString(), quest.Races, quest.Classes, quest.Text, filled, [.. quest.PreAll.Select(Link)],
            [.. quest.PreAny.Select(Link)], quest.Next == 0 ? null : Link(quest.Next), quest.Repeatable));
    }

    private QuestLink Link(int id)
    {
        return new QuestLink(id, Book.Quests.TryGetValue(id, out QuestRecord? quest) ? quest.Name : $"quest {id}");
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

    private static Loaded Load(DataConfig data, WorldMapAreaDB maps, WorldCatalog world, ILogger logger)
    {
        QuestBook book = QuestBook.Load(data, logger);
        ZoneInfo[] zones = world.Zones();
        List<Giving> givings = new(book.Quests.Count);
        foreach (QuestRecord quest in book.Quests.Values.OrderBy(q => q.Id))
        {
            List<QuestPlace> starts = [.. book.PlacesOf(quest with { Objectives = [], Explore = [], Ends = new() })
                .Where(p => p.Kind == QuestPlaceKind.Start)];
            givings.Add(new Giving(quest, starts, ZoneOf(quest, starts, maps, zones)));
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Quests of {Client}: {Quests} quests, {Objects} objects, {Items} items", data.Exp,
                book.Quests.Count, book.Objects.Count, book.Items.Count);
        }

        return new Loaded(book, givings, givings.ToDictionary(g => g.Quest.Id));
    }

    // The quest log's zone, a sub-area's counting for its zone; for a sort, or an area the map data
    // lacks, the zone its giver stands in; 0 for none.
    private static int ZoneOf(QuestRecord quest, List<QuestPlace> starts, WorldMapAreaDB maps, ZoneInfo[] zones)
    {
        if (quest.Zone > 0 && maps.TryGetZoneOfArea(quest.Zone, out WorldMapArea zone))
        {
            return zone.AreaID;
        }

        foreach (QuestPlace place in starts)
        {
            if (ZoneMath.ZoneAt(zones, place.At.Map, place.At.X, place.At.Y) is { AreaId: > 0 } at)
            {
                return at.AreaId;
            }
        }

        return 0;
    }

    // A quest, where it is given (kept so a zone's list need not place every objective), and its zone's area id.
    private sealed record Giving(QuestRecord Quest, List<QuestPlace> Places, int ZoneArea);

    private sealed record Loaded(QuestBook Book, List<Giving> Givings, Dictionary<int, Giving> ById);
}
