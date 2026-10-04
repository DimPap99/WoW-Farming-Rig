using Microsoft.Extensions.Logging;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverHuntHereState { Idle, Search, Failed }

/// <summary>
/// Carries out <see cref="ForeverHuntHereTask"/>. The nameplates show only
/// what the camera sees, so finding a unit means looking around: take one in
/// view, else turn a full circle standing, then walk on a stretch and look
/// again. A unit taken goes to <see cref="ForeverEngageActivity"/>.
/// </summary>
public sealed class ForeverHuntHere : IForeverTaskRunner
{
    private const int SearchMs = 45000;
    // Looking around is a calm scan: this share of full steering speed.
    private const double LookAroundRate = 0.25;
    // After a full look-around with nothing to take, walk on this long first.
    private const int RoamMs = 8000;

    private readonly ForeverHuntHereTask task;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly ForeverStateMachine<ForeverHuntHereState> machine;
    private readonly ForeverLookAround lookAround = new();
    private long roamSince;
    private bool nothingInView;

    public ForeverHuntHere(ILogger logger, ForeverHuntHereTask task, ForeverEngageActivity engage,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config)
    {
        this.task = task;
        this.engage = engage;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        // Walking hops when snagged, and now and then for no reason.
        machine = new ForeverStateMachine<ForeverHuntHereState>(logger, "Forever hunt here",
                ForeverHuntHereState.Idle, ForeverHuntHereState.Failed)
            .On(ForeverHuntHereState.Idle, exits: [ForeverHuntHereState.Search])
            .On(ForeverHuntHereState.Search, Search, exits: [ForeverHuntHereState.Idle],
                timeoutMs: SearchMs, onTimeout: "no safe target found within acquisition time",
                enter: Restart, exit: navigator.StopPursuit,
                allows: ForeverAllowed.SnagHop | ForeverAllowed.IdleHops)
            .On(ForeverHuntHereState.Failed, enter: navigator.StopPursuit);
    }

    public string Status => task.Name;
    public ForeverAllowed Allowed => machine.Allowed;
    public ForeverHuntHereState State => machine.State;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverHuntHereState state) => machine.AllowedIn(state);

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (machine.State == ForeverHuntHereState.Idle)
            machine.Go(ForeverHuntHereState.Search);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress progress)
    {
        nothingInView = false;
        machine.Tick(bars);
        if (machine.State == ForeverHuntHereState.Failed)
            return ForeverActivityResult.Failed(machine.LastFailure);
        return nothingInView ? ForeverActivityResult.Done : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        navigator.StopPursuit();
        if (machine.State == ForeverHuntHereState.Search)
            machine.Go(ForeverHuntHereState.Idle);
    }

    private void Search(VisualBarSample bars)
    {
        // Before the next pull: an attack the client cannot use, such as a
        // shot without ammo, stops the run with why.
        switch (ForeverPulling.TryPull(bars, combat, config, engage, out string unusable))
        {
            case ForeverPull.Unusable:
                machine.Fail(unusable);
                return;
            case ForeverPull.Offered:
                return;
        }
        if (!lookAround.Turned(bars))
        {
            if (navigator.TurnInPlace(bars, LookAroundRate, out string turnReason) ==
                    ForeverNavigationResult.Failed)
                machine.Fail(turnReason);
            return;
        }
        // A full look-around found nothing to take: done, when the task asks for that.
        if (task.Until.NothingInView)
        {
            navigator.StopPursuit();
            nothingInView = true;
            return;
        }
        if (roamSince == 0)
            roamSince = GetTimestamp();
        if (GetElapsedTime(roamSince).TotalMilliseconds > RoamMs)
        {
            navigator.StopPursuit();
            Restart();
            return;
        }
        switch (navigator.Roam(bars, out string roamReason))
        {
            case ForeverNavigationResult.Stuck or ForeverNavigationResult.Blocked:
                // Look around again; the walk goes on in a direction that is free.
                navigator.StopPursuit();
                Restart();
                break;
            case ForeverNavigationResult.Failed:
                machine.Fail(roamReason);
                break;
        }
    }

    // A fresh look-around, and the next walk starts its stretch anew.
    private void Restart()
    {
        lookAround.Reset();
        roamSince = 0;
    }
}
