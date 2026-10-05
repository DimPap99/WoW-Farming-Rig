using BotAgent.Runtime;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessServer.Hosting;

/// <summary>
/// The agent's way out when a run cannot be torn down: the host stops, and <c>Program.Main</c> ends
/// the process with <see cref="RestartExitCode"/>, which takes the stuck run's threads with it; the
/// logon task's loop (<c>run-agent.ps1</c>) starts the agent again on that code.
/// </summary>
public sealed partial class AgentProcess(IHostApplicationLifetime lifetime, ILogger<AgentProcess> logger) : IAgentProcess
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
