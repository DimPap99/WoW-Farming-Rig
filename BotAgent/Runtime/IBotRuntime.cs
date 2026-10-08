using Contracts.Bots;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Runtime;

/// <summary>
/// One run of the bot, built for an assignment by a <see cref="CreateRuntime"/>: run once, on a
/// thread of its own, until it ends or its token is cancelled. Cancelling is how a run is stopped,
/// and the run releases what it holds before it returns, as <c>Game/CLAUDE.md</c> requires: the
/// host never tears a run down under its feet, only after it returned or its grace period ran out.
/// </summary>
public interface IBotRuntime : IDisposable
{
    BotRuntimeKind Kind { get; }

    /// <summary>What the run does, for a person: the route or the profile.</summary>
    string Summary { get; }

    /// <summary>Runs until the run ends, fails or <paramref name="stop"/> is cancelled; releases input itself first.</summary>
    BotRunResult Run(CancellationToken stop);

    /// <summary>
    /// Releases every key the run holds, whatever it is doing, without waiting on it: for a host
    /// whose run did not end within its grace period. The run's own stop has released first.
    /// </summary>
    void ForceRelease();

    /// <summary>What the run shows now, through read-only taps; called from another thread about once a second.</summary>
    BotTelemetry Read();
}

/// <summary>How a run ended, as its runtime saw it.</summary>
/// <param name="ExitCode">The Forever run's exit code (<c>ForeverRunProtocol</c>), when it has one.</param>
/// <param name="Progress">What the run did, as it ended: the report's. Without it, the report has what a read last showed.</param>
public sealed record BotRunResult(BotRunOutcome Outcome, string Message, int? ExitCode = null, BotProgress? Progress = null);

/// <summary>What a runtime's taps read; everything but the state is optional.</summary>
public sealed record BotTelemetry(BotRunState State, BotCharacter? Character = null, BotVitals? Vitals = null,
    BotPosition? Position = null, string? Activity = null, string? Agenda = null, BotProgress? Progress = null,
    string? Message = null);

/// <summary>
/// The runtime an assignment needs (Classic or Forever), or why there is none: no client, the wrong
/// client, a file missing. Nothing has attached to the game when it refuses.
/// </summary>
public delegate (IBotRuntime? Runtime, string? Refusal) CreateRuntime(BotAssignment assignment);

/// <summary>The WoW client running on this PC now, or null.</summary>
public delegate AgentGame? ProbeGame();

/// <summary>The class profiles on this PC, for the manager to offer Classic assignments from: names relative to the class folder, <c>/</c> between subfolders.</summary>
public delegate string[] ListClassProfiles();

/// <summary>Something done before a run starts, such as pulling the data it needs: false with why refuses the run before anything attaches.</summary>
public delegate Task<RunPreparation> PrepareRun(BotAssignment assignment, CancellationToken token);

/// <summary>What a preparation found: whether the run may go on, and what a person should know.</summary>
public sealed record RunPreparation(bool Proceed, string? Message);

/// <summary>Ends the process with the code that asks its launcher to start it again: a run that cannot be torn down leaves it only that way out.</summary>
public delegate void RestartProcess(string reason);
