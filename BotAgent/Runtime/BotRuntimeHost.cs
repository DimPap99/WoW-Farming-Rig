using Contracts.Bots;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Runtime;

/// <summary>
/// The agent's one run at a time. A start builds the runtime its assignment needs (after any
/// preparation, such as pulling data) and runs it on a thread of its own. A stop is a command, not a
/// kill: the run's token is cancelled and the run releases input and ends by itself. Only a run that
/// has not ended after the grace period (<see cref="AgentOptions.StopGrace"/>) is forced: its keys
/// are released from here, and one that still does not end is abandoned and the agent's process
/// restarted, the in-process equivalent of ending a child that would not stop. The host's own
/// shutdown stops a run the same way.
/// </summary>
public sealed partial class BotRuntimeHost(IBotRuntimeFactory factory, IEnumerable<IRunPreparation> preparations,
    IAgentProcess process, AgentOptions options, TimeProvider clock, ILogger<BotRuntimeHost> logger) : IHostedService, IDisposable
{
    /// <summary>After the grace period, how long the forced release may take.</summary>
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(2);

    private readonly TimeSpan abandonWait = options.StopGrace / 2;

    private readonly IRunPreparation[] preparations = [.. preparations];
    private readonly Lock sync = new();
    private ActiveRun? active;
    private RunReport? last;
    private BotAssignment? assignment;
    private bool stopping;

    /// <summary>A run ended, or was abandoned; raised once per run, on the thread that saw it end.</summary>
    public event Action<RunReport>? RunEnded;

    /// <summary>The bot's assignment as the manager last said: shown, and never run without a start command.</summary>
    public BotAssignment? Assignment
    {
        get
        {
            lock (sync)
            {
                return assignment;
            }
        }
        set
        {
            lock (sync)
            {
                assignment = value;
            }
        }
    }

    /// <summary>The run under way, or null.</summary>
    public BotRunInfo? CurrentRun
    {
        get
        {
            lock (sync)
            {
                return active?.Info;
            }
        }
    }

    /// <summary>How the last run ended, or null.</summary>
    public RunReport? LastReport
    {
        get
        {
            lock (sync)
            {
                return last;
            }
        }
    }

    /// <summary>Starts <paramref name="assignment"/> unless a run is under way; the answer says which.</summary>
    public CommandAck Start(BotAssignment? assignment)
    {
        if (AssignmentRules.Problem(assignment) is string problem)
        {
            return new CommandAck(false, problem, null);
        }

        ActiveRun run;
        lock (sync)
        {
            // A run started now would be cut off by the process ending under it (known issue 76).
            if (stopping)
            {
                return new CommandAck(false, "the agent is stopping", null);
            }

            if (active is { } current)
            {
                return new CommandAck(false, $"a run is already under way: {current.Info.Summary}", null);
            }

            BotRunInfo info = new(Guid.NewGuid().ToString("N")[..12], assignment!.Kind, assignment.ToString(), clock.GetUtcNow());
            active = run = new ActiveRun(info, preparations.Length > 0 ? BotRunState.Syncing : BotRunState.Starting);
        }

        LogStarting(logger, run.Info.Summary, run.Info.RunId);
        new Thread(() => RunThread(run, assignment!)) { IsBackground = true, Name = $"Bot run {run.Info.RunId}" }.Start();
        return new CommandAck(true, null, run.Info.RunId);
    }

    /// <summary>
    /// Stops the run under way: its token is cancelled, so it releases input and ends by itself; a
    /// run that has not ended after the grace period is forced. Answers at once.
    /// </summary>
    public CommandAck Stop()
    {
        ActiveRun? run;
        lock (sync)
        {
            run = active;
            if (run is null)
            {
                return new CommandAck(true, "no run is under way", null);
            }

            if (run.StopRequested)
            {
                return new CommandAck(true, "the run is already stopping", run.Info.RunId);
            }

            run.StopRequested = true;
        }

        LogStopping(logger, run.Info.Summary);
        run.Stop.Cancel();
        _ = WatchStopAsync(run);
        return new CommandAck(true, null, run.Info.RunId);
    }

    /// <summary>What the bot shows now: the run's taps, or idle with how the last run ended.</summary>
    public BotTelemetry Read()
    {
        ActiveRun? run;
        RunReport? report;
        lock (sync)
        {
            run = active;
            report = last;
        }

        if (run is null)
        {
            return new BotTelemetry(BotRunState.Idle,
                Message: report is null ? null : $"Last run: {report.Run.Summary} ended {report.Outcome}: {report.Message}");
        }

        BotTelemetry telemetry;
        if (run.Runtime is { } runtime)
        {
            try
            {
                telemetry = runtime.Read();
            }
            catch (Exception ex)
            {
                telemetry = new BotTelemetry(BotRunState.Running, Message: $"the run's state could not be read: {ex.Message}");
            }
        }
        else
        {
            telemetry = new BotTelemetry(run.State, Message: run.Message);
        }

        if (run.StopRequested)
        {
            telemetry = telemetry with { State = BotRunState.Stopping };
        }

        run.LastTelemetry = telemetry;
        return telemetry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>The host stops: a run is stopped as a stop command would, and waited for.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveRun? run;
        lock (sync)
        {
            stopping = true;
            run = active;
        }

        if (run is null || run.Abandoned)
        {
            return;
        }

        Stop();
        try
        {
            await run.Ended.Task.WaitAsync(options.StopGrace + ReleaseWait + abandonWait, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Abandoned: WatchStopAsync reported it and asked for the restart.
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            active?.Stop.Dispose();
        }
    }

    private void RunThread(ActiveRun run, BotAssignment assignment)
    {
        BotRunResult result;
        IBotRuntime? runtime = null;
        try
        {
            if (Prepare(run, assignment) is { } refused)
            {
                result = refused;
            }
            else
            {
                run.State = BotRunState.Starting;
                if (!factory.TryCreate(assignment, out IBotRuntime created, out string refusal))
                {
                    result = new BotRunResult(BotRunOutcome.Refused, refusal);
                }
                else
                {
                    runtime = created;
                    run.Runtime = created;
                    result = run.Stop.IsCancellationRequested
                        ? new BotRunResult(BotRunOutcome.Stopped, "stopped before it started")
                        : created.Run(run.Stop.Token);
                }
            }
        }
        catch (Exception ex)
        {
            LogRunError(logger, ex, run.Info.Summary);
            result = new BotRunResult(BotRunOutcome.Failed, $"the run stopped on an error: {ex.Message}");
        }
        finally
        {
            if (runtime is not null)
            {
                // Its last state, read before it is torn down, then out of telemetry's reach: a runtime being
                // disposed reads as one attaching, and the report took that, losing its kills (known issue 76).
                run.Final = TryRead(runtime);
                run.State = BotRunState.Stopping;
                run.Runtime = null;
                try
                {
                    runtime.Dispose();
                }
                catch (Exception ex)
                {
                    LogRunError(logger, ex, run.Info.Summary);
                }
            }
        }

        Finish(run, result);
    }

    private BotTelemetry? TryRead(IBotRuntime runtime)
    {
        try
        {
            return runtime.Read();
        }
        catch (Exception ex)
        {
            LogRunError(logger, ex, "reading the run's last state");
            return null;
        }
    }

    // Null to go on; a result ends the run before anything attached.
    private BotRunResult? Prepare(ActiveRun run, BotAssignment assignment)
    {
        foreach (IRunPreparation preparation in preparations)
        {
            RunPreparation prepared;
            try
            {
                prepared = preparation.PrepareAsync(assignment, run.Stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
            {
                // A stop while pulling data is a stop like any other, not the run failing.
                return new BotRunResult(BotRunOutcome.Stopped, "stopped before it started");
            }

            if (prepared.Message is not null)
            {
                run.Message = prepared.Message;
                LogPrepared(logger, prepared.Message);
            }

            if (!prepared.Proceed)
            {
                return new BotRunResult(BotRunOutcome.Refused, prepared.Message ?? "the run's data is not ready");
            }
        }

        return run.Stop.IsCancellationRequested ? new BotRunResult(BotRunOutcome.Stopped, "stopped before it started") : null;
    }

    private void Finish(ActiveRun run, BotRunResult result)
    {
        // A stopped run reports a stop, however its runtime ended it.
        BotRunOutcome outcome = run.StopRequested && result.Outcome is BotRunOutcome.Failed or BotRunOutcome.Refused
            ? BotRunOutcome.Stopped
            : result.Outcome;
        RunReport report = new(run.Info, outcome, clock.GetUtcNow(), result.Message, result.ExitCode,
            (run.Final ?? run.LastTelemetry)?.Progress);
        bool abandoned;
        lock (sync)
        {
            if (active == run)
            {
                active = null;
            }

            // Exactly one of this and the abandon reports the run.
            run.Finished = true;
            abandoned = run.Abandoned;
            if (!abandoned)
            {
                last = report;
            }
        }

        // Raised before the run's end is signalled: a host that stops waits for that, then lets the link go,
        // and the report raised after it was lost (known issue 76).
        if (!abandoned)
        {
            LogEnded(logger, run.Info.Summary, outcome, result.Message);
            RunEnded?.Invoke(report);
        }

        run.Ended.TrySetResult();
    }

    private async Task WatchStopAsync(ActiveRun run)
    {
        if (await EndsWithin(run, options.StopGrace))
        {
            return;
        }

        LogForcing(logger, run.Info.Summary, options.StopGrace.TotalSeconds);
        if (run.Runtime is { } runtime)
        {
            try
            {
                await Task.Run(runtime.ForceRelease).WaitAsync(ReleaseWait);
            }
            catch (Exception ex)
            {
                LogRunError(logger, ex, run.Info.Summary);
            }
        }

        // After the forced release, half the grace again before the run is given up.
        if (await EndsWithin(run, abandonWait))
        {
            return;
        }

        RunReport report = new(run.Info, BotRunOutcome.Abandoned, clock.GetUtcNow(),
            $"it did not end within {options.StopGrace.TotalSeconds:0} s of the stop; its keys were released and the agent restarts",
            null, run.LastTelemetry?.Progress);
        lock (sync)
        {
            // Ended in the meantime: its own report went out, and nothing needs a restart.
            if (run.Finished)
            {
                return;
            }

            run.Abandoned = true;
            last = report;
        }

        LogEnded(logger, run.Info.Summary, BotRunOutcome.Abandoned, report.Message);
        RunEnded?.Invoke(report);
        process.Restart($"the run {run.Info.Summary} could not be torn down");
    }

    private static async Task<bool> EndsWithin(ActiveRun run, TimeSpan wait)
    {
        try
        {
            await run.Ended.Task.WaitAsync(wait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed class ActiveRun(BotRunInfo info, BotRunState state)
    {
        private volatile int runState = (int)state;
        private volatile bool stopRequested;
        private volatile bool abandoned;
        private volatile bool finished;
        private volatile BotTelemetry? final;
        private volatile IBotRuntime? runtime;
        private volatile string? message;
        private volatile BotTelemetry? lastTelemetry;

        public BotRunInfo Info { get; } = info;
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BotRunState State { get => (BotRunState)runState; set => runState = (int)value; }
        public bool StopRequested { get => stopRequested; set => stopRequested = value; }
        public bool Abandoned { get => abandoned; set => abandoned = value; }
        public bool Finished { get => finished; set => finished = value; }
        /// <summary>What the runtime showed as it returned, before it was disposed.</summary>
        public BotTelemetry? Final { get => final; set => final = value; }
        public IBotRuntime? Runtime { get => runtime; set => runtime = value; }
        public string? Message { get => message; set => message = value; }
        public BotTelemetry? LastTelemetry { get => lastTelemetry; set => lastTelemetry = value; }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting {Run} (run {RunId})")]
    static partial void LogStarting(ILogger logger, string run, string runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping {Run}: it releases input, then ends")]
    static partial void LogStopping(ILogger logger, string run);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Message}")]
    static partial void LogPrepared(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Run} did not end within {Seconds} s of the stop: releasing its keys from here")]
    static partial void LogForcing(ILogger logger, string run, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Run} ended {Outcome}: {Message}")]
    static partial void LogEnded(ILogger logger, string run, BotRunOutcome outcome, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Run} failed")]
    static partial void LogRunError(ILogger logger, Exception exception, string run);

    #endregion
}
