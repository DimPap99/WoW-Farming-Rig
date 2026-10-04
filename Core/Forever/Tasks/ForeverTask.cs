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

/// <summary>
/// Hunts where the run stands: looks round, takes what is in view, walks on
/// a stretch within 25 yards and looks again, as the bounded trials do.
/// </summary>
public sealed record ForeverHuntHereTask(ForeverUntil Until) : ForeverTask("Hunt here", Until);

/// <summary>The tasks an agenda takes: the API for code, and what route files load into.</summary>
public static class ForeverTasks
{
    /// <summary>Hunt where the run stands until <paramref name="until"/> holds.</summary>
    public static ForeverTask HuntHere(ForeverUntil until) => new ForeverHuntHereTask(until);
}
