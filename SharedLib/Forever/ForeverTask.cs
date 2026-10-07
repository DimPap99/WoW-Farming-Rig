using System.Collections.Generic;

namespace SharedLib.Forever;

/// <summary>
/// Something the agenda has the run do. A task is data only, so code and
/// route files build the same tasks; a runner carries one out over many
/// frames. It never blocks: on any frame the fight, the loot or the rest can
/// take over, and the task resumes once they are done.
/// </summary>
/// <param name="Name">Names the task in logs and the trial runner.</param>
/// <param name="Until">When the task is done.</param>
public abstract record ForeverTask(string Name, ForeverUntil Until);

/// <summary>Which units in view a task takes as it goes; what attacks is fought whatever this says.</summary>
public enum ForeverPulls
{
    /// <summary>Fights only what attacks.</summary>
    None,
    /// <summary>Takes the best unit in view, as a roam clearing its area does.</summary>
    Any,
    /// <summary>
    /// Takes only units on the way: near the path still to walk, a little way
    /// ahead, as a person on their way somewhere kills what stands in it and
    /// makes no detour.
    /// </summary>
    OnTheWay,
}

/// <summary>
/// Hunts where the run stands: looks round, takes what is in view, walks on
/// a stretch within 25 yards and looks again, as the bounded trials do.
/// </summary>
public sealed record ForeverHuntHereTask(ForeverUntil Until) : ForeverTask("Hunt here", Until);

/// <summary>
/// Travels along the baked mesh through the <paramref name="Via"/> points to
/// <paramref name="To"/>, and is done on arrival. Units that attack are
/// fought whatever <paramref name="Pulls"/> says; with
/// <see cref="ForeverPulls.OnTheWay"/> the units on the way are taken too,
/// with <see cref="ForeverPulls.Any"/> any unit in view.
/// </summary>
public sealed record ForeverTravelTask(ForeverMapPoint To, IReadOnlyList<ForeverMapPoint> Via,
    ForeverPulls Pulls, ForeverUntil Until) : ForeverTask($"Travel to {To}", Until);

/// <summary>
/// Roams <paramref name="Area"/> until <paramref name="Until"/> holds: walks
/// in if outside, then from spot to spot inside, looking round at each, and
/// takes eligible units in view unless <paramref name="Pulls"/> says
/// otherwise. Spots are drawn from <see cref="Seed"/>, or a fresh seed that
/// is logged.
/// </summary>
public sealed record ForeverRoamAreaTask(ForeverArea Area, ForeverPulls Pulls, ForeverUntil Until)
    : ForeverTask($"Roam the {Area}", Until)
{
    public int? Seed { get; init; }
}

/// <summary>Who or what a <see cref="ForeverVisitTask"/> goes to.</summary>
public enum ForeverVisitKind
{
    /// <summary>
    /// The nearest NPC with a role - <see cref="ForeverVisitTarget.Name"/>, an <c>NpcFlags</c> name such
    /// as vendor, repair, innkeeper or flightmaster - who serves the character's side.
    /// </summary>
    Role,
    /// <summary>The nearest spawn of the NPC called <see cref="ForeverVisitTarget.Name"/>.</summary>
    Npc,
    /// <summary>Where quest <see cref="ForeverVisitTarget.QuestId"/> is given, the nearest such place.</summary>
    QuestStart,
    /// <summary>Where it is handed in.</summary>
    QuestEnd,
}

/// <summary>Who or what to visit, found when the task begins: the nearest from where the character stands.</summary>
/// <param name="Name">A role's name, or an NPC's.</param>
/// <param name="Title">For a role: the NPC's title holds this, as "Warrior Trainer" or "Leatherworking".</param>
public sealed record ForeverVisitTarget(ForeverVisitKind Kind, string Name = "", int QuestId = 0, string? Title = null)
{
    public static ForeverVisitTarget Nearest(string role, string? title = null) => new(ForeverVisitKind.Role, role, Title: title);

    public static ForeverVisitTarget Npc(string name) => new(ForeverVisitKind.Npc, name);

    public static ForeverVisitTarget QuestStart(int questId) => new(ForeverVisitKind.QuestStart, QuestId: questId);

    public static ForeverVisitTarget QuestEnd(int questId) => new(ForeverVisitKind.QuestEnd, QuestId: questId);

    public override string ToString() => Kind switch
    {
        ForeverVisitKind.Role => Title is null ? $"the nearest {Name}" : $"the nearest {Name} titled {Title}",
        ForeverVisitKind.Npc => Name,
        ForeverVisitKind.QuestStart => $"where quest {QuestId} is given",
        _ => $"where quest {QuestId} is handed in",
    };
}

/// <summary>
/// Travels to <paramref name="Target"/>, a place the task names by who or what is there instead of
/// a point: it is found when the task begins, the nearest on the character's continent from where
/// it stands, in the client's world data (<see cref="ForeverDestinations"/>), then walked to as a
/// <see cref="ForeverTravelTask"/> and done on arrival. Talking to whoever is there is not done yet.
/// </summary>
public sealed record ForeverVisitTask(ForeverVisitTarget Target, ForeverPulls Pulls, ForeverUntil Until)
    : ForeverTask($"Visit {Target}", Until);

/// <summary>
/// Roams where quest <paramref name="QuestId"/> sends a character to hunt or gather until
/// <paramref name="Until"/> holds: the camp of the creatures it asks to kill, or that drop what it
/// needs, nearest the character when the task begins (else the objects it needs, else the place it
/// asks to explore), roamed as a <see cref="ForeverRoamAreaTask"/>. It takes any eligible unit there,
/// not only the quest's, and knows nothing of the quest log: <paramref name="Until"/> ends it.
/// </summary>
public sealed record ForeverQuestAreaTask(int QuestId, ForeverPulls Pulls, ForeverUntil Until)
    : ForeverTask($"Roam where quest {QuestId} sends", Until)
{
    public int? Seed { get; init; }
}

/// <summary>The tasks an agenda takes: the API for code, and what route files load into.</summary>
public static class ForeverTasks
{
    /// <summary>
    /// Roam <paramref name="area"/> until <paramref name="until"/> holds,
    /// taking the units in view unless <paramref name="pulls"/> says otherwise.
    /// </summary>
    public static ForeverTask RoamArea(ForeverArea area, ForeverUntil until,
        ForeverPulls pulls = ForeverPulls.Any, int? seed = null) =>
        new ForeverRoamAreaTask(area, pulls, until) { Seed = seed };

    /// <summary>Hunt where the run stands until <paramref name="until"/> holds.</summary>
    public static ForeverTask HuntHere(ForeverUntil until) => new ForeverHuntHereTask(until);

    /// <summary>
    /// Travel to <paramref name="to"/>, through <paramref name="via"/> in
    /// order, fighting only what attacks unless <paramref name="pulls"/> says otherwise.
    /// </summary>
    public static ForeverTask TravelTo(ForeverMapPoint to, IEnumerable<ForeverMapPoint>? via = null,
        ForeverPulls pulls = ForeverPulls.None) =>
        new ForeverTravelTask(to, [.. via ?? []], pulls, ForeverUntil.Never);

    /// <summary>
    /// Travel to <paramref name="target"/>, found when the task begins, fighting only what attacks
    /// unless <paramref name="pulls"/> says otherwise.
    /// </summary>
    public static ForeverTask Visit(ForeverVisitTarget target, ForeverPulls pulls = ForeverPulls.None) =>
        new ForeverVisitTask(target, pulls, ForeverUntil.Never);

    /// <summary>
    /// Roam where quest <paramref name="questId"/> sends a character until <paramref name="until"/>
    /// holds, taking the units in view unless <paramref name="pulls"/> says otherwise.
    /// </summary>
    public static ForeverTask QuestArea(int questId, ForeverUntil until, ForeverPulls pulls = ForeverPulls.Any,
        int? seed = null) =>
        new ForeverQuestAreaTask(questId, pulls, until) { Seed = seed };
}
