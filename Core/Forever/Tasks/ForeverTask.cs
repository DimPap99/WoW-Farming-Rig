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

/// <summary>Whether a task takes units in view as it goes, or fights only what attacks.</summary>
public enum ForeverPulls { None, Any }

/// <summary>
/// Hunts where the run stands: looks round, takes what is in view, walks on
/// a stretch within 25 yards and looks again, as the bounded trials do.
/// </summary>
public sealed record ForeverHuntHereTask(ForeverUntil Until) : ForeverTask("Hunt here", Until);

/// <summary>
/// Travels along the baked mesh through the <paramref name="Via"/> points to
/// <paramref name="To"/>, and is done on arrival. Units that attack are
/// fought whatever <paramref name="Pulls"/> says; with
/// <see cref="ForeverPulls.Any"/> the units on the way are taken too.
/// </summary>
public sealed record ForeverTravelTask(ForeverMapPoint To, IReadOnlyList<ForeverMapPoint> Via,
    ForeverPulls Pulls, ForeverUntil Until) : ForeverTask($"Travel to {To}", Until);

/// <summary>The tasks an agenda takes: the API for code, and what route files load into.</summary>
public static class ForeverTasks
{
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
