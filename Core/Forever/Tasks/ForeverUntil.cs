namespace Core.Forever;

/// <summary>
/// When a task is done: any condition that is set ends it once it holds.
/// With none set the task does not end by itself.
/// </summary>
/// <param name="Kills">Kills verified while the task was current, its fights included.</param>
/// <param name="NothingInView">A full look-around found nothing to take; only hunting tasks look.</param>
public sealed record ForeverUntil(int? Kills = null, bool NothingInView = false)
{
    public static ForeverUntil Never { get; } = new();

    /// <summary>Done after <paramref name="kills"/> kills.</summary>
    public static ForeverUntil AfterKills(int kills) => new(Kills: kills);

    /// <summary>Done once a full look-around finds nothing to take.</summary>
    public static ForeverUntil WhenNothingInView { get; } = new(NothingInView: true);

    /// <summary>Whether a condition the agenda itself tracks holds; a runner reports the rest.</summary>
    public bool IsMet(ForeverTaskProgress progress) =>
        Kills is { } kills && progress.Kills >= kills;
}
