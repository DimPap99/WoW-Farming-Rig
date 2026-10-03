using System;

namespace Core.Forever;

/// <summary>A failure with one unit, or with the way to it, that another unit may not share.</summary>
public enum ForeverSetback
{
    /// <summary>Its nameplate could not be clicked into the target.</summary>
    SelectFailed,
    /// <summary>Its marker stayed out of sight during the approach.</summary>
    TargetLost,
    /// <summary>Held movement toward it did not move the character.</summary>
    Stuck,
    /// <summary>No walkable way toward it.</summary>
    Blocked,
    /// <summary>It stayed out of range for the whole approach.</summary>
    OutOfReach
}

/// <summary>
/// How the run recovers from a setback: the unit is set aside for a while and
/// the search starts again, as a person gives up on a mob across a ravine and
/// picks another. In combat only a selection may be retried, since the unit
/// fighting the player stays a threat; any other setback in combat, and more
/// than <see cref="Budget"/> setbacks without a kill, stop the run.
/// </summary>
public sealed class ForeverRecovery
{
    public const int Budget = 5;

    private int sinceKill;

    /// <summary>Setbacks since the last kill.</summary>
    public int SinceKill => sinceKill;

    /// <summary>How long a unit is passed over after <paramref name="setback"/>; zero keeps it a candidate.</summary>
    public static TimeSpan SetAsideFor(ForeverSetback setback, bool inCombat) => setback switch
    {
        _ when inCombat => TimeSpan.Zero,
        ForeverSetback.SelectFailed or ForeverSetback.TargetLost => TimeSpan.FromMinutes(1),
        // The way there is the problem, which a minute does not change.
        _ => TimeSpan.FromMinutes(2),
    };

    /// <summary>Counts the setback; false when the run should stop instead, with why.</summary>
    public bool TryRecover(ForeverSetback setback, bool inCombat, out string refusal)
    {
        refusal = string.Empty;
        if (inCombat && setback != ForeverSetback.SelectFailed)
        {
            refusal = $"{setback} while in combat";
            return false;
        }
        if (++sinceKill > Budget)
        {
            refusal = $"{sinceKill} setbacks without a kill";
            return false;
        }
        return true;
    }

    public void Killed() => sinceKill = 0;
}
