using Core;
using Core.Forever;
using Core.Goals;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PPather.Navmesh;

using SharedLib;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

// The dashboard keeps ForeverDiagnosticController. This explicit command
// constructs the normal controller for a bounded trial only.
bool run = args.Contains("--run");
bool sweep = args.Contains("--sweep");
bool auto = args.Contains("--auto-check") || args.Contains("--auto-run") || sweep;
run |= args.Contains("--auto-run") || sweep;
if (args.Any(a => a is not ("--check" or "--run" or "--auto-check" or "--auto-run" or "--sweep" or "--pid") &&
    !int.TryParse(a, out _)) ||
    (run && (args.Contains("--check") || args.Contains("--auto-check"))) ||
    (auto && (args.Contains("--check") || args.Contains("--run"))))
{
    Console.Error.WriteLine("Use --check, --run, --auto-check, --auto-run or --sweep [--pid <WowB process ID>].");
    return 1;
}

int pidIndex = Array.IndexOf(args, "--pid");
int? requestedPid = null;
if (pidIndex >= 0)
{
    if (pidIndex + 1 >= args.Length ||
        !int.TryParse(args[pidIndex + 1], out int id) || id <= 0)
    {
        Console.Error.WriteLine("Use --pid with a positive WowB process ID.");
        return 1;
    }
    requestedPid = id;
}

Process? wow = Process.GetProcessesByName("WowB")
    .OrderByDescending(p => p.StartTime)
    .FirstOrDefault(p => requestedPid is null || p.Id == requestedPid);
if (wow is null)
{
    Console.Error.WriteLine("WowB is not running.");
    return 2;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    WowProcess process = new(cts, Options.Create(new StartupConfigPid { Id = wow.Id }));
    if (process.FileVersion is not { Major: 1, Minor: >= 60 })
    {
        Console.Error.WriteLine("The selected client is not Forever.");
        return 2;
    }

    IServiceCollection services = new ServiceCollection();
    ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
        b.AddProvider(new TrialLoggerProvider()));
    ILogger trialLogger = loggerFactory.CreateLogger("ControllerTrial");
    services.AddSingleton(loggerFactory);
    services.AddSingleton(trialLogger);
    services.AddLogging();
    services.AddSingleton(cts);
    services.AddSingleton(process);
    services.AddSingleton(process.FileVersion);
    services.AddSingleton(new ForeverRunOptions
    {
        StopWhenNoTargets = sweep,
        MaxCompletedCycles = sweep ? null : 1
    });
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
        Root = Path.GetFullPath("Json"), Exp = "forever"
    });
    services.AddCoreNormal(trialLogger);

    using ServiceProvider provider = services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    IWowScreen screen = provider.GetRequiredService<IWowScreen>();
    ForeverVisualBarTracker tracker = provider.GetRequiredService<ForeverVisualBarTracker>();
    bool ready = false;
    int classId = 0;
    // Only this thread captures until BotController starts its addon thread.
    for (int i = 0; i < 100 && !cts.IsCancellationRequested; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample bars) &&
            ForeverFrame.HasTrialInputs(bars))
        {
            ready = true;
            classId = bars.PlayerIdentity?.ClassId ?? 0;
            break;
        }
        Thread.Sleep(100);
    }
    if (!ready)
    {
        Console.Error.WriteLine("No complete fresh Forever trial frame; no input sent.");
        return 2;
    }

    // The autonomous run uses the profile verified for the logged-in class;
    // the bounded route trial exists for the Paladin only.
    string? profile = auto
        ? ForeverVerifiedSetup.AutoProfileFor(classId)
        : ForeverVerifiedSetup.PaladinTrialProfile;
    if (profile is null)
    {
        Console.Error.WriteLine($"No verified Forever profile for class {(UnitClass)classId}; no input sent.");
        return 2;
    }
    Console.WriteLine($"Loading {profile}.");

    using BotController controller = ActivatorUtilities.CreateInstance<BotController>(provider);
    if (!controller.LoadClassProfile(profile))
    {
        Console.Error.WriteLine("Normal controller did not load the Forever profile.");
        return 2;
    }

    if (auto)
    {
        if (controller.GoapAgent?.AvailableGoals is not [ForeverAutonomousGoal automatic])
        {
            Console.Error.WriteLine("Normal controller did not load the autonomous goal.");
            return 2;
        }
        string autoReason = string.Empty;
        bool autoReady = false;
        // BotController's addon thread now owns capture. Calling Update here
        // too would race it on the shared screen image and tracker buffers.
        // A live run waits for a fight to end or health to come back.
        int attempts = run ? 600 : 50;
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
                Console.WriteLine($"Waiting for the preflight: {autoReason}.");
                waitingFor = autoReason;
            }
            Thread.Sleep(100);
        }
        if (!autoReady)
        {
            Console.Error.WriteLine($"Autonomous preflight refused: {autoReason}. No input sent.");
            return 2;
        }
        Console.WriteLine("Autonomous current-position preflight passed.");
        if (!run)
            return 0;

        // The run steers and clicks with the mouse, which the game follows only
        // while it is the active window; Windows will not hand that over to a
        // background process right after someone typed elsewhere.
        if (!WaitForGameWindow(process, cts.Token))
        {
            Console.Error.WriteLine("The game window did not become active; no input sent.");
            return 2;
        }
        controller.ToggleBotStatus();
        DateTime autoDeadline = DateTime.UtcNow.AddSeconds(sweep ? 600 : 170);
        DateTime nextSnapshot = DateTime.MinValue;
        string autoLastStatus = string.Empty;
        while (!cts.IsCancellationRequested && DateTime.UtcNow < autoDeadline &&
               automatic.Status is not ("Failed" or "Complete"))
        {
            string status = automatic.Status;
            if (status != autoLastStatus)
            {
                Console.WriteLine($"Autonomous phase: {status}; completed cycles={automatic.CompletedKills}, looted={automatic.CompletedLoots}");
                autoLastStatus = status;
            }
            if (DateTime.UtcNow >= nextSnapshot)
            {
                if (tracker.TryGetLatest(out VisualBarSample current))
                    Console.WriteLine($"  health={current.PlayerHealth:P0}, mana={current.PlayerPower:P0}, " +
                        $"target={current.TargetHealth:P0}, flags={current.TargetFlags}, " +
                        $"auras={current.SlotAuras?.ToString() ?? "unreadable"}, marker={current.TargetScreenPosition}, " +
                        $"plates={current.Nameplates?.PlateCount.ToString() ?? "unreadable"}, " +
                        $"range1/2/3={current.ActionStates?.GetSlot(1)?.InRange}/{current.ActionStates?.GetSlot(2)?.InRange}/{current.ActionStates?.GetSlot(3)?.InRange}, " +
                        $"attack={current.Attack?.ToString() ?? "unreadable"}, " +
                        $"soft={current.SoftInteract?.ToString() ?? "unreadable"}, " +
                        $"cast={current.LastCastEvent}, death={current.LastDeathEvent}, loot={current.Loot}, " +
                        $"frameReady={ForeverFrame.HasTrialInputs(current)}");
                else
                    Console.WriteLine("  visual frame unavailable");
                nextSnapshot = DateTime.UtcNow.AddSeconds(2);
            }
            Thread.Sleep(100);
        }
        if (controller.IsBotActive)
            controller.ToggleBotStatus();
        Console.WriteLine($"Autonomous kills: {automatic.CompletedKills}; deaths observed: {automatic.ObservedDeaths}; " +
            $"corpses looted: {automatic.CompletedLoots}; " +
            $"status: {automatic.Status}; {automatic.LastFailure}");
        if (DateTime.UtcNow >= autoDeadline)
            Console.Error.WriteLine("Trial time limit reached before the sweep completed.");
        return automatic.Status == "Complete" ? 0 : 3;
    }

    if (controller.GoapAgent?.AvailableGoals is not [ForeverTrialGoal goal])
    {
        Console.Error.WriteLine("Normal controller did not load the isolated trial goal.");
        return 2;
    }

    Console.WriteLine("Normal Forever controller loaded with one bounded trial goal.");
    bool routeReady = goal.TryReadOnlyRoutePreflight(out string routeReason);
    if (!routeReady)
    {
        Console.Error.WriteLine($"Route preflight refused: {routeReason}. No game input sent.");
        return 2;
    }
    Console.WriteLine("Route preflight passed.");
    if (!run)
    {
        Console.WriteLine("Read-only check passed; no game input sent.");
        return 0;
    }

    controller.ToggleBotStatus();
    DateTime deadline = DateTime.UtcNow.AddSeconds(195);
    string lastStatus = string.Empty;
    while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline &&
           goal.TrialStatus is not ("Passed" or "Failed"))
    {
        string status = goal.TrialStatus;
        if (status != lastStatus)
        {
            Console.WriteLine($"Controller phase: {status}");
            lastStatus = status;
        }
        Thread.Sleep(100);
    }
    if (controller.IsBotActive)
        controller.ToggleBotStatus();

    Console.WriteLine($"Controller trial status: {goal.TrialStatus}. {goal.LastFailure}");
    return goal.TrialStatus == "Passed" ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Controller trial refused: {ex.Message}");
    return 2;
}
finally
{
    cts.Cancel();
}

static bool WaitForGameWindow(WowProcess process, CancellationToken token)
{
    if (WinAPI.NativeMethods.GetForegroundWindow() == process.MainWindowHandle)
        return true;
    Console.WriteLine("Click into the game window to start the run (waiting up to 60 seconds).");
    for (int i = 0; i < 600 && !token.IsCancellationRequested; i++)
    {
        if (WinAPI.NativeMethods.GetForegroundWindow() == process.MainWindowHandle)
        {
            // Let the click that activated the window finish first.
            Thread.Sleep(1000);
            return true;
        }
        Thread.Sleep(100);
    }
    return false;
}

sealed class TrialLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TrialLogger(categoryName);
    public void Dispose() { }

    private sealed class TrialLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // The Forever runtime's own decisions are worth reading in a trial log.
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning ||
            level >= LogLevel.Information && category.Contains("Forever", StringComparison.Ordinal);
        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Console.Error.WriteLine($"[{category}] {formatter(state, exception)} {exception}");
        }
    }
}
