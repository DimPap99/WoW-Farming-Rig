using BotAgent.Runtime;

using Contracts.Bots;

using Game;

using SharedLib;

using System.ComponentModel;
using System.Diagnostics;

namespace HeadlessServer.Runtimes;

/// <summary>
/// The WoW client on this PC, as <see cref="WowProcess"/> finds it (the <c>--pid</c> one, else the
/// first known executable), without attaching to it: no polling thread, nothing composed.
/// </summary>
public sealed class WowGameProbe(RunOptions options) : IGameProbe
{
    public AgentGame? Probe()
    {
        try
        {
            using Process? process = WowProcess.Get(options.Pid);
            if (process is null || process.HasExited)
            {
                return null;
            }

            (_, Version version) = WowProcess.ReadInfo(process);
            StartupClientVersion client = new(version);
            return new AgentGame(client.Path, DataConfig.ClientEra(client.Path), process.Id, version.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or
            NullReferenceException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Not running, gone meanwhile, or not readable (another user's process, not Windows).
            return null;
        }
    }
}
