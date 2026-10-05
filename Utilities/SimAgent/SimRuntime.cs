using BotAgent.Runtime;

using Contracts.Bots;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using System;
using System.Threading;

namespace SimAgent;

/// <summary>What this agent pretends to be: <c>Sim:Client</c> (forever or a Classic client) and <c>Sim:ClickSeconds</c>.</summary>
public sealed record SimOptions(string Client, int ClickSeconds)
{
    public const string ForeverClient = "forever";

    public static SimOptions From(IConfiguration configuration)
    {
        return new SimOptions(configuration["Sim:Client"] is { Length: > 0 } client ? client : ForeverClient,
            configuration.GetValue("Sim:ClickSeconds", 5));
    }

    public bool Forever => Client == ForeverClient;
}

/// <summary>Builds a <see cref="SimRuntime"/>, refusing a run of the other kind as the real agent does.</summary>
public sealed class SimRuntimeFactory(SimOptions options, ILoggerFactory loggers) : IBotRuntimeFactory
{
    public bool TryCreate(BotAssignment assignment, out IBotRuntime runtime, out string refusal)
    {
        if ((assignment.Kind == BotRuntimeKind.Forever) != options.Forever)
        {
            runtime = null!;
            refusal = $"a {assignment.Kind} run needs a {assignment.Kind} client, and this PC runs {options.Client} (simulated)";
            return false;
        }

        runtime = new SimRuntime(assignment, options, loggers.CreateLogger<SimRuntime>());
        refusal = string.Empty;
        return true;
    }
}

/// <summary>
/// A run that plays nothing: it waits for a "click", then walks a circle in Durotar, fights, loots and
/// rests by the clock, a log line a second, until it is stopped or its time bound is up.
/// </summary>
public sealed partial class SimRuntime(BotAssignment assignment, SimOptions options, ILogger<SimRuntime> logger) : IBotRuntime
{
    private static readonly string[] Activities = ["Task/Travel", "Engage/Pull", "Engage/Fight", "Loot", "Rest"];

    private volatile BotTelemetry telemetry = new(BotRunState.Starting);

    public BotRuntimeKind Kind => assignment.Kind;

    public string Summary => assignment.ToString();

    public BotRunResult Run(CancellationToken stop)
    {
        DateTimeOffset bound = DateTimeOffset.UtcNow.AddMinutes(assignment.Forever?.Minutes ?? 60);
        LogClick(logger, options.ClickSeconds);
        telemetry = new BotTelemetry(BotRunState.WaitingForClick, Message: "Click into the game window within 60 seconds");
        if (stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(options.ClickSeconds)))
        {
            return Released("stopped while it waited for the click");
        }

        int kills = 0;
        int deaths = 0;
        int loots = 0;
        for (int tick = 1; DateTimeOffset.UtcNow < bound; tick++)
        {
            string activity = Activities[tick / 4 % Activities.Length];
            kills += activity == "Loot" && tick % 4 == 0 ? 1 : 0;
            loots += activity == "Rest" && tick % 4 == 0 ? 1 : 0;
            double angle = tick * 0.05;
            BotPosition position = new(1411, (float)(52 + 4 * Math.Cos(angle)), (float)(44 + 4 * Math.Sin(angle)));
            BotVitals vitals = new((float)(70 + 30 * Math.Sin(tick * 0.2)), (float)(50 + 50 * Math.Cos(tick * 0.13)),
                activity.StartsWith("Engage", StringComparison.Ordinal) ? Math.Max(0, 100 - tick % 8 * 14) : null,
                activity.StartsWith("Engage", StringComparison.Ordinal));
            telemetry = new BotTelemetry(BotRunState.Running, new BotCharacter("Hunter", 12, "Orc"), vitals, position, activity,
                assignment.Kind == BotRuntimeKind.Forever ? $"task {1 + tick / 30 % 2} of 2: {assignment.Forever!.Route}" : null,
                new BotProgress(kills, deaths, loots));

            if (tick % 15 == 0)
            {
                LogSlow(logger, tick);
            }
            else
            {
                LogTick(logger, tick, activity, position.MapX, position.MapY);
            }

            if (stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
            {
                return Released("stopped");
            }
        }

        return new BotRunResult(BotRunOutcome.Complete, "the time bound was reached", 0);
    }

    public void ForceRelease()
    {
        LogForced(logger);
    }

    public BotTelemetry Read()
    {
        return telemetry;
    }

    public void Dispose()
    {
    }

    private BotRunResult Released(string why)
    {
        LogReleased(logger);
        telemetry = new BotTelemetry(BotRunState.Stopping);
        return new BotRunResult(BotRunOutcome.Stopped, why, 3);
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Click into the game window within 60 seconds (simulated: {Seconds} s)")]
    static partial void LogClick(ILogger logger, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tick {Tick}: {Activity} at {X:F1}, {Y:F1}")]
    static partial void LogTick(ILogger logger, int tick, string activity, float x, float y);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tick {Tick}: the step took longer than its budget")]
    static partial void LogSlow(ILogger logger, int tick);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released the keys and the mouse")]
    static partial void LogReleased(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forced release (simulated)")]
    static partial void LogForced(ILogger logger);

    #endregion
}
