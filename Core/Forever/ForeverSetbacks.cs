using System.Collections.Generic;

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
/// the run stands (<see cref="ForeverAgenda.GiveUp"/>). Only what no fallback
/// can make good stops the run: the player's death, the player taking the
/// game back, a client setup that broke, a long loss of the screen.
/// <para>
/// This counts the failures that reach the run in a row, without a kill, a
/// body looted or a task done between them. Past <see cref="MaxInARow"/>
/// whatever the run falls back to keeps failing too, and it stops with the
/// first reason rather than loop on.
/// </para>
/// </summary>
public sealed class ForeverSetbacks
{
    /// <summary>Failures in a row, with no progress between them, that stop the run.</summary>
    public const int MaxInARow = 10;

    private readonly List<string> inARow = [];

    /// <summary>Failures since the last progress.</summary>
    public int InARow => inARow.Count;

    /// <summary>Counts a failure the run falls back from; false once too many came in a row, with why.</summary>
    public bool TryAbsorb(string reason, out string refusal)
    {
        inARow.Add(reason);
        if (inARow.Count < MaxInARow)
        {
            refusal = string.Empty;
            return true;
        }
        refusal = $"{inARow.Count} failures in a row without a kill, a body looted or a task done; the first: {inARow[0]}";
        return false;
    }

    /// <summary>A kill, a body looted or a task done: the run gets somewhere again.</summary>
    public void Progress() => inARow.Clear();
}
