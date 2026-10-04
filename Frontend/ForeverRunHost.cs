using Core.Forever;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Frontend;

/// <summary>A route file as the runs page lists it: its file, its name and steps, or why it is refused.</summary>
public sealed record ForeverRouteChoice(string File, string Name, IReadOnlyList<string> Steps, bool Repeat, string Error);

/// <summary>
/// Starts and stops WoW: Forever runs for the web UI, which itself stays read-only.
/// A run is the same trial runner the live tests use (<c>ForeverControllerTrial
/// --route</c>), started as a child process with <c>--listen</c>: its output is
/// kept for the page (and in <c>local/logs</c> when that folder exists), and a
/// "stop" line on its input stops it the way Ctrl+C does, releasing what it holds.
/// One run at a time.
/// </summary>
public sealed class ForeverRunHost : IDisposable
{
    /// <summary>Output lines kept for the page.</summary>
    public const int KeptLines = 400;
    private const int StopGraceMs = 10000;
    private const string RunnerName = "ForeverControllerTrial";

    private readonly object sync = new();
    private readonly string repositoryRoot;
    private readonly DataConfig data;
    private readonly Queue<string> lines = new();
    private WorldMapAreaDB? maps;
    private Process? process;
    private StreamWriter? log;
    private Timer? killTimer;

    public ForeverRunHost(DataConfig data)
    {
        this.data = data;
        repositoryRoot = Path.GetFullPath(Path.Combine(data.Root, ".."));
    }

    /// <summary>Something the page shows changed: a line, the state.</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public bool StopRequested { get; private set; }
    public string Route { get; private set; } = string.Empty;
    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public int? ExitCode { get; private set; }
    /// <summary>The latest "Autonomous phase" line: the activity, its step and the route's progress.</summary>
    public string Phase { get; private set; } = string.Empty;
    /// <summary>The run's closing line: kills, loot and its status.</summary>
    public string Summary { get; private set; } = string.Empty;
    /// <summary>The trial runner asks for a click into the game before it moves anything.</summary>
    public bool WaitingForClick { get; private set; }
    public string LogFile { get; private set; } = string.Empty;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (sync)
                return [.. lines];
        }
    }

    /// <summary>The route files in <c>Json/path/_forever</c>, each loaded and checked as a run would.</summary>
    public IReadOnlyList<ForeverRouteChoice> Routes()
    {
        string folder = Path.Combine(data.Path, ForeverRouteFile.Folder);
        if (!Directory.Exists(folder))
            return [];
        maps ??= new WorldMapAreaDB(data);
        List<ForeverRouteChoice> routes = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*.json").OrderBy(Path.GetFileName))
        {
            string file = Path.GetFileName(path);
            routes.Add(ForeverRouteFile.TryLoad(path, maps, out ForeverRoute route, out string error)
                ? new(file, route.Name, [.. route.Tasks.Select(task => task.Name)], route.Repeat, string.Empty)
                : new(file, file, [], false, error));
        }
        return routes;
    }

    /// <summary>Starts <paramref name="routeFile"/> for at most <paramref name="minutes"/>; false with why when it cannot.</summary>
    public bool TryStart(string routeFile, int minutes, out string error)
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
            foreach (string argument in new[] { "--route", routeFile, "--minutes", minutes.ToString(), "--listen" })
                start.ArgumentList.Add(argument);

            Process child = new() { StartInfo = start, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) => Received(e.Data);
            child.ErrorDataReceived += (_, e) => Received(e.Data);
            child.Exited += (_, _) => Ended(child);

            lines.Clear();
            Route = routeFile;
            Phase = Summary = string.Empty;
            StartedAt = DateTime.Now;
            EndedAt = null;
            ExitCode = null;
            StopRequested = false;
            WaitingForClick = false;
            log = OpenLog(routeFile);
            try
            {
                child.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log?.Dispose();
                log = null;
                error = $"the run could not start: {ex.Message}";
                return false;
            }
            process = child;
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
    /// with Ctrl+C. A run that has not exited after a grace period is ended.
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
                child.StandardInput.WriteLine("stop");
                child.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
            killTimer = new(_ => Kill(child), null, StopGraceMs, Timeout.Infinite);
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Process? child;
        lock (sync)
            child = IsRunning ? process : null;
        if (child is null)
            return;
        Stop();
        if (!child.WaitForExit(3000))
            Kill(child);
    }

    private void Received(string? line)
    {
        if (line is null)
            return;
        lock (sync)
        {
            lines.Enqueue(line);
            while (lines.Count > KeptLines)
                lines.Dequeue();
            log?.WriteLine(line);
            if (line.StartsWith("Autonomous phase:", StringComparison.Ordinal))
            {
                Phase = line["Autonomous phase:".Length..].Trim();
                WaitingForClick = false;
            }
            else if (line.StartsWith("Autonomous kills:", StringComparison.Ordinal))
                Summary = line;
            else if (line.StartsWith("Click into the game window", StringComparison.Ordinal))
                WaitingForClick = true;
        }
        Changed?.Invoke();
    }

    private void Ended(Process child)
    {
        // The streams may still hold the last lines; they are read to the end
        // first, outside the lock their handler takes.
        child.WaitForExit();
        lock (sync)
        {
            if (child != process)
                return;
            ExitCode = child.ExitCode;
            EndedAt = DateTime.Now;
            IsRunning = false;
            WaitingForClick = false;
            killTimer?.Dispose();
            killTimer = null;
            log?.WriteLine($"exit={ExitCode}");
            log?.Dispose();
            log = null;
            child.Dispose();
            process = null;
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

    // The newest build of the trial runner beside the repository's Json folder.
    private string? FindRunner()
    {
        string bin = Path.Combine(repositoryRoot, "Utilities", RunnerName, "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, RunnerName + ".exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
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
