using BotAgent.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessServer.Hosting;

/// <summary>How a host ended, and so what the process does next.</summary>
public enum HostEnd
{
    /// <summary>It was stopped (Ctrl+C, the window closing, a logoff, a standalone run done): the process ends with the host's exit code.</summary>
    Stopped,

    /// <summary>It was stopped, and the stop itself threw: the process ends with an error, and is not built again.</summary>
    StoppedOnError,

    /// <summary>It failed with no run under way: an agent's host is built again in the same process.</summary>
    Rebuild,

    /// <summary>
    /// A run could not be torn down, or the host failed while a run was under way: only a new process
    /// takes the run's threads, so the process ends with <see cref="AgentProcess.RestartExitCode"/>.
    /// </summary>
    Restart,
}

/// <summary>
/// Runs HeadlessServer's host, and keeps the agent's running: a host that fails is built again after
/// <see cref="RestartDelay"/>, as <c>BlazorServer/Program.Main</c> does, keeping the log stream and the
/// configuration (which reloads its file, so a setting fixed meanwhile is read). A host fails when it
/// throws before it was asked to stop, or when one of its background services faults: on .NET 10 that
/// stops the host as quietly as a stop does (<c>Run</c> returns, exit code 0), so a fault is read from
/// the services' tasks. A failure while a run is still under way ends the process with
/// <see cref="AgentProcess.RestartExitCode"/> instead, and the logon task's loop (<c>run-agent.ps1</c>)
/// starts a new one.
/// </summary>
public static partial class ResidentHost
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs hosts from <paramref name="build"/> until one ends for good, and answers the process's exit
    /// code. Only a <paramref name="resident"/> agent's host is built again; a standalone run's failure is an error.
    /// </summary>
    public static int Run(Func<IHost> build, ILogger log, bool resident)
    {
        return Run(build, log, resident, RestartDelay, () => Environment.ExitCode);
    }

    /// <param name="exitCode">The process's exit code as the host left it: <see cref="AgentProcess"/> sets the restart's.</param>
    public static int Run(Func<IHost> build, ILogger log, bool resident, TimeSpan delay, Func<int> exitCode)
    {
        while (true)
        {
            (HostEnd end, Exception? failure) = RunOnce(build, exitCode);
            switch (end)
            {
                case HostEnd.Stopped:
                    return exitCode();
                case HostEnd.StoppedOnError:
                    LogStopFailed(log, failure);
                    return Program.ExitError;
                case HostEnd.Restart:
                    if (failure is not null)
                    {
                        LogFailedMidRun(log, failure);
                    }

                    return AgentProcess.RestartExitCode;
                case HostEnd.Rebuild when resident:
                    LogRebuild(log, failure, delay.TotalSeconds);
                    Thread.Sleep(delay);
                    continue;
                default:
                    LogFailed(log, failure);
                    return Program.ExitError;
            }
        }
    }

    /// <summary>What follows a host's end, from what was seen of it.</summary>
    /// <param name="restartRequested">A run could not be torn down (the exit code asks for a new process).</param>
    /// <param name="threw">Building or running it threw.</param>
    /// <param name="stopRequested">It was asked to stop: by a person, the system, or a faulted service.</param>
    /// <param name="serviceFaulted">One of its background services faulted.</param>
    /// <param name="runUnderWay">A run had not ended when the host did.</param>
    public static HostEnd Decide(bool restartRequested, bool threw, bool stopRequested, bool serviceFaulted, bool runUnderWay)
    {
        if (restartRequested)
        {
            return HostEnd.Restart;
        }

        // A throw once a stop was asked for is the stop failing, not the host: BlazorServer's rule too.
        bool failed = serviceFaulted || (threw && !stopRequested);
        if (!failed)
        {
            return threw ? HostEnd.StoppedOnError : HostEnd.Stopped;
        }

        return runUnderWay ? HostEnd.Restart : HostEnd.Rebuild;
    }

    private static (HostEnd End, Exception? Failure) RunOnce(Func<IHost> build, Func<int> exitCode)
    {
        BackgroundService[] services = [];
        BotRuntimeHost? runs = null;
        bool stopRequested = false;
        Exception? thrown = null;
        try
        {
            using IHost host = build();
            services = [.. host.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            runs = host.Services.GetService<BotRuntimeHost>();
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopRequested = true);
            host.Run();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Exception? fault = services.Select(service => service.ExecuteTask?.Exception).FirstOrDefault(exception => exception is not null);
        HostEnd end = Decide(exitCode() == AgentProcess.RestartExitCode, thrown is not null, stopRequested, fault is not null,
            runs?.CurrentRun is not null);
        return (end, thrown ?? fault);
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Critical, Message = "The agent's host failed; it is built again in {Seconds} s")]
    static partial void LogRebuild(ILogger logger, Exception? exception, double seconds);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The agent's host failed while a run was under way: the agent restarts")]
    static partial void LogFailedMidRun(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "HeadlessServer's host failed")]
    static partial void LogFailed(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "HeadlessServer failed while it stopped")]
    static partial void LogStopFailed(ILogger logger, Exception? exception);

    #endregion
}
