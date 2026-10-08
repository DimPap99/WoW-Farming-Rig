using BotAgent.Runtime;

using Contracts.Bots;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace ServiceTests.Agent;

/// <summary>
/// A run that plays nothing and records what happens to it: it runs until stopped, then "releases"
/// before it returns, as a real runtime must. One that ignores its stop stands for a run stuck in
/// the game, until the test lets it go.
/// </summary>
internal sealed class FakeRuntime(BotAssignment assignment, bool ignoreStop, TimeSpan disposeTakes = default) : IBotRuntime
{
    public const string Ran = "run";
    public const string Released = "released";
    public const string Forced = "forced";
    public const string Disposed = "disposed";

    private readonly ManualResetEventSlim stuck = new(false);
    private volatile bool disposed;

    public ConcurrentQueue<string> Calls { get; } = new();

    public int Kills { get; set; } = 3;

    public BotRuntimeKind Kind => assignment.Kind;

    public string Summary => assignment.ToString();

    public BotRunResult Run(CancellationToken stop)
    {
        Calls.Enqueue(Ran);
        if (ignoreStop)
        {
            stuck.Wait();
            return new BotRunResult(BotRunOutcome.Failed, "let go");
        }

        stop.WaitHandle.WaitOne();
        Calls.Enqueue(Released);
        // A runtime that saw its stop may report a failure; the host still reports a stop.
        return new BotRunResult(BotRunOutcome.Failed, "stopped by its token", 3);
    }

    public void ForceRelease()
    {
        Calls.Enqueue(Forced);
    }

    public BotTelemetry Read()
    {
        // As the Classic runtime reads once torn down: attaching, with nothing done.
        if (disposed)
        {
            return new BotTelemetry(BotRunState.Starting, Message: "attaching to the client");
        }

        return new BotTelemetry(BotRunState.Running, new BotCharacter("Hunter", 12, "Orc"), new BotVitals(90, 50, null, false),
            new BotPosition(1413, 51.9f, 29.9f), "Task/Travel", "task 1 of 1", new BotProgress(Kills, 0, Kills));
    }

    /// <summary>Lets a run that ignored its stop end.</summary>
    public void LetGo()
    {
        stuck.Set();
    }

    public void Dispose()
    {
        Calls.Enqueue(Disposed);
        disposed = true;
        Thread.Sleep(disposeTakes);
    }
}

/// <summary>
/// Builds <see cref="FakeRuntime"/>s (its <see cref="Create"/> is the agent's <see cref="CreateRuntime"/>) and keeps
/// them for the test; refuses with <see cref="Refusal"/> when set.
/// </summary>
internal sealed class FakeRuntimeFactory
{
    public ConcurrentQueue<FakeRuntime> Built { get; } = new();

    public bool IgnoreStop { get; set; }

    /// <summary>How long a runtime's dispose takes, as a Classic run's container does.</summary>
    public TimeSpan DisposeTakes { get; set; }

    public string? Refusal { get; set; }

    public FakeRuntime Last => Built.Last();

    public (IBotRuntime? Runtime, string? Refusal) Create(BotAssignment assignment)
    {
        if (Refusal is { } why)
        {
            return (null, why);
        }

        FakeRuntime created = new(assignment, IgnoreStop, DisposeTakes);
        Built.Enqueue(created);
        return (created, null);
    }
}

/// <summary>Keeps the restarts the agent asked for (its <see cref="Restart"/> is the agent's <see cref="RestartProcess"/>).</summary>
internal sealed class FakeProcess
{
    public ConcurrentQueue<string> Restarts { get; } = new();

    public void Restart(string reason)
    {
        Restarts.Enqueue(reason);
    }
}
