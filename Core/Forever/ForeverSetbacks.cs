namespace Core.Forever;

/// <summary>
/// The run's fallback ladder: a failure is absorbed by the goal above the
/// one that failed, as a person who cannot loot a body leaves it, cannot
/// reach a mob picks another, and cannot get to one place goes on to the
/// next. In order: a press that opens nothing is pressed again a step closer
/// (<see cref="ForeverLoot"/>); a body, a unit or a spot that keeps failing is
/// left for another; an activity that fails starts afresh
/// (<see cref="IForeverActivity.FallBack"/>); a task that fails is given up for
/// the next; and the last task of a route is tried again after hunting where
/// the run stands (<see cref="ForeverAgenda.GiveUp"/>). The run never stops
/// for a failure: only reaching its goal ends it, or what no fallback can
/// change (the player taking the game back, the player's death, a client
/// setup that broke).
/// <para>
/// This counts the failures that reach the run in a row, without a kill, a
/// body looted or a task done between them. Every <see cref="MaxInARow"/>th
/// in a row, what the run falls back to keeps failing too, so the current
/// task is given up as well: the next bigger goal is tried.
/// </para>
/// </summary>
public sealed class ForeverSetbacks
{
    /// <summary>Failures in a row, with no progress between them, after which the current task is given up too.</summary>
    public const int MaxInARow = 10;

    private int inARow;
    private string first = string.Empty;

    /// <summary>Failures since the last progress.</summary>
    public int InARow => inARow;

    /// <summary>The first failure since the last progress.</summary>
    public string First => first;

    /// <summary>Counts a failure the run falls back from; true when the current task is to be given up as well.</summary>
    public bool Absorb(string reason)
    {
        if (inARow++ == 0)
            first = reason;
        return inARow % MaxInARow == 0;
    }

    /// <summary>A kill, a body looted or a task done: the run gets somewhere again.</summary>
    public void Progress()
    {
        inARow = 0;
        first = string.Empty;
    }
}
