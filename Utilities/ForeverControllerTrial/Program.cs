using Core.Forever;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// The dashboard keeps ForeverDiagnosticController. This explicit command runs
// the normal controller for a bounded trial only, through Core's ForeverRun:
// this file is its command line (arguments, the route, Ctrl+C and --listen).
bool run = args.Contains("--run");
bool sweep = args.Contains("--sweep");
// --listen: the host that started this run (the web UI) stops it with a
// "stop" line on standard input, and its input closing stops it too.
bool listen = args.Contains(ForeverRunProtocol.ListenOption);
// The host reads this output as UTF-8, whatever the console's code page.
if (listen)
{
    Console.OutputEncoding = Encoding.UTF8;
}
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
    return ForeverRunProtocol.ExitUsage;
}
if (roam is [] || roam is not null && (roam[..4].Any(OffMap) || roam[4] < 1 || roam[4] != Math.Floor(roam[4])))
{
    Console.Error.WriteLine("Use --roam <x1> <y1> <x2> <y2> <kills>: opposite corners on the current zone's map, 0 to 100, and a number of kills.");
    return ForeverRunProtocol.ExitUsage;
}
if (routeIndex >= 0 && routeName is null)
{
    Console.Error.WriteLine("Use --route <file>, a route file in Json/path/_forever or a path to one.");
    return ForeverRunProtocol.ExitUsage;
}
if (minutes is [] || minutes is [<= 0 or > ForeverRunRequest.MaxMinutes])
{
    Console.Error.WriteLine("Use --minutes <n> with a time bound from 1 to 600 minutes.");
    return ForeverRunProtocol.ExitUsage;
}
bool task = travel is not null || roam is not null || routeName is not null;
bool auto = args.Contains("--auto-check") || args.Contains("--auto-run") || sweep || task;
run |= args.Contains("--auto-run") || sweep || task;
if (args.Where((_, i) => !values.Contains(i))
        .Any(a => a is not ("--check" or "--run" or "--auto-check" or "--auto-run" or "--sweep" or "--travel" or
            "--roam" or "--route" or "--minutes" or "--pid" or ForeverRunProtocol.ListenOption) && !int.TryParse(a, out _)) ||
    (run && (args.Contains("--check") || args.Contains("--auto-check"))) ||
    (auto && (args.Contains("--check") || args.Contains("--run"))) ||
    (minutes is not null && !run) ||
    new[] { args.Contains("--auto-run"), sweep, travel is not null, roam is not null, routeName is not null }
        .Count(mode => mode) > 1)
{
    Console.Error.WriteLine("Use --check, --run, --auto-check, --auto-run, --sweep, --travel <x> <y>, " +
        "--roam <x1> <y1> <x2> <y2> <kills> or --route <file>, [--minutes <n>] [--pid <WowB process ID>].");
    return ForeverRunProtocol.ExitUsage;
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
        return ForeverRunProtocol.ExitUsage;
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

static bool OffMap(double coordinate) => coordinate is < 0 or > ForeverMapPoint.MapSize || !double.IsFinite(coordinate);

int pidIndex = Array.IndexOf(args, "--pid");
int? requestedPid = null;
if (pidIndex >= 0)
{
    if (pidIndex + 1 >= args.Length ||
        !int.TryParse(args[pidIndex + 1], out int id) || id <= 0)
    {
        Console.Error.WriteLine("Use --pid with a positive WowB process ID.");
        return ForeverRunProtocol.ExitUsage;
    }
    requestedPid = id;
}

Process? wow = ForeverRun.FindClient(requestedPid);
if (wow is null)
{
    Console.Error.WriteLine(ForeverRun.NotRunning);
    return ForeverRunProtocol.ExitRefused;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (listen)
{
    new Thread(() =>
    {
        try
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null &&
                   !line.Trim().Equals(ForeverRunProtocol.StopCommand, StringComparison.OrdinalIgnoreCase))
            {
            }
            Console.WriteLine(line is null ? "The host went away; stopping." : "Stop requested; stopping.");
            cts.Cancel();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    })
    { IsBackground = true, Name = "Forever run listener" }.Start();
}

ForeverRunMode mode = travel is not null ? ForeverRunMode.Travel
    : roam is not null ? ForeverRunMode.Roam
    : route is not null ? ForeverRunMode.Route
    : sweep ? ForeverRunMode.Sweep
    : args.Contains("--auto-run") ? ForeverRunMode.AutoRun
    : args.Contains("--auto-check") ? ForeverRunMode.AutoCheck
    : run ? ForeverRunMode.Run
    : ForeverRunMode.Check;
ForeverRunRequest request = new(mode)
{
    TravelTo = travel is [double travelX, double travelY] ? (travelX, travelY) : null,
    Roam = roam is [double x1, double y1, double x2, double y2, double kills] ? new ForeverRoamRequest(x1, y1, x2, y2, (int)kills) : null,
    Route = route,
    Minutes = minutes is [double bound] ? bound : null,
    DataRoot = Path.GetFullPath("Json"),
    // A capture the bars cannot be read from is kept beside the live logs, when there are any.
    UnreadableFrameFolder = Directory.Exists(Path.Combine("local", "logs")) ? Path.GetFullPath(Path.Combine("local", "logs")) : null,
};
ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TrialLoggerProvider()));
return new ForeverRun(request, loggerFactory, new ConsoleRunOutput()).Run(wow.Id, cts.Token).ExitCode;

// The run's lines, printed as the runner always printed them; the web UI's run host reads them.
sealed class ConsoleRunOutput : IForeverRunOutput
{
    public void Line(string text) => Console.WriteLine(text);
    public void Problem(string text) => Console.Error.WriteLine(text);
    public void Snapshot(string text) => Console.WriteLine(text);
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
