using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// What a state of the automaton allows besides its own work, declared with
/// the state, so a shared routine such as walking does only what the state
/// it runs in allows.
/// </summary>
[Flags]
public enum ForeverAllowed
{
    None = 0,
    /// <summary>A hop when held movement stalls, snagged on something the mesh does not know.</summary>
    SnagHop = 1,
    /// <summary>Now and then one to three hops in a row while walking, as a person does for no reason.</summary>
    IdleHops = 2,
}

/// <summary>
/// The explicit automaton behind a Forever goal. Each state is declared once
/// with <see cref="On"/>: what it does with a frame, the states it may move
/// to, its hard time bound, the hooks that release what it holds, and what it
/// allows besides its work. A move the declaration does not allow stops the
/// machine rather than act on a bug. The goal ticks it on the controller
/// thread; <see cref="State"/> and <see cref="Fail"/> are safe from any thread.
/// </summary>
public sealed class ForeverStateMachine<TState> where TState : struct, Enum
{
    private sealed record Definition(Action<VisualBarSample>? Update, TState[] Exits,
        int TimeoutMs, string? TimeoutReason, Action<string>? TimedOut, Action? Enter, Action? Exit,
        ForeverAllowed Allows);

    private sealed record Current(TState State, long EnteredAt);

    private readonly ILogger logger;
    private readonly string name;
    private readonly TState failed;
    private readonly HashSet<TState> terminal;
    private readonly Dictionary<TState, Definition> states = [];

    // Orders a move on the controller thread with a Fail from another thread.
    // Hooks run inside it, so a hook only releases input and never moves the
    // machine or waits for the controller thread.
    private readonly object sync = new();
    private volatile Current current;
    private volatile string lastFailure = string.Empty;

    /// <param name="name">Names the machine in its log lines.</param>
    /// <param name="terminal">States besides <paramref name="failed"/> that end the run.</param>
    public ForeverStateMachine(ILogger logger, string name, TState initial,
        TState failed, params TState[] terminal)
    {
        this.logger = logger;
        this.name = name;
        this.failed = failed;
        this.terminal = [failed, .. terminal];
        current = new(initial, GetTimestamp());
    }

    public TState State => current.State;

    /// <summary>Milliseconds since the current state was entered.</summary>
    public double ElapsedMs => GetElapsedTime(current.EnteredAt).TotalMilliseconds;

    public bool IsTerminal => terminal.Contains(current.State);

    /// <summary>Why the machine failed, or empty.</summary>
    public string LastFailure => lastFailure;

    /// <summary>What the current state allows besides its work.</summary>
    public ForeverAllowed Allowed => AllowedIn(current.State);

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(TState state) =>
        states.TryGetValue(state, out Definition? definition) ? definition.Allows : ForeverAllowed.None;

    /// <summary>Declares <paramref name="state"/>; a terminal state needs only hooks.</summary>
    /// <param name="update">Its work on one frame; it moves with <see cref="Go"/> or <see cref="Fail"/>.</param>
    /// <param name="exits">The states it may move to. Failing is always allowed.</param>
    /// <param name="timeoutMs">Its hard bound, checked before its work on each frame; 0 for none.</param>
    /// <param name="onTimeout">The failure reason once the bound passes.</param>
    /// <param name="timedOut">
    /// Gets the reason once the bound passes and may move to a declared exit
    /// instead of failing; if it does not move, the machine fails.
    /// </param>
    /// <param name="enter">Runs on every entry.</param>
    /// <param name="exit">Runs on every exit, failing included.</param>
    /// <param name="allows">What it allows besides its work; nothing by default.</param>
    public ForeverStateMachine<TState> On(TState state, Action<VisualBarSample>? update = null,
        TState[]? exits = null, int timeoutMs = 0, string? onTimeout = null,
        Action<string>? timedOut = null, Action? enter = null, Action? exit = null,
        ForeverAllowed allows = ForeverAllowed.None)
    {
        if (timeoutMs > 0 && string.IsNullOrEmpty(onTimeout))
            throw new ArgumentException($"{state} has a time bound without a reason", nameof(onTimeout));
        states.Add(state, new(update, exits ?? [], timeoutMs, onTimeout, timedOut, enter, exit, allows));
        return this;
    }

    /// <summary>Fails a state past its bound, otherwise runs its work on <paramref name="bars"/>.</summary>
    public void Tick(VisualBarSample bars)
    {
        Current now = current;
        if (terminal.Contains(now.State) ||
            !states.TryGetValue(now.State, out Definition? definition) ||
            definition.Update is null)
            return;
        if (definition.TimeoutMs > 0 &&
            GetElapsedTime(now.EnteredAt).TotalMilliseconds > definition.TimeoutMs)
        {
            definition.TimedOut?.Invoke(definition.TimeoutReason!);
            if (ReferenceEquals(current, now))
                Fail(definition.TimeoutReason!);
            return;
        }
        definition.Update(bars);
    }

    /// <summary>Moves to <paramref name="next"/>, which the current state must declare as an exit.</summary>
    public void Go(TState next)
    {
        lock (sync)
        {
            TState from = current.State;
            if (terminal.Contains(from))
                return;
            if (!states.TryGetValue(from, out Definition? definition) ||
                Array.IndexOf(definition.Exits, next) < 0)
            {
                FailLocked($"{from} may not move to {next}");
                return;
            }
            Move(definition, next);
        }
    }

    /// <summary>Ends the run with <paramref name="reason"/>; a terminal state stays as it is.</summary>
    public void Fail(string reason)
    {
        lock (sync)
            FailLocked(reason);
    }

    private void FailLocked(string reason)
    {
        TState from = current.State;
        if (terminal.Contains(from))
            return;
        lastFailure = reason;
        Move(states.GetValueOrDefault(from), failed);
        logger.LogWarning("{Machine} stopped in {State}: {Reason}", name, from, reason);
    }

    private void Move(Definition? from, TState next)
    {
        from?.Exit?.Invoke();
        TState previous = current.State;
        current = new(next, GetTimestamp());
        if (states.TryGetValue(next, out Definition? to))
            to.Enter?.Invoke();
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("{Machine}: {From} -> {To}", name, previous, next);
    }
}
