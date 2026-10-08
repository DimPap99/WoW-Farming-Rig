using BotAgent.Link;
using BotAgent.Runtime;

using Contracts.Bots;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Telemetry;

/// <summary>
/// Once a second, the bot's state as a <see cref="BotSnapshot"/>, read through the run's read-only
/// taps (or the client probe while idle), and the log lines since the last batch, to the manager.
/// While the manager is away nothing is sent and the log waits in its sink; the file log is
/// always written. A tick that fails is logged and the next one runs: on .NET 10 a background
/// service that faults stops the host, and with it the run (known issue 67).
/// </summary>
public sealed partial class TelemetryPublisher(AgentIdentity identity, BotRuntimeHost host, AgentLink link, IGameProbe probe,
    AgentLogSink sink, TimeProvider clock, ILogger<TelemetryPublisher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How often the client is looked for: listing processes is not free.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>The most log lines one batch carries.</summary>
    public const int BatchLines = 500;

    private AgentGame? game;
    private long probedAt;
    private bool probed;
    private string? probeFailure;
    private string? tickFailure;
    private long sentLine;

    /// <summary>The last snapshot taken, sent or not.</summary>
    public BotSnapshot? Latest { get; private set; }

    /// <summary>Takes a snapshot now; what <see cref="ExecuteAsync"/> sends each second.</summary>
    public BotSnapshot Take()
    {
        // Timed on the monotonic clock: a wall clock set back would hold the next probe that long.
        if (!probed || clock.GetElapsedTime(probedAt) >= ProbeInterval)
        {
            game = Probe();
            probedAt = clock.GetTimestamp();
            probed = true;
        }

        BotTelemetry telemetry = host.Read();
        BotSnapshot snapshot = new(identity.AgentId, clock.GetUtcNow(), telemetry.State, host.CurrentRun, game, telemetry.Character,
            telemetry.Vitals, telemetry.Position, telemetry.Activity, telemetry.Agenda, telemetry.Progress, telemetry.Message);
        Latest = snapshot;
        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using PeriodicTimer timer = new(Interval, clock);
        try
        {
            do
            {
                try
                {
                    await PublishAsync(stopping);
                    tickFailure = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
                {
                    if (Changed(ref tickFailure, ex))
                    {
                        LogTickFailed(logger, ex);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private async Task PublishAsync(CancellationToken stopping)
    {
        BotSnapshot snapshot = Take();
        if (!link.Connected)
        {
            return;
        }

        await link.SendSnapshotAsync(snapshot, stopping);
        BotLogLine[] lines = sink.ReadAfter(sentLine, BatchLines, out long last);
        if (lines.Length > 0 && await link.SendLogAsync(lines, stopping))
        {
            sentLine = last;
        }
    }

    // The client, or none when the probe cannot tell: a probe that throws is no client, never a stop.
    private AgentGame? Probe()
    {
        try
        {
            AgentGame? found = probe.Probe();
            probeFailure = null;
            return found;
        }
        catch (Exception ex)
        {
            if (Changed(ref probeFailure, ex))
            {
                LogProbeFailed(logger, ex);
            }

            return null;
        }
    }

    // Whether this failure differs from the last one: a failure that lasts is logged once.
    private static bool Changed(ref string? last, Exception exception)
    {
        if (exception.Message == last)
        {
            return false;
        }

        last = exception.Message;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The bot's state could not be read or sent; the agent goes on and tries again")]
    static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The WoW client could not be looked for; the agent sees none until it can")]
    static partial void LogProbeFailed(ILogger logger, Exception exception);
}
