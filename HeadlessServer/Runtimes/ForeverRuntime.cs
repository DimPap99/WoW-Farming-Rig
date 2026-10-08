using BotAgent.Runtime;

using Contracts.Bots;

using Core;
using Core.Forever;
using Core.Goals;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Forever;

using System.Diagnostics;
using System.Text;

namespace HeadlessServer.Runtimes;

/// <summary>
/// A Forever route run: Core's <see cref="ForeverRun"/>, as the trial runner runs <c>--route</c>,
/// with every gate it has: the profile verified for the logged-in class, the read-only preflight, a
/// click into the game window before anything moves (the console shows the wait), and the time
/// bound. This is the one place the agent lifts HeadlessServer's "Forever is diagnostic-only" guard,
/// on purpose: a standalone HeadlessServer still refuses Forever. The run's lines go to the log, to
/// the Control Center's console, and to <c>local/logs/agent-&lt;route&gt;-&lt;time&gt;.log</c> when that
/// folder exists.
/// </summary>
public sealed partial class ForeverRuntime : IBotRuntime
{
    public const string LogCategory = "ForeverRun";
    public const string RunLogPrefix = "agent-";

    private readonly ForeverRunRequest request;
    private readonly int processId;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly string? logFolder;
    private volatile ForeverRun? run;

    private ForeverRuntime(ForeverRunRequest request, string route, int processId, ILoggerFactory loggerFactory, string? logFolder)
    {
        this.request = request;
        this.processId = processId;
        this.loggerFactory = loggerFactory;
        this.logFolder = logFolder;
        logger = loggerFactory.CreateLogger(LogCategory);
        Summary = $"route {route}";
    }

    public BotRuntimeKind Kind => BotRuntimeKind.Forever;

    public string Summary { get; }

    /// <summary>A runtime for the route, once its file is on this PC and a run would load it; else why not.</summary>
    public static (IBotRuntime? Runtime, string? Refusal) Create(ForeverAssignment assignment, AgentGame game, DataConfig data,
        ILoggerFactory loggerFactory)
    {
        DataConfig forever = new() { Root = Path.GetFullPath(data.Root), Exp = ForeverRunRequest.Client };
        string file = Path.Combine(forever.Path, ForeverRouteFile.Folder, assignment.Route);
        if (!File.Exists(file))
        {
            return (null, $"the route {assignment.Route} is not in this PC's Json/path/{ForeverRouteFile.Folder}");
        }

        WorldMapAreaDB maps;
        try
        {
            maps = new WorldMapAreaDB(forever);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            // Refused, not failed: nothing attached yet (known issue 77).
            return (null, $"Forever's map data (Json/dbc/{ForeverRunRequest.Client}) could not be read: {ex.Message}");
        }

        if (!ForeverRouteFile.TryLoad(file, maps, out ForeverRoute route, out string error))
        {
            return (null, $"the route is refused: {error}");
        }

        // The live tests' logs, beside the data folder.
        string logs = Path.GetFullPath(Path.Combine(forever.Root, "..", "local", "logs"));
        ForeverRunRequest request = new(ForeverRunMode.Route)
        {
            Route = route,
            Minutes = assignment.Minutes,
            DataRoot = forever.Root,
            UnreadableFrameFolder = Directory.Exists(logs) ? logs : null,
        };
        if (request.Problem is string problem)
        {
            return (null, problem);
        }

        return (new ForeverRuntime(request, assignment.Route, game.ProcessId, loggerFactory, Directory.Exists(logs) ? logs : null), null);
    }

    public BotRunResult Run(CancellationToken stop)
    {
        using Process? wow = ForeverRun.FindClient(processId);
        if (wow is null)
        {
            return new BotRunResult(BotRunOutcome.Refused, ForeverRun.NotRunning);
        }

        using RunOutput output = new(logger, OpenLog());
        ForeverRoute route = request.Route!;
        output.Line($"Route {route.Name}: {route.Tasks.Count} steps{(route.Repeat ? ", repeating" : string.Empty)}.");
        for (int i = 0; i < route.Tasks.Count; i++)
        {
            output.Line($"  {i + 1}. {route.Tasks[i].Name}");
        }

        // The run's token is also the one the controller's own threads watch, as in the trial runner.
        using CancellationTokenSource cts = new();
        using CancellationTokenRegistration stopping = stop.Register(cts.Cancel);
        ForeverRun forever = new(request, loggerFactory, output);
        run = forever;
        ForeverRunResult ended = forever.Run(wow, cts);
        output.Line($"exit={ended.ExitCode}");

        BotRunOutcome outcome = ended.ExitCode switch
        {
            ForeverRunProtocol.ExitComplete => BotRunOutcome.Complete,
            ForeverRunProtocol.ExitRefused or ForeverRunProtocol.ExitUsage => BotRunOutcome.Refused,
            _ => BotRunOutcome.Failed,
        };
        return new BotRunResult(stop.IsCancellationRequested && outcome != BotRunOutcome.Complete ? BotRunOutcome.Stopped : outcome,
            ended.Message, ended.ExitCode, forever.Autonomous is { } goal ? Progress(goal) : null);
    }

    public void ForceRelease()
    {
        run?.ReleaseInput();
    }

    public BotTelemetry Read()
    {
        if (run is not { } forever)
        {
            return new BotTelemetry(BotRunState.Starting, Message: "attaching to the client");
        }

        BotRunState state = forever.Stage switch
        {
            ForeverRunStage.WaitingForClick => BotRunState.WaitingForClick,
            ForeverRunStage.Running => BotRunState.Running,
            ForeverRunStage.Ended => BotRunState.Stopping,
            _ => BotRunState.Starting,
        };

        BotCharacter? character = null;
        BotVitals? vitals = null;
        BotPosition? position = null;
        if (forever.Tracker is { } tracker && tracker.TryGetLatest(out VisualBarSample bars))
        {
            if (bars.PlayerIdentity is { } identity)
            {
                character = new BotCharacter(((UnitClass)identity.ClassId).ToString(), identity.Level, ((UnitRace)identity.RaceId).ToString());
            }

            bool target = bars.TargetPresent == true || bars.TargetFlags?.Present == true;
            vitals = new BotVitals((float)(bars.PlayerHealth * 100), (float)(bars.PlayerPower * 100),
                target ? (float)(bars.TargetHealth * 100) : null, bars.TargetFlags?.PlayerCombat == true);
            if (bars.Position is { } at)
            {
                position = new BotPosition(at.UIMapId, (float)(at.MapX * 100), (float)(at.MapY * 100));
            }
        }

        string? message = state == BotRunState.WaitingForClick
            ? "click into the game window to start the run; nothing has moved"
            : null;
        if (forever.Autonomous is not { } goal)
        {
            return new BotTelemetry(state, character, vitals, position, Message: message);
        }

        return new BotTelemetry(state, character, vitals, position, goal.Status, goal.AgendaStatus, Progress(goal),
            message ?? (goal.LastFailure is { Length: > 0 } failure ? failure : null));
    }

    private static BotProgress Progress(ForeverAutonomousGoal goal)
    {
        return new BotProgress(goal.CompletedKills, goal.ObservedDeaths, goal.CompletedLoots);
    }

    public void Dispose()
    {
        // The run disposed its controller and container before it returned.
    }

    private StreamWriter? OpenLog()
    {
        if (logFolder is null)
        {
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(Summary.Replace("route ", string.Empty, StringComparison.Ordinal));
        string file = Path.Combine(logFolder, $"{RunLogPrefix}{name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            return new StreamWriter(file, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNoRunLog(logger, file, ex.Message);
            return null;
        }
    }

    // The run's lines: to the log (snapshots at Debug, so the console streams only what a person reads) and the run's file.
    private sealed partial class RunOutput(ILogger logger, StreamWriter? file) : IForeverRunOutput, IDisposable
    {
        private readonly Lock sync = new();

        public void Line(string text)
        {
            LogLine(logger, text);
            Write(text);
        }

        public void Problem(string text)
        {
            LogProblem(logger, text);
            Write(text);
        }

        public void Snapshot(string text)
        {
            LogSnapshot(logger, text);
            Write(text);
        }

        public void Dispose()
        {
            lock (sync)
            {
                file?.Dispose();
            }
        }

        private void Write(string text)
        {
            lock (sync)
            {
                try
                {
                    file?.WriteLine(text);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
        static partial void LogLine(ILogger logger, string line);

        [LoggerMessage(Level = LogLevel.Warning, Message = "{Line}")]
        static partial void LogProblem(ILogger logger, string line);

        [LoggerMessage(Level = LogLevel.Debug, Message = "{Line}")]
        static partial void LogSnapshot(ILogger logger, string line);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The run's log {File} could not be opened: {Reason}")]
    static partial void LogNoRunLog(ILogger logger, string file, string reason);
}
