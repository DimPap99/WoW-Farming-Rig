using BotAgent.Runtime;

using Contracts.Bots;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;

using System.Diagnostics;

namespace HeadlessServer.Runtimes;

/// <summary>
/// The agent's <see cref="ProbeGame"/>: the WoW client on this PC, as <see cref="WowProcess"/> finds it (the <c>--pid</c> one, else the
/// first known executable), without attaching to it: no polling thread, nothing composed. Any failure
/// is no client: the process gone meanwhile, WMI failing, a client run as administrator while the
/// agent is not, or not Windows. It is called every few seconds and by every run, so one that throws
/// stopped the run under way (known issue 67); why is logged once, until the probe succeeds again.
/// </summary>
public sealed partial class WowGameProbe(RunOptions options, ILogger<WowGameProbe> logger)
{
    private readonly Lock sync = new();
    private string? failing;
    // The client last read: its file is read once per process, as asking Windows for it is not free.
    private volatile Known? known;

    public AgentGame? Probe()
    {
        try
        {
            using Process? process = WowProcess.Get(options.Pid);
            if (process is null || process.HasExited)
            {
                Failed(null);
                return null;
            }

            // An id is used again once its process ended: the start time tells the processes apart.
            DateTime started = process.StartTime;
            if (known is { } seen && seen.ProcessId == process.Id && seen.Started == started)
            {
                Failed(null);
                return seen.Game;
            }

            (_, Version version) = WowProcess.ReadInfo(process);
            StartupClientVersion client = new(version);
            AgentGame game = new(client.Path, DataConfig.ClientEra(client.Path), process.Id, version.ToString());
            known = new Known(process.Id, started, game);
            Failed(null);
            return game;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed(ex);
            return null;
        }
    }

    private void Failed(Exception? exception)
    {
        lock (sync)
        {
            string? message = exception?.Message;
            if (message == failing)
            {
                return;
            }

            failing = message;
        }

        // Not Windows, as when the agent is tested on Linux, is not news.
        if (exception is not (null or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException))
        {
            LogUnreadable(logger, exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A WoW client could not be read, so none is seen: {Reason}")]
    static partial void LogUnreadable(ILogger logger, string reason);

    private sealed record Known(int ProcessId, DateTime Started, AgentGame Game);
}
