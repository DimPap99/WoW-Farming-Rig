namespace Core.Forever;

/// <summary>
/// What the trial runner (<c>Utilities/ForeverControllerTrial</c>) and the web
/// UI's run host (<c>Frontend/ForeverRunHost</c>) share: the option and the
/// line that stop a run, the lines the host reads a run's state from, and the
/// exit codes. The runner prints these, the host matches them; rewording one
/// here rewords both sides.
/// </summary>
public static class ForeverRunProtocol
{
    /// <summary>The runner reads its standard input: <see cref="StopCommand"/>, or the input closing, stops the run.</summary>
    public const string ListenOption = "--listen";
    public const string StopCommand = "stop";

    /// <summary>Starts the line naming the activity, its step and the route's progress.</summary>
    public const string PhasePrefix = "Autonomous phase:";
    /// <summary>Starts the run's closing line: kills, loot and its status.</summary>
    public const string SummaryPrefix = "Autonomous kills:";
    /// <summary>Starts the line that asks for the click into the game, before anything moves.</summary>
    public const string ClickPrompt = "Click into the game window";
    /// <summary>Starts a frame snapshot, printed every two seconds.</summary>
    public const string SnapshotPrefix = "  map=";

    /// <summary>The route completed, or a repeating route reached its time bound without failing.</summary>
    public const int ExitComplete = 0;
    /// <summary>A usage mistake, or a route file refused when it loads.</summary>
    public const int ExitUsage = 1;
    /// <summary>Refused before anything moved: no game, no frame, the start-up check, no click in time.</summary>
    public const int ExitRefused = 2;
    /// <summary>The run failed, stopped or ran out of time after it started.</summary>
    public const int ExitFailed = 3;
}
