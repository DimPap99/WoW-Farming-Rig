using System.Collections.Generic;

namespace Core.Forever;

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
}
