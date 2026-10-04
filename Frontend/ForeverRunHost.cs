using Core.Forever;

using Game;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Frontend;

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

    private readonly object sync = new();
    private readonly WorldMapAreaDB maps;
    private readonly WowProcess wow;
    private readonly ForeverRunFiles files;
    private readonly ForeverRunOutput output = new();
    private Process? process;
    private StreamWriter? log;
    private Timer? killTimer;

    public ForeverRunHost(DataConfig data, WorldMapAreaDB maps, WowProcess wow)
    {
        this.maps = maps;
        this.wow = wow;
        files = ForeverRunFiles.Locate(data);
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
    /// <inheritdoc cref="ForeverRunOutput.CurrentTask"/>
    public int CurrentTask => output.CurrentTask;
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
    public IReadOnlyList<ForeverRouteChoice> Routes() => ForeverRouteCatalog.Load(files.RoutesFolder, maps);

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
            if (files.FindRunner() is not { } runner)
            {
                error = $"the {ForeverRunFiles.RunnerName} build was not found; build it with: dotnet build Utilities/{ForeverRunFiles.RunnerName}";
                return false;
            }

            ProcessStartInfo start = new(runner)
            {
                WorkingDirectory = files.RepositoryRoot,
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

    // Beside the live tests' logs, when the repository keeps them.
    private StreamWriter? OpenLog(string routeFile)
    {
        LogFile = files.NewLogFile(routeFile);
        if (LogFile.Length == 0)
            return null;
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
