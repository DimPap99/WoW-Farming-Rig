using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;

namespace BotAgent;

/// <summary>
/// The agent's way out when a run cannot be torn down, the <see cref="Runtime.RestartProcess"/>
/// <see cref="AgentRegistration.AddBotAgent"/> registers: the host stops with <see cref="RestartExitCode"/>
/// as the process's exit code, and the process ends, which takes the stuck run's threads with it; the
/// launcher's loop (HeadlessServer's <c>run-agent.ps1</c>) starts the agent again on that code.
/// </summary>
public sealed partial class AgentProcess(IHostApplicationLifetime lifetime, ILogger<AgentProcess> logger)
{
    /// <summary>EX_TEMPFAIL: "try again", which the launcher's loop does.</summary>
    public const int RestartExitCode = 75;

    public void Restart(string reason)
    {
        LogRestart(logger, reason);
        Environment.ExitCode = RestartExitCode;
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The agent restarts: {Reason}")]
    static partial void LogRestart(ILogger logger, string reason);
}
