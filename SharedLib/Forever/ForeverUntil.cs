namespace SharedLib.Forever;

/// <summary>
/// When a task is done: any condition that is set ends it once it holds.
/// With none set the task does not end by itself. Whether one holds is the
/// runtime's to say (<c>Core.Forever.ForeverUntilChecks</c>).
/// </summary>
/// <param name="Kills">Kills verified while the task was current, its fights included.</param>
/// <param name="NothingInView">A full look-around found nothing to take; only hunting tasks look.</param>
/// <param name="Minutes">Minutes since the task began, its fights and rests included.</param>
/// <param name="Level">The player has reached this level.</param>
public sealed record ForeverUntil(int? Kills = null, bool NothingInView = false,
    double? Minutes = null, int? Level = null)
{
    public static ForeverUntil Never { get; } = new();

    /// <summary>Done after <paramref name="kills"/> kills.</summary>
    public static ForeverUntil AfterKills(int kills) => new(Kills: kills);

    /// <summary>Done after <paramref name="minutes"/> minutes.</summary>
    public static ForeverUntil AfterMinutes(double minutes) => new(Minutes: minutes);

    /// <summary>Done once the player is level <paramref name="level"/>.</summary>
    public static ForeverUntil AtLevel(int level) => new(Level: level);

    /// <summary>Done once a full look-around finds nothing to take.</summary>
    public static ForeverUntil WhenNothingInView { get; } = new(NothingInView: true);
}
