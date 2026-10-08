using Core.Goals;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Core.Forever;

/// <summary>Where a run's lines go: the trial runner prints them, an agent logs and streams them.</summary>
public interface IForeverRunOutput
{
    /// <summary>The run's own text: the profile, the preflight, a phase line, the summary.</summary>
    void Line(string text);

    /// <summary>Why the run was refused, failed or ran out of time.</summary>
    void Problem(string text);

    /// <summary>A frame snapshot, every two seconds while an autonomous run moves.</summary>
    void Snapshot(string text);
}

/// <summary>How a <see cref="ForeverRun"/> ended.</summary>
/// <param name="ExitCode">One of <see cref="ForeverRunProtocol"/>'s exit codes.</param>
/// <param name="Message">What a person reads of it, as it was printed: why it was refused or failed, else how it closed.</param>
public sealed record ForeverRunResult(int ExitCode, string Message);

/// <summary>How far a <see cref="ForeverRun"/> got.</summary>
public enum ForeverRunStage
{
    NotStarted,
    /// <summary>Attached to the client: the runtime is composed and a complete frame awaited.</summary>
    Attaching,
    /// <summary>The verified profile loads into the normal controller.</summary>
    LoadingProfile,
    /// <summary>Waiting for the read-only preflight: a fight to end, health to come back.</summary>
    Preflight,
    /// <summary>Waiting for a person to click into the game window; nothing has moved.</summary>
    WaitingForClick,
    /// <summary>The controller runs and sends input.</summary>
    Running,
    /// <summary>Over; <see cref="ForeverRun.ExitCode"/> says how.</summary>
    Ended,
}

/// <summary>
/// One run of WoW: Forever's normal controller, bounded and gated as the trial runner has always
/// run it (<c>Utilities/ForeverControllerTrial</c> is now a command line over this): a verified
/// profile for the logged-in class, a read-only preflight, a click into the game before anything
/// moves, a time bound per mode, and the exit codes of <see cref="ForeverRunProtocol"/>. Stopping
/// is cancelling the token given to <see cref="Run"/>: the loop turns the controller off, which
/// releases what it holds, before anything is torn down.
/// </summary>
public sealed class ForeverRun(ForeverRunRequest request, ILoggerFactory loggerFactory, IForeverRunOutput output)
{
    /// <summary>The Forever client's process name.</summary>
    public const string ClientProcessName = "WowB";

    /// <summary>The category the run's own log lines carry.</summary>
    public const string LoggerCategory = "ControllerTrial";

    public const string NotRunning = ClientProcessName + " is not running.";

    // The first frame: attempts and the pause between them.
    private const int FrameAttempts = 100;
    private const int PollMs = 100;
    // The autonomous preflight: a run waits for a fight to end or health to come back.
    private const int RunPreflightAttempts = 600;
    private const int CheckPreflightAttempts = 50;
    private const int SnapshotSeconds = 2;
    // The click into the game: attempts at PollMs, and the pause after it.
    private const int ClickAttempts = 600;
    private const int ClickSettleMs = 1000;

    private volatile int stage;
    private volatile ForeverAutonomousGoal? autonomous;
    private volatile ForeverTrialGoal? trial;
    private volatile ForeverVisualBarTracker? tracker;
    private volatile WowProcessInput? input;
    private volatile int exitCode = -1;

    public ForeverRunRequest Request => request;

    public ForeverRunStage Stage => (ForeverRunStage)stage;

    /// <summary>The exit code once <see cref="Stage"/> is <see cref="ForeverRunStage.Ended"/>, else null.</summary>
    public int? ExitCode => exitCode < 0 ? null : exitCode;

    /// <summary>The autonomous controller's goal once the profile loaded; read it, never drive it.</summary>
    public ForeverAutonomousGoal? Autonomous => autonomous;

    /// <summary>The route trial's goal once the profile loaded; read it, never drive it.</summary>
    public ForeverTrialGoal? Trial => trial;

    /// <summary>The visual channel's frames once attached.</summary>
    public ForeverVisualBarTracker? Tracker => tracker;

    /// <summary>The newest Forever client, or the one with <paramref name="processId"/>; null when there is none.</summary>
    public static Process? FindClient(int? processId)
    {
        return Process.GetProcessesByName(ClientProcessName)
            .OrderByDescending(p => p.StartTime)
            .FirstOrDefault(p => processId is null || p.Id == processId);
    }

    /// <summary>
    /// Releases every key the run's input holds, whatever the controller is doing. For a host
    /// whose run did not end within its grace period; the run's own stop releases first.
    /// </summary>
    public void ReleaseInput()
    {
        input?.Reset();
    }

    /// <summary>
    /// Runs on the client <paramref name="processId"/> until it completes, fails, runs out of time
    /// or <paramref name="stop"/> is cancelled, which the controller's own threads then see too.
    /// Returns its <see cref="ForeverRunProtocol"/> exit code and its last word.
    /// </summary>
    public ForeverRunResult Run(int processId, CancellationToken stop)
    {
        if (request.Problem is string problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        try
        {
            ForeverRunResult result = RunOn(processId, stop);
            exitCode = result.ExitCode;
            return result;
        }
        finally
        {
            if (exitCode < 0)
            {
                exitCode = ForeverRunProtocol.ExitFailed;
            }
            stage = (int)ForeverRunStage.Ended;
        }
    }

    private ForeverRunResult RunOn(int processId, CancellationToken stop)
    {
        // The run's own: the controller's threads watch it, and the run cancels it as it ends.
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        bool auto = request.IsAutonomous;
        bool run = request.Moves;
        stage = (int)ForeverRunStage.Attaching;

        // Once the controller runs, an error ends a run that may have moved, not a refusal.
        bool started = false;
        try
        {
            // Registered as an instance below, which the container does not dispose: this does.
            using WowProcess process = new(cts, Options.Create(new StartupConfigPid { Id = processId }));
            if (process.FileVersion is not { Major: 1, Minor: >= 60 })
            {
                return Problem(ForeverRunProtocol.ExitRefused, "The selected client is not Forever.");
            }

            IServiceCollection services = new ServiceCollection();
            ILogger trialLogger = loggerFactory.CreateLogger(LoggerCategory);
            services.AddSingleton(loggerFactory);
            services.AddSingleton(trialLogger);
            services.AddLogging();
            services.AddSingleton(cts);
            services.AddSingleton(process);
            services.AddSingleton(process.FileVersion);
            // Resolved when the profile loads, after the first frame named the zone
            // a travel point lies in.
            ForeverRunOptions runOptions = new()
            {
                StopWhenNoTargets = request.Mode == ForeverRunMode.Sweep,
                MaxCompletedCycles = request.Mode == ForeverRunMode.Sweep ? null : 1
            };
            services.AddSingleton(_ => runOptions);
            services.AddSingleton<AddonConfigurator>();
            services.AddSingleton<IOptions<StartupConfigReader>>(Options.Create(new StartupConfigReader()));
            services.AddSingleton<IOptions<StartupConfigDiagnostics>>(Options.Create(new StartupConfigDiagnostics()));
            services.AddSingleton<IOptions<StartupConfigPathing>>(Options.Create(new StartupConfigPathing
            {
                Mode = nameof(StartupConfigPathing.Types.Local),
                PathVisualizer = false
            }));
            services.AddSingleton<IOptions<StartupConfigNpcOverlay>>(Options.Create(new StartupConfigNpcOverlay()));
            services.AddSingleton<IOptions<NavmeshBakeOptions>>(Options.Create(new NavmeshBakeOptions()));
            services.AddSingleton<IOptions<NavmeshQueryOptions>>(Options.Create(new NavmeshQueryOptions()));
            services.AddSingleton<IOptions<SplineFollowerOptions>>(Options.Create(new SplineFollowerOptions()));
            services.AddCoreBase(trialLogger);
            services.AddSingleton(new DataConfig
            {
                Root = request.DataRoot, Exp = ForeverRunRequest.Client
            });
            services.AddCoreNormal(trialLogger);

            using ServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            IWowScreen screen = provider.GetRequiredService<IWowScreen>();
            ForeverVisualBarTracker bars = provider.GetRequiredService<ForeverVisualBarTracker>();
            tracker = bars;
            // A capture the bars cannot be read from is kept beside the live logs, when there are any.
            if (request.UnreadableFrameFolder is string folder)
                bars.UnreadableFrameFolder = folder;
            bool ready = false;
            int classId = 0;
            int uiMapId = 0;
            // Only this thread captures until BotController starts its addon thread.
            for (int i = 0; i < FrameAttempts && !cts.IsCancellationRequested; i++)
            {
                screen.Update();
                bars.Update();
                if (bars.TryGetLatest(out VisualBarSample sample) &&
                    ForeverFrame.HasTrialInputs(sample))
                {
                    ready = true;
                    classId = sample.PlayerIdentity?.ClassId ?? 0;
                    uiMapId = sample.Position?.UIMapId ?? 0;
                    break;
                }
                Thread.Sleep(PollMs);
            }
            if (!ready)
            {
                string missing = bars.TryGetLatest(out VisualBarSample last)
                    ? $"{ForeverFrame.MissingTrialInput(last)} did not read"
                    : bars.Problem;
                return Problem(ForeverRunProtocol.ExitRefused, $"No complete fresh Forever trial frame ({missing}); no input sent.");
            }
            if (request.TravelTo is (double travelX, double travelY))
            {
                ForeverMapPoint destination = new(uiMapId, travelX, travelY);
                runOptions = runOptions with { Tasks = [ForeverTasks.TravelTo(destination)] };
                output.Line($"Travelling to {destination}.");
            }
            if (request.Roam is { } roam)
            {
                ForeverArea area = ForeverArea.Rectangle(uiMapId, (roam.X1, roam.Y1), (roam.X2, roam.Y2));
                runOptions = runOptions with { Tasks = [ForeverTasks.RoamArea(area, ForeverUntil.AfterKills(roam.Kills))] };
                output.Line($"Roaming the {area} until {roam.Kills} kills.");
            }
            if (request.Route is { } route)
            {
                runOptions = runOptions with { Tasks = route.Tasks, RepeatTasks = route.Repeat, ApplyCostZones = route.ApplyCostZones };
                if (route.ApplyCostZones)
                    output.Line("The route plans with the authored cost zones.");
            }

            // The autonomous run uses the profile verified for the logged-in class;
            // the bounded route trial exists for the Paladin only.
            string? profile = auto
                ? ForeverVerifiedSetup.AutoProfileFor(classId)
                : ForeverVerifiedSetup.PaladinTrialProfile;
            if (profile is null)
            {
                return Problem(ForeverRunProtocol.ExitRefused, $"No verified Forever profile for class {(UnitClass)classId}; no input sent.");
            }
            stage = (int)ForeverRunStage.LoadingProfile;
            output.Line($"Loading {profile}.");

            using BotController controller = ActivatorUtilities.CreateInstance<BotController>(provider);
            if (!controller.LoadClassProfile(profile))
            {
                return Problem(ForeverRunProtocol.ExitRefused, "Normal controller did not load the Forever profile.");
            }
            // The session built it; nothing can be held before this.
            input = provider.GetRequiredService<WowProcessInput>();

            if (auto)
            {
                if (controller.GoapAgent?.AvailableGoals is not [ForeverAutonomousGoal automatic])
                {
                    return Problem(ForeverRunProtocol.ExitRefused, "Normal controller did not load the autonomous goal.");
                }
                autonomous = automatic;
                stage = (int)ForeverRunStage.Preflight;
                string autoReason = string.Empty;
                bool autoReady = false;
                // BotController's addon thread now owns capture. Calling Update here
                // too would race it on the shared screen image and tracker buffers.
                // A live run waits for a fight to end or health to come back.
                int attempts = run ? RunPreflightAttempts : CheckPreflightAttempts;
                string waitingFor = string.Empty;
                for (int attempt = 0; attempt < attempts && !cts.IsCancellationRequested;
                     attempt++)
                {
                    if (automatic.TryReadOnlyPreflight(out autoReason))
                    {
                        autoReady = true;
                        break;
                    }
                    if (run && autoReason != waitingFor)
                    {
                        output.Line($"Waiting for the preflight: {autoReason}.");
                        waitingFor = autoReason;
                    }
                    Thread.Sleep(PollMs);
                }
                if (!autoReady)
                {
                    return Problem(ForeverRunProtocol.ExitRefused, $"Autonomous preflight refused: {autoReason}. No input sent.");
                }
                output.Line("Autonomous current-position preflight passed.");
                if (!run)
                {
                    IReadOnlyList<ForeverPullOption> pulls = automatic.PullOptions();
                    return Closing(ForeverRunProtocol.ExitComplete, pulls.Count == 0
                        ? "Pull options now: none in view."
                        : "Pull options now, the best first: " + string.Join("; ", pulls) + ".");
                }

                // The run steers and clicks with the mouse, which the game follows only
                // while it is the active window; Windows will not hand that over to a
                // background process right after someone typed elsewhere.
                if (!WaitForGameWindow(process, cts.Token))
                {
                    return Problem(ForeverRunProtocol.ExitRefused, "The game window did not become active; no input sent.");
                }
                controller.ToggleBotStatus();
                started = true;
                stage = (int)ForeverRunStage.Running;
                DateTime autoDeadline = DateTime.UtcNow.AddSeconds(request.TimeBoundSeconds);
                DateTime nextSnapshot = DateTime.MinValue;
                string autoLastStatus = string.Empty;
                while (!cts.IsCancellationRequested && DateTime.UtcNow < autoDeadline &&
                       automatic.State is not (ForeverAutonomousGoal.RunState.Failed or ForeverAutonomousGoal.RunState.Complete))
                {
                    string status = automatic.Status;
                    if (status != autoLastStatus)
                    {
                        output.Line($"{ForeverRunProtocol.PhasePrefix} {status}; completed cycles={automatic.CompletedKills}, looted={automatic.CompletedLoots}; {automatic.AgendaStatus}");
                        autoLastStatus = status;
                    }
                    if (DateTime.UtcNow >= nextSnapshot)
                    {
                        if (bars.TryGetLatest(out VisualBarSample current))
                            output.Snapshot($"{ForeverRunProtocol.SnapshotPrefix}{current.Position?.UIMapId} {current.Position?.MapX * 100:0.00},{current.Position?.MapY * 100:0.00}, " +
                                $"health={current.PlayerHealth:P0}, mana={current.PlayerPower:P0}, " +
                                $"target={current.TargetHealth:P0}, flags={current.TargetFlags}, " +
                                $"auras={current.SlotAuras?.ToString() ?? "unreadable"}, marker={current.TargetScreenPosition}, " +
                                $"plates={current.Nameplates?.PlateCount.ToString() ?? "unreadable"}, " +
                                $"range1/2/3={current.ActionStates?.GetSlot(1)?.InRange}/{current.ActionStates?.GetSlot(2)?.InRange}/{current.ActionStates?.GetSlot(3)?.InRange}, " +
                                $"attack={current.Attack?.ToString() ?? "unreadable"}, pet={PetState(current)}, " +
                                $"tracked={current.TrackedBlips?.Count.ToString() ?? "no minimap"}, " +
                                $"soft={current.SoftInteract?.ToString() ?? "unreadable"}, " +
                                $"cast={current.LastCastEvent}, death={current.LastDeathEvent}, loot={current.Loot}, " +
                                $"frameReady={ForeverFrame.HasTrialInputs(current)}");
                        else
                            output.Snapshot($"  visual frame unavailable: {bars.Problem}");
                        nextSnapshot = DateTime.UtcNow.AddSeconds(SnapshotSeconds);
                    }
                    Thread.Sleep(PollMs);
                }
                // How the run stood, read before the bot is turned off: turning it off
                // fails a run that has no kill yet ("Controller stopped").
                ForeverAutonomousGoal.RunState ended = automatic.State;
                bool timedOut = DateTime.UtcNow >= autoDeadline;
                if (controller.IsBotActive)
                    controller.ToggleBotStatus();
                string summary = $"{ForeverRunProtocol.SummaryPrefix} {automatic.CompletedKills}; deaths observed: {automatic.ObservedDeaths}; " +
                    $"corpses looted: {automatic.CompletedLoots}; " +
                    $"status: {automatic.Status}; {automatic.LastFailure}";
                output.Line(summary);
                // A repeating route has no end of its own: the time bound, reached
                // without a failure, is how it ends.
                if (timedOut && request.Route is { Repeat: true } && ended != ForeverAutonomousGoal.RunState.Failed)
                {
                    output.Line($"Time limit reached; the repeating route ran until then ({automatic.AgendaStatus}).");
                    return new ForeverRunResult(ForeverRunProtocol.ExitComplete, summary);
                }
                int code = ended == ForeverAutonomousGoal.RunState.Complete ? ForeverRunProtocol.ExitComplete : ForeverRunProtocol.ExitFailed;
                return timedOut ? Problem(code, "Trial time limit reached before the run completed.") : new ForeverRunResult(code, summary);
            }

            if (controller.GoapAgent?.AvailableGoals is not [ForeverTrialGoal goal])
            {
                return Problem(ForeverRunProtocol.ExitRefused, "Normal controller did not load the isolated trial goal.");
            }
            trial = goal;
            stage = (int)ForeverRunStage.Preflight;

            output.Line("Normal Forever controller loaded with one bounded trial goal.");
            bool routeReady = goal.TryReadOnlyRoutePreflight(out string routeReason);
            if (!routeReady)
            {
                return Problem(ForeverRunProtocol.ExitRefused, $"Route preflight refused: {routeReason}. No game input sent.");
            }
            output.Line("Route preflight passed.");
            if (!run)
            {
                return Closing(ForeverRunProtocol.ExitComplete, "Read-only check passed; no game input sent.");
            }

            controller.ToggleBotStatus();
            started = true;
            stage = (int)ForeverRunStage.Running;
            DateTime deadline = DateTime.UtcNow.AddSeconds(request.TimeBoundSeconds);
            string lastStatus = string.Empty;
            while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline &&
                   goal.State is not (ForeverTrialGoal.Phase.Passed or ForeverTrialGoal.Phase.Failed))
            {
                string status = goal.TrialStatus;
                if (status != lastStatus)
                {
                    output.Line($"Controller phase: {status}");
                    lastStatus = status;
                }
                Thread.Sleep(PollMs);
            }
            if (controller.IsBotActive)
                controller.ToggleBotStatus();

            return Closing(goal.State == ForeverTrialGoal.Phase.Passed ? ForeverRunProtocol.ExitComplete : ForeverRunProtocol.ExitFailed,
                $"Controller trial status: {goal.TrialStatus}. {goal.LastFailure}");
        }
        catch (Exception ex)
        {
            if (!started)
            {
                return Problem(ForeverRunProtocol.ExitRefused, $"Controller trial refused: {ex.Message}");
            }
            return Problem(ForeverRunProtocol.ExitFailed, $"Controller trial stopped by an error: {ex.Message}");
        }
        finally
        {
            cts.Cancel();
        }
    }

    // The run's last word, printed as a problem: why it was refused or failed.
    private ForeverRunResult Problem(int exitCode, string text)
    {
        output.Problem(text);
        return new ForeverRunResult(exitCode, text);
    }

    // The run's last word, printed as its own text: how it closed.
    private ForeverRunResult Closing(int exitCode, string text)
    {
        output.Line(text);
        return new ForeverRunResult(exitCode, text);
    }

    private bool WaitForGameWindow(WowProcess process, CancellationToken token)
    {
        if (WinAPI.NativeMethods.GetForegroundWindow() == process.MainWindowHandle)
            return true;
        stage = (int)ForeverRunStage.WaitingForClick;
        output.Line($"{ForeverRunProtocol.ClickPrompt} to start the run (waiting up to 60 seconds).");
        for (int i = 0; i < ClickAttempts && !token.IsCancellationRequested; i++)
        {
            if (WinAPI.NativeMethods.GetForegroundWindow() == process.MainWindowHandle)
            {
                // Let the click that activated the window finish first.
                Thread.Sleep(ClickSettleMs);
                return true;
            }
            Thread.Sleep(PollMs);
        }
        return false;
    }

    // The pet as the snapshot shows it: absent, dead, idle, on the target or on another unit.
    private static string PetState(VisualBarSample bars) => bars.Pet switch
    {
        null => "unreadable",
        { Exists: not true } => "none",
        { Dead: true } => "dead",
        { TargetHash1: 0, TargetHash2: 0 } => "idle",
        { } pet when bars.TargetIdentity is { } target && pet.Attacks(target) => "on the target",
        _ => "on another unit"
    };
}
