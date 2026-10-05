using Contracts.Bots;

using System;
using System.Threading;

namespace BotAgent.Runtime;

/// <summary>
/// One run of the bot, built for an assignment by an <see cref="IBotRuntimeFactory"/>: run once, on a
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
public sealed record BotRunResult(BotRunOutcome Outcome, string Message, int? ExitCode = null);

/// <summary>What a runtime's taps read; everything but the state is optional.</summary>
public sealed record BotTelemetry(BotRunState State, BotCharacter? Character = null, BotVitals? Vitals = null,
    BotPosition? Position = null, string? Activity = null, string? Agenda = null, BotProgress? Progress = null,
    string? Message = null);

/// <summary>Builds the runtime an assignment needs: Classic or Forever.</summary>
public interface IBotRuntimeFactory
{
    /// <summary>
    /// A runtime for <paramref name="assignment"/>, or why there is none (no client, the wrong client,
    /// a file missing). Nothing has attached to the game when it refuses.
    /// </summary>
    bool TryCreate(BotAssignment assignment, out IBotRuntime runtime, out string refusal);
}

/// <summary>What WoW client runs on this PC.</summary>
public interface IGameProbe
{
    /// <summary>The client running now, or null.</summary>
    AgentGame? Probe();
}

/// <summary>The class profiles on this PC, for the manager to offer Classic assignments from.</summary>
public interface IClassProfileCatalog
{
    /// <summary>Profile names relative to the class folder, <c>/</c> between subfolders.</summary>
    string[] List();
}

/// <summary>Something done before a run starts, such as pulling the data it needs.</summary>
public interface IRunPreparation
{
    /// <summary>Prepares for <paramref name="assignment"/>: false with why refuses the run before anything attaches.</summary>
    System.Threading.Tasks.Task<RunPreparation> PrepareAsync(BotAssignment assignment, CancellationToken token);
}

/// <summary>What a preparation found: whether the run may go on, and what a person should know.</summary>
public sealed record RunPreparation(bool Proceed, string? Message);

/// <summary>The process itself: a run that cannot be torn down leaves it only one way out.</summary>
public interface IAgentProcess
{
    /// <summary>Ends the process with the code that asks its launcher to start it again.</summary>
    void Restart(string reason);
}
