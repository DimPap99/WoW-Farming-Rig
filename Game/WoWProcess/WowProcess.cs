using Microsoft.Extensions.Options;

using SharedLib;

using System;
using System.Diagnostics;
using System.Threading;

#nullable enable

namespace Game;

/// <summary>
/// The WoW client the bot attaches to: found by process id or by a known executable name, polled
/// every <see cref="PollInterval"/> on a thread of its own for the client ending (and another one of
/// the known names starting). Dispose it with whatever built it: that ends the thread.
/// </summary>
public sealed class WowProcess : IDisposable
{
    /// <summary>How often the client is looked at again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly string[] defaultProcessNames = [
        "Wow",
        "WowClassic",
        "WowClassicT",
        "Wow-64",
        "WowClassicB",
        "WowB", // World of Warcraft: Forever beta
        // Windows on ARM64 (e.g. WoW running natively inside a Win11 ARM64 VM
        // on Apple Silicon). Match is OrdinalIgnoreCase, so casing is irrelevant.
        "WowClassic-arm64"
    ];

    private readonly PollThread poll;

    public Version FileVersion { get; private set; }

    public string Path { get; private set; }

    private Process process;

    private int id = -1;
    public int Id
    {
        get => id;
        set
        {
            id = value;
            process = Process.GetProcessById(id);
        }
    }

    public string ProcessName => process.ProcessName;

    public IntPtr MainWindowHandle => process.MainWindowHandle;

    public bool IsRunning { get; private set; }

    private WowProcess(CancellationTokenSource cts, int pid = -1)
    {
        Process? p = Get(pid)
            ?? throw new NullReferenceException(
                $"Unable to find {(pid == -1 ? "any" : $"pid={pid}")} " +
                $"running World of Warcraft process!");

        process = p;
        id = process.Id;
        IsRunning = true;
        (Path, FileVersion) = GetProcessInfo();

        // A poll that throws (the client gone between two calls, WMI failing) is a client not running.
        poll = new PollThread(nameof(WowProcess), PollInterval, PollProcessExited, _ => IsRunning = false, cts.Token);
    }

    public WowProcess(CancellationTokenSource cts, IOptions<StartupConfigPid> options) : this(cts, options.Value.Id) { }

    public void Dispose()
    {
        poll.Dispose();
    }

    private void PollProcessExited()
    {
        process.Refresh();
        if (!process.HasExited)
        {
            return;
        }

        IsRunning = false;
        Process? p = Get();
        if (p != null)
        {
            process = p;
            id = process.Id;
            IsRunning = true;
            (Path, FileVersion) = GetProcessInfo();
        }
    }

    public static Process? Get(int processId = -1)
    {
        if (processId != -1)
        {
            return Process.GetProcessById(processId);
        }

        Process[] processList = Process.GetProcesses();
        for (int i = 0; i < processList.Length; i++)
        {
            Process p = processList[i];
            for (int j = 0; j < defaultProcessNames.Length; j++)
            {
                if (defaultProcessNames[j].Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private (string path, Version version) GetProcessInfo() => ReadInfo(process);

    /// <summary>
    /// Where <paramref name="process"/>'s executable is and its file version, corrected where
    /// Blizzard shipped a broken one; an empty version when the file has none.
    /// </summary>
    public static (string Path, Version Version) ReadInfo(Process process)
    {
        string path = WinAPI.ExecutablePath.Get(process)
            ?? throw new NullReferenceException("Unable to identify World of Warcraft process path!");

        var exePath = System.IO.Path.Join(path, process.ProcessName + ".exe");
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);

        if (info.FileMajorPart > 0)
        {
            Version v = new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);

            v = CorrectVersion(v);

            return (path, v);
        }

        return (path, new Version());
    }

    // Blizzard occasionally ships executables with broken file versions
    // where FileMajorPart encodes realMajor*100+realMinor (e.g. 115 means
    // Major=1, Minor=15), FileMinorPart is the real Build, and
    // FileBuildPart*10+FilePrivatePart gives the real Revision.
    private static Version CorrectVersion(Version v)
    {
        if (v.Major < 100)
            return v;

        return new Version(
            v.Major / 100,
            v.Major % 100,
            v.Minor,
            v.Build * 10 + v.Revision);
    }
}
