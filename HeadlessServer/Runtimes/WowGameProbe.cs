using BotAgent.Runtime;

using Contracts.Bots;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;

using System.Diagnostics;

namespace HeadlessServer.Runtimes;

/// <summary>
/// The WoW client on this PC, as <see cref="WowProcess"/> finds it (the <c>--pid</c> one, else the
/// first known executable), without attaching to it: no polling thread, nothing composed. Any failure
/// is no client: the process gone meanwhile, WMI failing, a client run as administrator while the
/// agent is not, or not Windows. It is called every few seconds and by every run, so one that throws
/// stopped the run under way (known issue 67); why is logged once, until the probe succeeds again.
/// </summary>
public sealed partial class WowGameProbe(RunOptions options, ILogger<WowGameProbe> logger) : IGameProbe
{
    private readonly Lock sync = new();
    private string? failing;

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

            (_, Version version) = WowProcess.ReadInfo(process);
            StartupClientVersion client = new(version);
            Failed(null);
            return new AgentGame(client.Path, DataConfig.ClientEra(client.Path), process.Id, version.ToString());
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
}
