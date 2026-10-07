namespace Contracts.Pathing;

/// <summary>What one part of a quest is: where it is given or handed in, or one thing it asks.</summary>
public enum QuestPartKind
{
    /// <summary>Where the quest is given.</summary>
    Start,
    /// <summary>Where it is handed in.</summary>
    End,
    /// <summary>A creature to kill.</summary>
    Kill,
    /// <summary>An object to use.</summary>
    Use,
    /// <summary>A creature that drops an item the quest needs.</summary>
    Loot,
    /// <summary>An object that holds an item the quest needs.</summary>
    Gather,
    /// <summary>A vendor that sells an item the quest needs.</summary>
    Buy,
    /// <summary>A place to go.</summary>
    Explore
}

/// <summary>A quest in a list, with where it is given.</summary>
/// <remarks>A zone lists the quests its quest log lists (a sub-area's, such as a valley's, count for its zone), and a class
/// or profession quest where its giver stands.</remarks>
/// <param name="Side">Alliance, Horde or Both.</param>
/// <param name="Giver">Who or what gives it; null when the data names no giver.</param>
/// <param name="GiverAt">Where, in world yards: in the zone asked for, or the giver's first place.</param>
/// <param name="GiverMapId">The map <paramref name="GiverAt"/> is on.</param>
public sealed record QuestSummary(int Id, string Name, int Level, int MinLevel, string Side, string? Giver, Point3? GiverAt,
    int GiverMapId);

/// <summary>A quest with everything it asks and every place it sends a character to.</summary>
/// <param name="UiMapId">The zone map of the zone the quest log lists it under (a class or profession quest: the zone it is
/// given in); 0 when none is known.</param>
/// <param name="MaxLevel">The level past which it is no longer offered; 0 for none.</param>
/// <param name="Races">The races that may take it, as the client's mask; 0 for every race.</param>
/// <param name="Classes">The classes that may take it, as the client's mask; 0 for every class.</param>
/// <param name="Text">What the quest log says to do.</param>
/// <param name="Parts">Where it is given and handed in, and each thing it asks, with its places.</param>
/// <param name="PreAll">Quests that must all be done first.</param>
/// <param name="PreAny">Quests of which one must be done first.</param>
/// <param name="Next">The next quest in its chain; null for none.</param>
public sealed record QuestDetail(int Id, string Name, int UiMapId, int Level, int MinLevel, int MaxLevel, string Side, long Races,
    int Classes, string[] Text, QuestPart[] Parts, QuestLink[] PreAll, QuestLink[] PreAny, QuestLink? Next, bool Repeatable);

/// <summary>Another quest named by a quest's chain; its name is "quest {id}" when the data lacks it.</summary>
public sealed record QuestLink(int Id, string Name);

/// <summary>
/// One part of a quest: who or what (<paramref name="Name"/>, the NPC's or object's
/// <paramref name="Source"/> id, 0 for a place to explore), for which item when it is one to collect,
/// for which objective (its index, -1 for where the quest is given and handed in), and every place.
/// </summary>
public sealed record QuestPart(QuestPartKind Kind, string Name, int Source, string? Item, int Objective, QuestSpot[] Places);

/// <summary>A place in world yards on a map; a height of 0 means find the ground there.</summary>
public sealed record QuestSpot(int MapId, Point3 At);
