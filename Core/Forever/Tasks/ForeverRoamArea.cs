using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Numerics;

namespace Core.Forever;

public enum ForeverRoamState { Idle, Walk, Look, Failed }

/// <summary>
/// Carries out <see cref="ForeverRoamAreaTask"/>: from outside, walks into
/// the area to the nearest reachable spot; inside, looks round, walks to the
/// next spot (<see cref="ForeverSpotPicker"/>) and looks round again, until
/// the task's condition holds. Units in view are taken on the walk and while
/// looking, unless the task says otherwise, and fought by
/// <see cref="ForeverEngageActivity"/>. The roam keeps its place meanwhile:
/// afterwards it walks on toward the same spot, back into the area when a
/// fight led out of it.
/// </summary>
public sealed class ForeverRoamArea : IForeverTaskRunner
{
    /// <summary>A spot counts as reached within this many yards.</summary>
    public const float SpotYards = 4;
    // Looking around is a calm scan: this share of full steering speed.
    private const double LookAroundRate = 0.25;
    // This many spots in a row given up, or picks that found none, stop the roam.
    private const int MaxSkipsInARow = 4;
    private const int MaxEmptyPicks = 3;
    // The start-up check looks for walkable ground at the centre and at this
    // many points in all, drawn from a fixed seed so the check repeats.
    private const int GroundProbes = 16;
    private const int GroundSeed = 1;

    private readonly ILogger logger;
    private readonly ForeverRoamAreaTask task;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly WorldMapAreaDB maps;
    private readonly ForeverWalker walker;
    private readonly ForeverStateMachine<ForeverRoamState> machine;
    private readonly ForeverLookAround lookAround = new();

    private ForeverWorldArea? area;
    private ForeverSpotPicker? picker;
    private int skipsInARow;
    private int emptyPicks;
    // The frame the mesh queries of a pick read the player's position from.
    private VisualBarSample frame;

    public ForeverRoamArea(ILogger logger, ForeverRoamAreaTask task, ForeverEngageActivity engage,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config, WorldMapAreaDB maps)
    {
        this.logger = logger;
        this.task = task;
        this.engage = engage;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        this.maps = maps;
        walker = new(logger, navigator);
        // Walking allows what the walk allows; looking round, nothing.
        machine = new ForeverStateMachine<ForeverRoamState>(logger, "Forever roam",
                ForeverRoamState.Idle, ForeverRoamState.Failed)
            .On(ForeverRoamState.Idle, exits: [ForeverRoamState.Walk, ForeverRoamState.Look])
            .On(ForeverRoamState.Walk, Walk, exits: [ForeverRoamState.Look],
                exit: navigator.StopPursuit)
            .On(ForeverRoamState.Look, Look, exits: [ForeverRoamState.Walk],
                enter: lookAround.Reset, exit: navigator.StopPursuit)
            .On(ForeverRoamState.Failed, enter: navigator.StopPursuit);
    }

    public string Status => machine.State == ForeverRoamState.Walk && walker.State != ForeverWalkState.Walk
        ? $"{task.Name}/{walker.State}"
        : $"{task.Name}/{machine.State}";

    public ForeverAllowed Allowed => machine.State == ForeverRoamState.Walk ? walker.Allowed : machine.Allowed;
    public ForeverRoamState State => machine.State;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (machine.State == ForeverRoamState.Failed)
            return;
        if (area is null && !TryResolve(bars, out string reason))
        {
            machine.Fail(reason);
            return;
        }
        switch (machine.State)
        {
            // Inside, the roam begins by looking round; outside, it walks in first.
            case ForeverRoamState.Idle when area!.Contains(Here(bars)):
                machine.Go(ForeverRoamState.Look);
                break;
            case ForeverRoamState.Idle:
                SetOff(bars, approach: true);
                break;
            // After a fight or a rest: on toward the same spot, or a fresh circle.
            case ForeverRoamState.Walk:
                walker.Resume();
                break;
            case ForeverRoamState.Look:
                lookAround.Reset();
                break;
        }
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress done)
    {
        machine.Tick(bars);
        return machine.State == ForeverRoamState.Failed
            ? ForeverActivityResult.Failed(machine.LastFailure)
            : ForeverActivityResult.Running;
    }

    // The roam keeps its place for when it resumes; only held input is released.
    public void Exit()
    {
        walker.Stop();
        navigator.StopPursuit();
    }

    /// <summary>
    /// Whether <paramref name="task"/> can begin: its area on a verified map of
    /// the player's continent, with walkable ground inside. The run's start-up
    /// check asks this before anything moves.
    /// </summary>
    public static bool TryCheck(ForeverRoamAreaTask task, VisualBarSample bars, ForeverNavigator navigator,
        WorldMapAreaDB maps, out string reason)
    {
        if (!TryResolveArea(task, bars, maps, out ForeverWorldArea area, out reason))
            return false;
        Random draws = new(GroundSeed);
        for (int i = 0; i < GroundProbes; i++)
        {
            Vector2 probe = i == 0 ? area.Centre : area.RandomPoint(draws);
            if (navigator.TrySnapWalkable(bars, probe, out Vector2 ground) && area.Contains(ground))
                return true;
        }
        reason = $"no walkable ground was found inside the {task.Area}";
        return false;
    }

    // The area in world yards, on the player's continent.
    private static bool TryResolveArea(ForeverRoamAreaTask task, VisualBarSample bars, WorldMapAreaDB maps,
        out ForeverWorldArea area, out string reason)
    {
        area = null!;
        if (bars.WorldPosition is not { } world)
        {
            reason = "the player's world position is unreadable";
            return false;
        }
        if (!task.Area.TryToWorld(maps, out area, out int instance, out reason))
            return false;
        if (instance != world.InstanceMapId)
        {
            reason = $"the {task.Area} is on another continent";
            return false;
        }
        return true;
    }

    private bool TryResolve(VisualBarSample bars, out string reason)
    {
        if (!TryResolveArea(task, bars, maps, out ForeverWorldArea resolved, out reason))
            return false;
        int seed = task.Seed ?? Random.Shared.Next();
        area = resolved;
        picker = new(resolved, seed);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever roam of the {Area}: seed {Seed}, spots at least {Spacing:0} yards apart",
                task.Area, seed, picker.Spacing);
        return true;
    }

    private void Walk(VisualBarSample bars)
    {
        if (TookUnit(bars))
            return;
        switch (walker.Update(bars, out string reason))
        {
            case ForeverWalkResult.Arrived:
                walker.Stop();
                skipsInARow = 0;
                machine.Go(ForeverRoamState.Look);
                break;
            case ForeverWalkResult.Skipped:
                if (++skipsInARow >= MaxSkipsInARow)
                {
                    machine.Fail($"no spot in the {task.Area} could be reached: {reason}");
                    return;
                }
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever roam gave up a spot it could not reach: {Reason}", reason);
                SetOff(bars, approach: !area!.Contains(Here(bars)));
                break;
            case ForeverWalkResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    private void Look(VisualBarSample bars)
    {
        if (TookUnit(bars))
            return;
        if (!lookAround.Turned(bars))
        {
            if (navigator.TurnInPlace(bars, LookAroundRate, out string turnReason) ==
                    ForeverNavigationResult.Failed)
                machine.Fail(turnReason);
            return;
        }
        // A full look-around found nothing to take: on to the next spot.
        navigator.StopPursuit();
        SetOff(bars, approach: !area!.Contains(Here(bars)));
    }

    // Units in view are taken unless the task says otherwise; an attack the
    // client cannot use stops the run first, as before every pull.
    private bool TookUnit(VisualBarSample bars)
    {
        if (task.Pulls != ForeverPulls.Any)
            return false;
        switch (ForeverPulling.TryPull(bars, combat, config, engage, out string unusable))
        {
            case ForeverPull.Unusable:
                machine.Fail(unusable);
                return true;
            case ForeverPull.Offered:
                return true;
            default:
                return false;
        }
    }

    // Draws the next spot, or from outside the nearest one inside, and sets
    // the walk toward it; with none, looks round again and draws anew.
    private void SetOff(VisualBarSample bars, bool approach)
    {
        frame = bars;
        Vector2? spot = approach
            ? picker!.Nearest(Here(bars), Snap, Reachable)
            : picker!.Next(Snap, Reachable);
        if (spot is not { } next)
        {
            if (++emptyPicks >= MaxEmptyPicks)
            {
                machine.Fail($"no walkable, reachable spot turned up in the {task.Area}");
                return;
            }
            if (machine.State == ForeverRoamState.Look)
                lookAround.Reset();
            else
                machine.Go(ForeverRoamState.Look);
            return;
        }
        emptyPicks = 0;
        walker.Begin(next, SpotYards, mayGiveUp: true, $"a spot in the {task.Area}");
        if (machine.State != ForeverRoamState.Walk)
            machine.Go(ForeverRoamState.Walk);
    }

    private Vector2? Snap(Vector2 hint) => navigator.TrySnapWalkable(frame, hint, out Vector2 spot) ? spot : null;

    private bool Reachable(Vector2 spot) => navigator.TryFindPath(frame, spot, out _, out _);

    private static Vector2 Here(VisualBarSample bars) =>
        new((float)bars.WorldPosition!.Value.X, (float)bars.WorldPosition!.Value.Y);
}
