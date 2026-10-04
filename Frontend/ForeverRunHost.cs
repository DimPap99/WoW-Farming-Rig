using Core.Forever;

using Game;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Frontend;

/// <summary>A route file as the runs page lists it: where it is, its name and steps, or why it is refused.</summary>
public sealed record ForeverRouteChoice(string FullPath, string File, string Name, IReadOnlyList<string> Steps,
    bool Repeat, string Error);

/// <summary>
/// Starts and stops WoW: Forever runs for the web UI, which itself stays read-only.
/// A run is the same trial runner the live tests use (<c>ForeverControllerTrial
/// --route</c>), started as a child process on the client this server watches,
/// listening (<see cref="ForeverRunProtocol.ListenOption"/>): its output is kept
/// for the page (and in <c>local/logs</c> when that folder exists), and
/// <see cref="ForeverRunProtocol.StopCommand"/> on its input stops it the way
/// Ctrl+C does, releasing what it holds. One run at a time.
/// </summary>
public sealed class ForeverRunHost : IDisposable
{
    /// <summary>How long a stopped run gets to release what it holds and exit before it is ended.</summary>
    public const int StopGraceMs = 10000;
    private const string RunnerName = "ForeverControllerTrial";
    private const string SolutionFile = "WoWFarmingRig.sln";

    private readonly object sync = new();
    private readonly WorldMapAreaDB maps;
    private readonly WowProcess wow;
    private readonly string repositoryRoot;
    private readonly string routesFolder;
    private readonly ForeverRunOutput output = new();
    private Process? process;
    private StreamWriter? log;
    private Timer? killTimer;

    public ForeverRunHost(DataConfig data, WorldMapAreaDB maps, WowProcess wow)
    {
        this.maps = maps;
        this.wow = wow;
        repositoryRoot = FindRepositoryRoot() ?? Path.GetFullPath(Path.Combine(data.Root, ".."));
        // The runner reads routes from Json under its working directory, the repository.
        routesFolder = Path.Combine(repositoryRoot, "Json", "path", ForeverRouteFile.Folder);
    }

    /// <summary>Something the page shows changed: a line, the state.</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public bool StopRequested { get; private set; }
    public string Route { get; private set; } = string.Empty;
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public int? ExitCode { get; private set; }
    /// <inheritdoc cref="ForeverRunOutput.Phase"/>
    public string Phase => output.Phase;
    /// <inheritdoc cref="ForeverRunOutput.Summary"/>
    public string Summary => output.Summary;
    /// <summary>The runner asks for a click into the game before it moves anything.</summary>
    public bool WaitingForClick => IsRunning && output.ClickPrompted;
    public string LogFile { get; private set; } = string.Empty;

    /// <summary>The run's output in order, with the frame snapshots when <paramref name="withSnapshots"/>.</summary>
    public IReadOnlyList<ForeverRunLine> Output(bool withSnapshots)
    {
        lock (sync)
        {
            return output.Lines(withSnapshots);
        }
    }

    /// <summary>The route files the runner can run, each loaded and checked as a run would.</summary>
    public IReadOnlyList<ForeverRouteChoice> Routes()
    {
        if (!Directory.Exists(routesFolder))
            return [];
        List<ForeverRouteChoice> routes = [];
        foreach (string path in Directory.EnumerateFiles(routesFolder, "*.json").OrderBy(Path.GetFileName))
        {
            string file = Path.GetFileName(path);
            routes.Add(ForeverRouteFile.TryLoad(path, maps, out ForeverRoute route, out string error)
                ? new(path, file, route.Name, [.. route.Tasks.Select(task => task.Name)], route.Repeat, string.Empty)
                : new(path, file, file, [], false, error));
        }
        return routes;
    }

    /// <summary>Starts <paramref name="route"/> for at most <paramref name="minutes"/>; false with why when it cannot.</summary>
    public bool TryStart(ForeverRouteChoice route, int minutes, out string error)
    {
        lock (sync)
        {
            if (IsRunning)
            {
                error = "a run is already under way";
                return false;
            }
            if (FindRunner() is not { } runner)
            {
                error = $"the {RunnerName} build was not found; build it with: dotnet build Utilities/{RunnerName}";
                return false;
            }

            ProcessStartInfo start = new(runner)
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // The full path: the runner looks in its working directory first.
            List<string> arguments = ["--route", route.FullPath,
                "--minutes", minutes.ToString(CultureInfo.InvariantCulture), ForeverRunProtocol.ListenOption];
            // The client this server watches, not whichever the runner would pick.
            if (wow.Id > 0)
                arguments.AddRange(["--pid", wow.Id.ToString(CultureInfo.InvariantCulture)]);
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            Process child = new() { StartInfo = start, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) => Received(child, e.Data);
            child.ErrorDataReceived += (_, e) => Received(child, e.Data);
            child.Exited += (_, _) => Ended(child);
            try
            {
                child.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The last run stays on the page as it was.
                child.Dispose();
                error = $"the run could not start: {ex.Message}";
                return false;
            }

            process?.Dispose();
            process = child;
            output.Clear();
            Route = route.File;
            StartedAtUtc = DateTime.UtcNow;
            EndedAtUtc = null;
            ExitCode = null;
            StopRequested = false;
            log = OpenLog(route.File);
            IsRunning = true;
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }
        Changed?.Invoke();
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Asks the run to stop: it releases its keys and the mouse and exits, as
    /// with Ctrl+C. Only a run that has not exited after <see cref="StopGraceMs"/>
    /// is ended outright.
    /// </summary>
    public void Stop()
    {
        lock (sync)
        {
            if (!IsRunning || process is not { } child || StopRequested)
                return;
            StopRequested = true;
            try
            {
                child.StandardInput.WriteLine(ForeverRunProtocol.StopCommand);
                child.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
            killTimer = new(_ => Kill(child), null, StopGraceMs, Timeout.Infinite);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The server goes away: a run is stopped and gets its full grace period to
    /// release what it holds, as BlazorServer stops nothing before input is released.
    /// </summary>
    public void Dispose()
    {
        Process? child;
        lock (sync)
        {
            child = IsRunning ? process : null;
        }
        if (child is not null)
        {
            Stop();
            try
            {
                if (!child.WaitForExit(StopGraceMs))
                    Kill(child);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        lock (sync)
        {
            killTimer?.Dispose();
            killTimer = null;
            log?.Dispose();
            log = null;
            process?.Dispose();
            process = null;
        }
    }

    private void Received(Process child, string? line)
    {
        if (line is null)
            return;
        lock (sync)
        {
            if (child != process)
                return;
            output.Add(line);
            log?.WriteLine(line);
        }
        Changed?.Invoke();
    }

    // Runs on the thread that saw the exit. The streams may still hold the
    // last lines: they are read to the end first, outside the lock their
    // handler takes. The process object is kept until the next start or the
    // server's end, so nothing here races with Dispose.
    private void Ended(Process child)
    {
        int? code = null;
        try
        {
            child.WaitForExit();
            code = child.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        lock (sync)
        {
            if (child != process)
                return;
            ExitCode = code;
            EndedAtUtc = DateTime.UtcNow;
            IsRunning = false;
            killTimer?.Dispose();
            killTimer = null;
            log?.WriteLine($"exit={code}");
            log?.Dispose();
            log = null;
        }
        Changed?.Invoke();
    }

    private static void Kill(Process child)
    {
        try
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    // The repository the server runs from: the folder with the solution, above its build output.
    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, SolutionFile)))
                return folder.FullName;
        }
        return null;
    }

    // The runner's build output written last, its own files and the Core it
    // carries alike, so a rebuild that only changed Core counts.
    private string? FindRunner()
    {
        string bin = Path.Combine(repositoryRoot, "Utilities", RunnerName, "bin");
        if (!Directory.Exists(bin))
            return null;
        return Directory.EnumerateFiles(bin, RunnerName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(exe => Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.dll")
                .Append(exe).Max(File.GetLastWriteTimeUtc))
            .FirstOrDefault();
    }

    // Beside the live tests' logs, when the repository keeps them.
    private StreamWriter? OpenLog(string routeFile)
    {
        string folder = Path.Combine(repositoryRoot, "local", "logs");
        if (!Directory.Exists(folder))
        {
            LogFile = string.Empty;
            return null;
        }
        LogFile = Path.Combine(folder,
            $"web-{Path.GetFileNameWithoutExtension(routeFile)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            return new StreamWriter(LogFile, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFile = string.Empty;
            return null;
        }
    }
}
