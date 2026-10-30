using SharedLib.Data;
using SharedLib.Quests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace SharedLib.Forever;

/// <summary>A place found for a task: who or what is there, where in world yards, and how far from where the search began.</summary>
public readonly record struct ForeverFound(string Name, WorldSpot At, float Yards)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} at {At.X:0}, {At.Y:0} on map {At.Map}, {Yards:0} yd away");
}

/// <summary>
/// Where a quest sends a character to hunt or gather: the camp nearest the character of the creatures
/// or objects it names, as world-yard bounds with a margin.
/// </summary>
/// <param name="Names">Who or what is there, as the quest names them.</param>
/// <param name="Places">The spawns inside.</param>
/// <param name="Yards">From where the search began to the nearest spawn.</param>
public sealed record ForeverCamp(string[] Names, int Places, Vector2 Min, Vector2 Max, float Yards)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{string.Join(", ", Names)}: {Places} {(Places == 1 ? "place" : "places")} in {Max.X - Min.X:0} by {Max.Y - Min.Y:0} yd, {Yards:0} yd away");
}

/// <summary>
/// The world data's answers for the tasks that name no point (<see cref="ForeverVisitTask"/>,
/// <see cref="ForeverQuestAreaTask"/>): the place nearest where the character stands, on its own
/// map, from the client's <see cref="QuestBook"/> - every NPC's spawns, its roles and the sides it
/// serves, and every quest's places (<see cref="QuestBook.PlacesOf"/>, the answer the Control Center
/// draws) - and that place as the travel or the roam a runner already carries out.
/// </summary>
public static class ForeverDestinations
{
    /// <summary>
    /// Spawns of a quest's creatures this near one another are one camp, or twice as far as they
    /// usually stand from the nearest of their kind where that is more: kobolds in a mine stand 11
    /// yards apart, peons asleep across a valley 80.
    /// </summary>
    public const float CampLinkYards = 60;

    /// <summary>
    /// A camp is at most this many spawns, those nearest the first: wolves a forest wide link up, and a
    /// roam of the whole forest is no camp.
    /// </summary>
    public const int CampMaxPlaces = 15;

    /// <summary>A camp's area reaches this far past its outermost spawns.</summary>
    public const float CampMarginYards = 15;

    /// <summary>The narrowest a camp's area is, so one spawn is still an area to roam.</summary>
    public const float CampMinYards = 40;

    /// <summary>
    /// What <paramref name="target"/> names, nearest (<paramref name="from"/>) on instance map
    /// <paramref name="mapId"/>: a role's NPCs who serve <paramref name="side"/>
    /// (<see cref="QuestSides.Serves"/>), an NPC by name, or where a quest is given or handed in.
    /// False with why when the data has none on this map.
    /// </summary>
    public static bool TryFind(QuestBook book, ForeverVisitTarget target, QuestSide side, int mapId, Vector2 from,
        out ForeverFound found, out string why)
    {
        found = default;
        IEnumerable<(string Name, WorldSpot At)> places;
        switch (target.Kind)
        {
            case ForeverVisitKind.Role:
                if (!NpcFlagsExtensions.ToDictionary().TryGetValue(target.Name.ToLowerInvariant(), out uint role))
                {
                    why = $"{target.Name} is not an NPC role";
                    return false;
                }
                places = book.Creatures.Values
                    .Where(c => ((uint)c.NpcFlag & role) != 0 && QuestSides.Serves(c, side) &&
                                (target.Title is null || c.SubName?.Contains(target.Title, StringComparison.OrdinalIgnoreCase) == true))
                    .SelectMany(c => book.SpawnsOfCreature(c.Entry).Select(at => (c.Name, at)));
                break;
            case ForeverVisitKind.Npc:
                places = book.Creatures.Values
                    .Where(c => string.Equals(c.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(c => book.SpawnsOfCreature(c.Entry).Select(at => (c.Name, at)));
                break;
            default:
                if (!book.Quests.TryGetValue(target.QuestId, out QuestRecord? quest))
                {
                    why = $"quest {target.QuestId} is not in the quest data";
                    return false;
                }
                QuestPlaceKind kind = target.Kind == ForeverVisitKind.QuestStart ? QuestPlaceKind.Start : QuestPlaceKind.End;
                places = book.PlacesOf(quest).Where(p => p.Kind == kind).Select(p => (p.Name, p.At));
                break;
        }

        bool any = false;
        foreach ((string name, WorldSpot at) in places)
        {
            if (at.Map != mapId)
                continue;
            float yards = Vector2.Distance(from, new Vector2(at.X, at.Y));
            if (!any || yards < found.Yards)
                found = new ForeverFound(name, at, yards);
            any = true;
        }

        if (!any)
        {
            why = target.Kind == ForeverVisitKind.Role
                ? $"no {target.Name}{(target.Title is null ? string.Empty : $" titled {target.Title}")} who serves {Side(side)} is on this continent"
                : $"{target} is not on this continent";
            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>
    /// The camp quest <paramref name="questId"/> sends a character to, nearest (<paramref name="from"/>)
    /// on instance map <paramref name="mapId"/>: among the creatures it asks to kill or that drop what it
    /// needs, else the objects it asks to use or that hold what it needs, else the places it asks to
    /// explore, the spawn nearest the character and the spawns linked to it (<see cref="CampLinkYards"/>),
    /// at most <see cref="CampMaxPlaces"/> of them. False with why when the quest has none on this map.
    /// </summary>
    public static bool TryCamp(QuestBook book, int questId, int mapId, Vector2 from, out ForeverCamp camp, out string why)
    {
        camp = null!;
        if (!book.Quests.TryGetValue(questId, out QuestRecord? quest))
        {
            why = $"quest {questId} is not in the quest data";
            return false;
        }

        List<QuestPlace> all = book.PlacesOf(quest);
        List<QuestPlace> here = [];
        foreach (QuestPlaceKind[] kinds in (QuestPlaceKind[][])[[QuestPlaceKind.Kill, QuestPlaceKind.Loot],
                     [QuestPlaceKind.Use, QuestPlaceKind.Gather], [QuestPlaceKind.Explore]])
        {
            here = [.. all.Where(p => kinds.Contains(p.Kind) && p.At.Map == mapId)];
            if (here.Count > 0)
                break;
        }

        if (here.Count == 0)
        {
            why = $"quest {questId} ({quest.Name}) sends no one to hunt or gather on this continent";
            return false;
        }

        // The spawn nearest the character, and every spawn linked to it by spawns this near one another.
        Vector2[] at = [.. here.Select(p => new Vector2(p.At.X, p.At.Y))];
        int nearest = 0;
        for (int i = 1; i < at.Length; i++)
        {
            if (Vector2.DistanceSquared(from, at[i]) < Vector2.DistanceSquared(from, at[nearest]))
                nearest = i;
        }

        float link = Math.Max(CampLinkYards, 2 * MedianSpacing(at));
        bool[] taken = new bool[at.Length];
        Queue<int> next = new([nearest]);
        taken[nearest] = true;
        List<int> inside = [];
        while (next.TryDequeue(out int i))
        {
            inside.Add(i);
            for (int j = 0; j < at.Length; j++)
            {
                if (!taken[j] && Vector2.Distance(at[i], at[j]) <= link)
                {
                    taken[j] = true;
                    next.Enqueue(j);
                }
            }
        }

        inside = [.. inside.OrderBy(i => Vector2.DistanceSquared(at[i], at[nearest])).Take(CampMaxPlaces)];
        Vector2 min = new(inside.Min(i => at[i].X), inside.Min(i => at[i].Y));
        Vector2 max = new(inside.Max(i => at[i].X), inside.Max(i => at[i].Y));
        Vector2 margin = new(CampMarginYards);
        min -= margin;
        max += margin;
        Vector2 middle = (min + max) / 2;
        Vector2 half = Vector2.Max((max - min) / 2, new Vector2(CampMinYards / 2));
        string[] names = [.. inside.Select(i => here[i].Name).Distinct()];
        camp = new ForeverCamp(names, inside.Count, middle - half, middle + half, Vector2.Distance(from, at[nearest]));
        why = string.Empty;
        return true;
    }

    /// <summary>
    /// The travel to what <paramref name="task"/> visits, found from (<paramref name="mapId"/>,
    /// <paramref name="from"/>) for a character of <paramref name="side"/>: a <see cref="ForeverTravelTask"/>
    /// to its spot on the zone map it lies in.
    /// </summary>
    public static bool TryTravel(QuestBook book, WorldMapAreaDB maps, ForeverVisitTask task, QuestSide side, int mapId,
        Vector2 from, out ForeverTravelTask travel, out ForeverFound found, out string why)
    {
        travel = null!;
        if (!TryFind(book, task.Target, side, mapId, from, out found, out why))
            return false;
        if (ForeverMapPoint.FromWorld(maps, mapId, found.At.X, found.At.Y) is not { } to)
        {
            why = $"{found} lies on no map of the map data";
            return false;
        }

        travel = new ForeverTravelTask(to, [], task.Pulls, ForeverUntil.Never);
        return true;
    }

    /// <summary>
    /// The roam of the camp <paramref name="task"/>'s quest sends a character to, found from
    /// (<paramref name="mapId"/>, <paramref name="from"/>): a <see cref="ForeverRoamAreaTask"/> of the
    /// camp's rectangle on the one map that holds it, with the task's pulls, end and seed.
    /// </summary>
    public static bool TryRoam(QuestBook book, WorldMapAreaDB maps, ForeverQuestAreaTask task, int mapId, Vector2 from,
        out ForeverRoamAreaTask roam, out ForeverCamp camp, out string why)
    {
        roam = null!;
        if (!TryCamp(book, task.QuestId, mapId, from, out camp, out why))
            return false;
        if (ForeverMapPoint.OnOneMap(maps, mapId, [camp.Min, camp.Max]) is not [ForeverMapPoint a, ForeverMapPoint b])
        {
            why = $"the camp of quest {task.QuestId} ({camp}) lies on no one map of the map data";
            return false;
        }

        roam = new ForeverRoamAreaTask(ForeverArea.Rectangle(a.UIMapId, (a.X, a.Y), (b.X, b.Y)), task.Pulls, task.Until)
        {
            Seed = task.Seed,
        };
        return true;
    }

    // How far a spawn usually stands from the nearest other one; 0 for one spawn.
    private static float MedianSpacing(Vector2[] at)
    {
        if (at.Length < 2)
            return 0;
        float[] nearest = new float[at.Length];
        for (int i = 0; i < at.Length; i++)
        {
            nearest[i] = float.MaxValue;
            for (int j = 0; j < at.Length; j++)
            {
                if (i != j)
                    nearest[i] = Math.Min(nearest[i], Vector2.Distance(at[i], at[j]));
            }
        }
        Array.Sort(nearest);
        return nearest[nearest.Length / 2];
    }

    private static string Side(QuestSide side) => side switch
    {
        QuestSide.Alliance => "the Alliance",
        QuestSide.Horde => "the Horde",
        _ => "both sides",
    };
}
