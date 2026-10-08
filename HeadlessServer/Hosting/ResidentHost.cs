using BotAgent;
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
/// throws before it was asked to stop, or when one of its background services ends by itself, faulted
/// or cancelled: on .NET 10 that stops the host as quietly as a stop does (<c>Run</c> returns, exit
/// code 0), so the services are looked at as the host starts stopping, when one that ended is what
/// stopped it. A failure while a run is still under way ends the process with
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
    /// <param name="failed">A background service ended by itself and stopped it, or it threw before it was asked to stop.</param>
    /// <param name="stopFailed">It threw once it was asked to stop: the stop failed, not the host (BlazorServer's rule too).</param>
    /// <param name="runUnderWay">A run had not ended when the host did.</param>
    public static HostEnd Decide(bool restartRequested, bool failed, bool stopFailed, bool runUnderWay)
    {
        if (restartRequested)
        {
            return HostEnd.Restart;
        }

        if (failed)
        {
            return runUnderWay ? HostEnd.Restart : HostEnd.Rebuild;
        }

        return stopFailed ? HostEnd.StoppedOnError : HostEnd.Stopped;
    }

    private static (HostEnd End, Exception? Failure) RunOnce(Func<IHost> build, Func<int> exitCode)
    {
        BotRuntimeHost? runs = null;
        bool stopRequested = false;
        Exception? fault = null;
        Exception? thrown = null;
        try
        {
            using IHost host = build();
            BackgroundService[] services = [.. host.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            runs = host.Services.GetService<BotRuntimeHost>();
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            {
                stopRequested = true;
                // A service that has already ended is what stopped the host; a person's stop finds them all running.
                fault = services.Select(EndedBy).FirstOrDefault(why => why is not null);
            });
            host.Run();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        bool failed = fault is not null || (thrown is not null && !stopRequested);
        HostEnd end = Decide(exitCode() == AgentProcess.RestartExitCode, failed, thrown is not null && stopRequested,
            runs?.CurrentRun is not null);
        return (end, fault ?? thrown);
    }

    // Why a service that ended by itself ended: what it threw, or that it was cancelled, which on .NET 10
    // stops the host as a fault does but leaves no exception to read (the review's V37). Null while it runs
    // or once it is done.
    private static Exception? EndedBy(BackgroundService service)
    {
        return service.ExecuteTask switch
        {
            { IsFaulted: true } task => task.Exception,
            { IsCanceled: true } => new OperationCanceledException($"{service.GetType().Name} ended cancelled before the host was stopped"),
            _ => null,
        };
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
