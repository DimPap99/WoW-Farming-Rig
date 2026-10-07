using System;

namespace SharedLib.Quests;

/// <summary>Which side may take a quest.</summary>
public enum QuestSide
{
    Both,
    Alliance,
    Horde
}

/// <summary>What a quest's objective asks.</summary>
public enum QuestGoalKind
{
    /// <summary>Kill a creature (<see cref="QuestGoal.Id"/>), or one of <see cref="QuestGoal.Ids"/> for its credit.</summary>
    Kill,
    /// <summary>Use an object.</summary>
    Object,
    /// <summary>Collect an item: from the creatures that drop it, the objects that hold it, the vendors that sell it.</summary>
    Item,
    /// <summary>Reach <see cref="QuestGoal.Value"/> with a faction.</summary>
    Reputation,
    /// <summary>Cast a spell, with <see cref="QuestGoal.Item"/> when an item casts it.</summary>
    Spell
}

/// <summary>A point in world yards on a map (a continent's or an island's map id).</summary>
public readonly record struct WorldSpot(int Map, float X, float Y);

/// <summary>A quest as <c>scripts/extract-questiedb.py</c> writes it to <c>Json/quests/&lt;client&gt;/quests.json</c>.</summary>
public sealed record QuestRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The quest's own level.</summary>
    public int Level { get; init; }

    /// <summary>The level a character needs to take it.</summary>
    public int MinLevel { get; init; }

    /// <summary>The level past which it is no longer offered; 0 for none.</summary>
    public int MaxLevel { get; init; }

    /// <summary>The races that may take it, as a mask; 0 for every race.</summary>
    public long Races { get; init; }

    public QuestSide Side { get; init; }

    /// <summary>The classes that may take it, as a mask; 0 for every class.</summary>
    public int Classes { get; init; }

    /// <summary>Above 0 the zone (AreaTable id) it belongs to; below 0 a quest sort (a class, a profession).</summary>
    public int Zone { get; init; }

    /// <summary>Who gives it: NPCs, objects, or items that start it.</summary>
    public QuestParties Starts { get; init; } = new();

    /// <summary>Who takes it back.</summary>
    public QuestParties Ends { get; init; } = new();

    public QuestGoal[] Objectives { get; init; } = [];

    /// <summary>Places to go, each with what to do there.</summary>
    public QuestExplore[] Explore { get; init; } = [];

    /// <summary>What the quest log says to do.</summary>
    public string[] Text { get; init; } = [];

    /// <summary>Quests that must all be done first.</summary>
    public int[] PreAll { get; init; } = [];

    /// <summary>Quests of which one must be done first.</summary>
    public int[] PreAny { get; init; } = [];

    /// <summary>Quests that rule this one out once taken.</summary>
    public int[] Exclusive { get; init; } = [];

    /// <summary>The next quest in its chain; 0 for none.</summary>
    public int Next { get; init; }

    /// <summary>The quest this optional one leads to; 0 for none.</summary>
    public int BreadcrumbFor { get; init; }

    public bool Repeatable { get; init; }

    /// <summary>A profession and the skill it needs; null for none.</summary>
    public QuestSkill? Skill { get; init; }

    /// <summary>Items the quest hands over or needs that are not objectives.</summary>
    public int[] SourceItems { get; init; } = [];
}

/// <summary>NPCs, objects and items, by id.</summary>
public sealed record QuestParties
{
    public int[] Npcs { get; init; } = [];
    public int[] Objects { get; init; } = [];
    public int[] Items { get; init; } = [];
}

public sealed record QuestGoal
{
    public QuestGoalKind Kind { get; init; }

    /// <summary>The creature, object, item, faction or spell.</summary>
    public int Id { get; init; }

    /// <summary>For kill credit: every creature that counts.</summary>
    public int[] Ids { get; init; } = [];

    /// <summary>What the quest log calls it, when not just the name.</summary>
    public string? Text { get; init; }

    /// <summary>The reputation a reputation objective asks.</summary>
    public int? Value { get; init; }

    /// <summary>The item a spell objective is cast with.</summary>
    public int? Item { get; init; }
}

public sealed record QuestExplore
{
    public string? Text { get; init; }
    public WorldSpot[] Places { get; init; } = [];
}

public sealed record QuestSkill
{
    public int Id { get; init; }
    public int Value { get; init; }
}

/// <summary>An object (a chest, a herb, a quest object) with every spawn, from <c>objects.json</c>.</summary>
public sealed record QuestObject
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public WorldSpot[] Spawns { get; init; } = [];
}

/// <summary>An item a quest needs, with where it comes from, from <c>items.json</c>.</summary>
public sealed record QuestItem
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int[] NpcDrops { get; init; } = [];
    public int[] ObjectDrops { get; init; } = [];
    public int[] ItemDrops { get; init; } = [];
    public int[] Vendors { get; init; } = [];

    /// <summary>The quest this item starts; 0 for none.</summary>
    public int StartQuest { get; init; }
}

/// <summary>What a place on a quest is for.</summary>
public enum QuestPlaceKind
{
    /// <summary>Where the quest is given.</summary>
    Start,
    /// <summary>Where it is taken back.</summary>
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

/// <summary>
/// One place on a quest: what to do (<see cref="Kind"/>), with whom or what (<see cref="Name"/>,
/// the NPC's or object's <see cref="Source"/> id, 0 for a place to explore), for which objective
/// (<see cref="Objective"/>, its index in <see cref="QuestRecord.Objectives"/>, or in
/// <see cref="QuestRecord.Explore"/> for a place; -1 for the start and the end), and where.
/// </summary>
public readonly record struct QuestPlace(QuestPlaceKind Kind, string Name, int Source, int Objective, string? Item, WorldSpot At);
