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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

// The dashboard keeps ForeverDiagnosticController. This explicit command
// constructs the normal controller for a bounded trial only.
bool run = args.Contains("--run");
bool sweep = args.Contains("--sweep");
// --travel X Y: walk to that point of the current zone's map (0 to 100), then stop.
// --roam X1 Y1 X2 Y2 KILLS: roam that rectangle of the current zone's map until KILLS kills.
// --route FILE: run a route file from Json/path/_forever (or a path), steps in order.
// --minutes N: the run's time bound instead of the mode's own.
HashSet<int> values = [];
double[]? travel = NumbersAfter("--travel", 2);
double[]? roam = NumbersAfter("--roam", 5);
double[]? minutes = NumbersAfter("--minutes", 1);
string? routeName = null;
int routeIndex = Array.IndexOf(args, "--route");
if (routeIndex >= 0 && routeIndex + 1 < args.Length && !args[routeIndex + 1].StartsWith("--", StringComparison.Ordinal))
{
    routeName = args[routeIndex + 1];
    values.Add(routeIndex + 1);
}
if (travel is [] || travel is not null && travel.Any(OffMap))
{
    Console.Error.WriteLine("Use --travel <x> <y> with map coordinates of the current zone, 0 to 100.");
    return 1;
}
if (roam is [] || roam is not null && (roam[..4].Any(OffMap) || roam[4] < 1 || roam[4] != Math.Floor(roam[4])))
{
    Console.Error.WriteLine("Use --roam <x1> <y1> <x2> <y2> <kills>: opposite corners on the current zone's map, 0 to 100, and a number of kills.");
    return 1;
}
if (routeIndex >= 0 && routeName is null)
{
    Console.Error.WriteLine("Use --route <file>, a route file in Json/path/_forever or a path to one.");
    return 1;
}
if (minutes is [] || minutes is [<= 0 or > 600])
{
    Console.Error.WriteLine("Use --minutes <n> with a time bound from 1 to 600 minutes.");
    return 1;
}
bool task = travel is not null || roam is not null || routeName is not null;
bool auto = args.Contains("--auto-check") || args.Contains("--auto-run") || sweep || task;
run |= args.Contains("--auto-run") || sweep || task;
if (args.Where((_, i) => !values.Contains(i))
        .Any(a => a is not ("--check" or "--run" or "--auto-check" or "--auto-run" or "--sweep" or "--travel" or
            "--roam" or "--route" or "--minutes" or "--pid") && !int.TryParse(a, out _)) ||
    (run && (args.Contains("--check") || args.Contains("--auto-check"))) ||
    (auto && (args.Contains("--check") || args.Contains("--run"))) ||
    (minutes is not null && !run) ||
    new[] { args.Contains("--auto-run"), sweep, travel is not null, roam is not null, routeName is not null }
        .Count(mode => mode) > 1)
{
    Console.Error.WriteLine("Use --check, --run, --auto-check, --auto-run, --sweep, --travel <x> <y>, " +
        "--roam <x1> <y1> <x2> <y2> <kills> or --route <file>, [--minutes <n>] [--pid <WowB process ID>].");
    return 1;
}

// A route is read and checked before anything attaches to the game, so a
// mistake in the file shows at once.
ForeverRoute? route = null;
if (routeName is not null)
{
    DataConfig routeData = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    string routePath = ForeverRouteFile.Resolve(routeData, routeName);
    if (!ForeverRouteFile.TryLoad(routePath, new WorldMapAreaDB(routeData), out ForeverRoute loaded, out string routeError))
    {
        Console.Error.WriteLine($"Route refused: {routeError}");
        return 1;
    }
    route = loaded;
    Console.WriteLine($"Route {route.Name}: {route.Tasks.Count} steps{(route.Repeat ? ", repeating" : string.Empty)}.");
    for (int i = 0; i < route.Tasks.Count; i++)
        Console.WriteLine($"  {i + 1}. {route.Tasks[i].Name}");
}

// The numbers after a flag: null without the flag, empty when they do not parse.
double[]? NumbersAfter(string flag, int count)
{
    int index = Array.IndexOf(args, flag);
    if (index < 0)
        return null;
    double[] numbers = new double[count];
    for (int i = 0; i < count; i++)
    {
        if (index + 1 + i >= args.Length ||
            !double.TryParse(args[index + 1 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            return [];
        values.Add(index + 1 + i);
    }
    return numbers;
}

// The pet as the snapshot shows it: absent, dead, idle, on the target or on another unit.
static string PetState(VisualBarSample bars) => bars.Pet switch
{
    null => "unreadable",
    { Exists: not true } => "none",
    { Dead: true } => "dead",
    { TargetHash1: 0, TargetHash2: 0 } => "idle",
    { } pet when bars.TargetIdentity is { } target && pet.Attacks(target) => "on the target",
    _ => "on another unit"
};

static bool OffMap(double coordinate) => coordinate is < 0 or > ForeverMapPoint.MapSize || !double.IsFinite(coordinate);

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
    // Resolved when the profile loads, after the first frame named the zone
    // a --travel point lies in.
    ForeverRunOptions runOptions = new()
    {
        StopWhenNoTargets = sweep,
        MaxCompletedCycles = sweep ? null : 1
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
        Root = Path.GetFullPath("Json"), Exp = "forever"
    });
    services.AddCoreNormal(trialLogger);

    using ServiceProvider provider = services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    IWowScreen screen = provider.GetRequiredService<IWowScreen>();
    ForeverVisualBarTracker tracker = provider.GetRequiredService<ForeverVisualBarTracker>();
    // A capture the bars cannot be read from is kept beside the live logs, when there are any.
    if (Directory.Exists(Path.Combine("local", "logs")))
        tracker.UnreadableFrameFolder = Path.GetFullPath(Path.Combine("local", "logs"));
    bool ready = false;
    int classId = 0;
    int uiMapId = 0;
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
            uiMapId = bars.Position?.UIMapId ?? 0;
            break;
        }
        Thread.Sleep(100);
    }
    if (!ready)
    {
        string missing = tracker.TryGetLatest(out VisualBarSample last)
            ? $"{ForeverFrame.MissingTrialInput(last)} did not read"
            : tracker.Problem;
        Console.Error.WriteLine($"No complete fresh Forever trial frame ({missing}); no input sent.");
        return 2;
    }
    if (travel is [double travelX, double travelY])
    {
        ForeverMapPoint destination = new(uiMapId, travelX, travelY);
        runOptions = runOptions with { Tasks = [ForeverTasks.TravelTo(destination)] };
        Console.WriteLine($"Travelling to {destination}.");
    }
    if (roam is [double x1, double y1, double x2, double y2, double kills])
    {
        ForeverArea area = ForeverArea.Rectangle(uiMapId, (x1, y1), (x2, y2));
        runOptions = runOptions with { Tasks = [ForeverTasks.RoamArea(area, ForeverUntil.AfterKills((int)kills))] };
        Console.WriteLine($"Roaming the {area} until {kills} kills.");
    }
    if (route is not null)
        runOptions = runOptions with { Tasks = route.Tasks, RepeatTasks = route.Repeat };

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
        {
            IReadOnlyList<ForeverPullOption> pulls = automatic.PullOptions();
            Console.WriteLine(pulls.Count == 0
                ? "Pull options now: none in view."
                : "Pull options now, the best first: " + string.Join("; ", pulls) + ".");
            return 0;
        }

        // The run steers and clicks with the mouse, which the game follows only
        // while it is the active window; Windows will not hand that over to a
        // background process right after someone typed elsewhere.
        if (!WaitForGameWindow(process, cts.Token))
        {
            Console.Error.WriteLine("The game window did not become active; no input sent.");
            return 2;
        }
        controller.ToggleBotStatus();
        DateTime autoDeadline = DateTime.UtcNow.AddSeconds(minutes is [double bound] ? bound * 60 :
            route is not null ? 1200 : roam is not null ? 900 : sweep ? 600 : travel is not null ? 300 : 170);
        DateTime nextSnapshot = DateTime.MinValue;
        string autoLastStatus = string.Empty;
        while (!cts.IsCancellationRequested && DateTime.UtcNow < autoDeadline &&
               automatic.Status is not ("Failed" or "Complete"))
        {
            string status = automatic.Status;
            if (status != autoLastStatus)
            {
                Console.WriteLine($"Autonomous phase: {status}; completed cycles={automatic.CompletedKills}, looted={automatic.CompletedLoots}; {automatic.AgendaStatus}");
                autoLastStatus = status;
            }
            if (DateTime.UtcNow >= nextSnapshot)
            {
                if (tracker.TryGetLatest(out VisualBarSample current))
                    Console.WriteLine($"  map={current.Position?.UIMapId} {current.Position?.MapX * 100:0.00},{current.Position?.MapY * 100:0.00}, " +
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
                    Console.WriteLine($"  visual frame unavailable: {tracker.Problem}");
                nextSnapshot = DateTime.UtcNow.AddSeconds(2);
            }
            Thread.Sleep(100);
        }
        if (controller.IsBotActive)
            controller.ToggleBotStatus();
        Console.WriteLine($"Autonomous kills: {automatic.CompletedKills}; deaths observed: {automatic.ObservedDeaths}; " +
            $"corpses looted: {automatic.CompletedLoots}; " +
            $"status: {automatic.Status}; {automatic.LastFailure}");
        bool timedOut = DateTime.UtcNow >= autoDeadline;
        // A repeating route has no end of its own: the time bound, reached
        // without a failure, is how it ends.
        if (timedOut && route is { Repeat: true } && automatic.Status != "Failed")
        {
            Console.WriteLine($"Time limit reached; the repeating route ran until then ({automatic.AgendaStatus}).");
            return 0;
        }
        if (timedOut)
            Console.Error.WriteLine("Trial time limit reached before the run completed.");
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
