using BotAgent.Link;
using BotAgent.Runtime;

using Contracts.Bots;

using Microsoft.Extensions.Hosting;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Telemetry;

/// <summary>
/// Once a second, the bot's state as a <see cref="BotSnapshot"/>, read through the run's read-only
/// taps (or the client probe while idle), and the log lines since the last batch, to the manager.
/// While the manager is away nothing is sent and the log waits in its sink; the file log is
/// always written.
/// </summary>
public sealed class TelemetryPublisher(AgentIdentity identity, BotRuntimeHost host, AgentLink link, IGameProbe probe,
    AgentLogSink sink, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How often the client is looked for: listing processes is not free.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>The most log lines one batch carries.</summary>
    public const int BatchLines = 500;

    private AgentGame? game;
    private DateTimeOffset probedAt = DateTimeOffset.MinValue;
    private long sentLine;

    /// <summary>The last snapshot taken, sent or not.</summary>
    public BotSnapshot? Latest { get; private set; }

    /// <summary>Takes a snapshot now; what <see cref="ExecuteAsync"/> sends each second.</summary>
    public BotSnapshot Take()
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (now - probedAt >= ProbeInterval)
        {
            game = probe.Probe();
            probedAt = now;
        }

        BotTelemetry telemetry = host.Read();
        BotSnapshot snapshot = new(identity.AgentId, now, telemetry.State, host.CurrentRun, game, telemetry.Character,
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
                BotSnapshot snapshot = Take();
                if (!link.Connected)
                {
                    continue;
                }

                await link.SendSnapshotAsync(snapshot, stopping);
                BotLogLine[] lines = sink.ReadAfter(sentLine, BatchLines, out long last);
                if (lines.Length > 0 && await link.SendLogAsync(lines, stopping))
                {
                    sentLine = last;
                }
            }
            while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
