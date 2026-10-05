using SharedLib.Forever;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// Whether a <see cref="ForeverUntil"/> holds. The condition is data in SharedLib, so route
/// files and the services read it; the agenda's progress and the frame it is checked on are
/// the runtime's, so the check lives here.
/// </summary>
public static class ForeverUntilChecks
{
    extension(ForeverUntil until)
    {
        /// <summary>Whether a condition the agenda itself tracks holds on <paramref name="bars"/>; a runner reports the rest.</summary>
        public bool IsMet(ForeverTaskProgress progress, VisualBarSample bars) =>
            until.Kills is { } kills && progress.Kills >= kills ||
            until.Minutes is { } minutes && progress.StartedAt != 0 &&
                GetElapsedTime(progress.StartedAt).TotalMinutes >= minutes ||
            until.Level is { } level && bars.PlayerIdentity is { Level: var current } && current >= level;
    }
}
