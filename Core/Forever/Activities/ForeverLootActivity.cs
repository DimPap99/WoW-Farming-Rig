using Microsoft.Extensions.Logging;

using System;

namespace Core.Forever;

public enum ForeverLootState { Idle, Round, Failed }

/// <summary>
/// Loots the bodies of the fights once nothing fights the player: a round
/// that goes from body to body (<see cref="IForeverLootRoutine"/>) until none
/// is left. Danger ends the activity; the bodies wait for the next round.
/// </summary>
public sealed class ForeverLootActivity : IForeverActivity
{
    /// <summary>
    /// Before Threat, so the bodies are looted while combat only lingers;
    /// in danger this does not want to run and Threat goes first.
    /// </summary>
    public const float Priority = 4.5f;

    private const int RoundMs = 120000;

    private readonly ILogger<ForeverLootActivity> logger;
    private readonly IForeverLootRoutine loot;
    private readonly ForeverCorpses corpses;
    private readonly ForeverNavigator navigator;
    private readonly ClassConfiguration config;
    private readonly ForeverStateMachine<ForeverLootState> machine;
    private ForeverFacts facts;
    private volatile int collected;

    public ForeverLootActivity(ILogger<ForeverLootActivity> logger, IForeverLootRoutine loot,
        ForeverCorpses corpses, ForeverNavigator navigator, ClassConfiguration config)
    {
        this.logger = logger;
        this.loot = loot;
        this.corpses = corpses;
        this.navigator = navigator;
        this.config = config;
        machine = new ForeverStateMachine<ForeverLootState>(logger, "Forever loot",
                ForeverLootState.Idle, ForeverLootState.Failed)
            .On(ForeverLootState.Idle, exits: [ForeverLootState.Round])
            .On(ForeverLootState.Round, Round, exits: [ForeverLootState.Idle],
                timeoutMs: RoundMs, onTimeout: "the loot round did not finish",
                timedOut: TimedOut, exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop)
            .On(ForeverLootState.Failed, enter: navigator.StopPursuit);
    }

    public string Name => "Loot";
    public float Cost => Priority;
    public ForeverAllowed Allowed => machine.Allowed;
    public string Status => Name;
    public ForeverLootState State => machine.State;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverLootState state) => machine.AllowedIn(state);

    /// <summary>Bodies looted in this run.</summary>
    public int Collected => collected;

    /// <summary>A round ended, done or out of time; the run rests before the next pull.</summary>
    public event Action? RoundEnded;

    // A round under way also wants the frame after its last body, when it
    // ends itself and the rest that follows a round is due.
    public bool Wants(in ForeverFacts facts) =>
        config.Loot && !facts.InDanger &&
        (facts.BodiesPending > 0 || machine.State == ForeverLootState.Round);

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (machine.State != ForeverLootState.Idle)
            return;
        if (bars.Loot is null)
        {
            machine.Fail("a kill to loot, but loot event telemetry is unavailable");
            return;
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever loot round: {Count} bodies where they fell", corpses.Pending.Count);
        loot.Begin();
        machine.Go(ForeverLootState.Round);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts)
    {
        this.facts = facts;
        machine.Tick(bars);
        return machine.State == ForeverLootState.Failed
            ? ForeverActivityResult.Failed(machine.LastFailure)
            : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        navigator.StopPursuit();
        if (machine.State == ForeverLootState.Round)
            machine.Go(ForeverLootState.Idle);
    }

    // The round goes from body to body until none is left; an attack ends
    // the activity on the next frame, and the bodies wait for the next round.
    private void Round(VisualBarSample bars)
    {
        switch (loot.Update(bars, facts.InDanger, out string reason))
        {
            case ForeverLootResult.Collected:
                collected++;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever looted a body; {Count} left", corpses.Pending.Count);
                break;
            case ForeverLootResult.Done:
                machine.Go(ForeverLootState.Idle);
                RoundEnded?.Invoke();
                break;
            case ForeverLootResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    private void TimedOut(string reason)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever {Reason}; leaving {Count} bodies", reason, corpses.Pending.Count);
        corpses.Clear();
        machine.Go(ForeverLootState.Idle);
        RoundEnded?.Invoke();
    }
}
