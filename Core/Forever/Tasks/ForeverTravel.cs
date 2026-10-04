using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverTravelState { Idle, Walk, Sidestep, BackOut, Failed }

/// <summary>What the travel does when its walk stalls, one step per stall.</summary>
public enum ForeverUnstick { Sidestep, BackOut, SkipVia }

/// <summary>
/// Carries out <see cref="ForeverTravelTask"/>: walks the baked mesh through
/// each point on the way to the destination, held forward and steered with
/// the mouse. A leg's whole path is asked for now and then, and the walk
/// heads for a point <see cref="CarrotYards"/> along it, since a long path
/// costs tens of milliseconds. When the walk stalls, a ladder borrowed from
/// upstream's StuckDetector climbs: step aside, back out turning, skip the
/// point on the way. Only a new closest approach counts as progress, and
/// without one for <see cref="GiveUpMs"/> the travel gives up with why.
/// </summary>
public sealed class ForeverTravel : IForeverTaskRunner
{
    /// <summary>The destination counts as reached within this many yards.</summary>
    public const float ArriveYards = 3;
    /// <summary>A point on the way counts as passed within this many yards, so the walk flows through it.</summary>
    public const float ViaYards = 6;
    /// <summary>The walk heads for the point this far along the leg's path.</summary>
    public const float CarrotYards = 20;
    /// <summary>A path point this close counts as passed.</summary>
    public const float PassYards = 2;
    /// <summary>A new closest approach must be this much closer to count as progress.</summary>
    public const float ProgressYards = 1;
    /// <summary>Without progress this long the travel gives up, as upstream's StuckDetector does.</summary>
    public const int GiveUpMs = 20000;

    // The leg's path is asked for again this often, and whenever the walk
    // strays this far from it, as after a fight or an unstick step.
    private const int RepathMs = 5000;
    private const float OffPathYards = 8;
    // A walk that cut a corner may be nearest a path point this far ahead.
    private const int LookAheadPoints = 20;
    // How long one unstick step is held, at random within the range.
    private const int SidestepMinMs = 700;
    private const int SidestepMaxMs = 1200;
    private const int BackOutMinMs = 800;
    private const int BackOutMaxMs = 1300;
    // How hard the camera turns while backing out, of full steering.
    private const double BackOutTurnRate = 0.5;

    private readonly ILogger logger;
    private readonly ForeverTravelTask task;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly WorldMapAreaDB maps;
    private readonly ForeverStateMachine<ForeverTravelState> machine;
    private readonly ForeverProgress progress = new();

    private List<(Vector2 World, ForeverMapPoint Map)>? legs;
    private int leg;
    private List<Vector2>? path;
    private int next;
    private long pathedAt;
    private int rung;
    private int side = 1;
    private double turnRate;
    private int stepMs;
    private string stallReason = string.Empty;
    private bool arrived;

    public ForeverTravel(ILogger logger, ForeverTravelTask task, ForeverEngageActivity engage,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config, WorldMapAreaDB maps)
    {
        this.logger = logger;
        this.task = task;
        this.engage = engage;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        this.maps = maps;
        // Walking hops when snagged, and now and then for no reason; the
        // unstick steps only step and turn.
        machine = new ForeverStateMachine<ForeverTravelState>(logger, "Forever travel",
                ForeverTravelState.Idle, ForeverTravelState.Failed)
            .On(ForeverTravelState.Idle, exits: [ForeverTravelState.Walk])
            .On(ForeverTravelState.Walk, Walk,
                exits: [ForeverTravelState.Sidestep, ForeverTravelState.BackOut, ForeverTravelState.Idle],
                exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop | ForeverAllowed.IdleHops)
            .On(ForeverTravelState.Sidestep, Sidestep, exits: [ForeverTravelState.Walk, ForeverTravelState.Idle],
                exit: navigator.StopPursuit)
            .On(ForeverTravelState.BackOut, BackOut, exits: [ForeverTravelState.Walk, ForeverTravelState.Idle],
                exit: navigator.StopPursuit)
            .On(ForeverTravelState.Failed, enter: navigator.StopPursuit);
    }

    public string Status => machine.State == ForeverTravelState.Walk ? task.Name : $"{task.Name}/{machine.State}";
    public ForeverAllowed Allowed => machine.Allowed;
    public ForeverTravelState State => machine.State;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverTravelState state) => machine.AllowedIn(state);

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (machine.State == ForeverTravelState.Failed)
            return;
        if (legs is null && !TryResolve(bars, out string reason))
        {
            machine.Fail(reason);
            return;
        }
        // Resumed after a fight or a rest: the way is asked for afresh from
        // where the character stands, with a fresh progress window.
        path = null;
        rung = 0;
        progress.Restart(GetTimestamp());
        if (machine.State == ForeverTravelState.Idle)
            machine.Go(ForeverTravelState.Walk);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress done)
    {
        machine.Tick(bars);
        if (machine.State == ForeverTravelState.Failed)
            return ForeverActivityResult.Failed(machine.LastFailure);
        return arrived ? ForeverActivityResult.Done : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        navigator.StopPursuit();
        if (machine.State is ForeverTravelState.Walk or ForeverTravelState.Sidestep or ForeverTravelState.BackOut)
            machine.Go(ForeverTravelState.Idle);
    }

    // Every point to world yards, once: on a verified map, on the player's
    // continent and covered by the baked mesh.
    private bool TryResolve(VisualBarSample bars, out string reason)
    {
        if (bars.WorldPosition is not { } world)
        {
            reason = "the player's world position is unreadable";
            return false;
        }
        List<(Vector2, ForeverMapPoint)> resolved = [];
        List<ForeverMapPoint> points = [.. task.Via, task.To];
        foreach (ForeverMapPoint point in points)
        {
            if (!point.TryToWorld(maps, out Vector2 at, out int instance, out reason))
                return false;
            if (instance != world.InstanceMapId)
            {
                reason = $"{point} is on another continent";
                return false;
            }
            if (!navigator.IsOnMesh(bars, at))
            {
                reason = $"{point} is outside the baked mesh";
                return false;
            }
            resolved.Add((at, point));
        }
        legs = resolved;
        reason = string.Empty;
        return true;
    }

    private void Walk(VisualBarSample bars)
    {
        if (task.Pulls == ForeverPulls.Any)
        {
            switch (ForeverPulling.TryPull(bars, combat, config, engage, out string unusable))
            {
                case ForeverPull.Unusable:
                    machine.Fail(unusable);
                    return;
                case ForeverPull.Offered:
                    return;
            }
        }
        long now = GetTimestamp();
        VisualWorldPositionSample world = bars.WorldPosition!.Value;
        Vector2 here = new((float)world.X, (float)world.Y);
        (Vector2 target, ForeverMapPoint point) = legs![leg];
        bool final = leg == legs.Count - 1;
        if (Vector2.Distance(here, target) <= (final ? ArriveYards : ViaYards))
        {
            if (final)
            {
                navigator.StopPursuit();
                arrived = true;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever travel arrived at {Point}", point);
                return;
            }
            NextLeg(now);
            return;
        }

        if (path is null || GetElapsedTime(pathedAt).TotalMilliseconds > RepathMs ||
            OffPath(path, next, here) > OffPathYards)
        {
            if (!navigator.TryFindPath(bars, target, out List<Vector2> found, out string pathReason))
            {
                if (!GiveUp(now, point))
                    Stall(bars, pathReason, canSkipVia: !final);
                return;
            }
            path = found;
            next = 0;
            pathedAt = now;
        }
        Vector2 carrot = Follow(path, ref next, here, out float remaining);
        if (progress.Observe(remaining, now))
            rung = 0;
        else if (GiveUp(now, point))
            return;

        switch (navigator.WalkToward(bars, carrot, out string reason))
        {
            case ForeverNavigationResult.Stuck or ForeverNavigationResult.Blocked:
                Stall(bars, reason, canSkipVia: !final);
                break;
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    // Past its limit without a new closest approach: the travel stops with why.
    private bool GiveUp(long now, ForeverMapPoint point)
    {
        if (!progress.GaveUp(now))
            return false;
        machine.Fail($"travel to {point} made no progress for {GiveUpMs / 1000} seconds" +
            (stallReason.Length > 0 ? $": {stallReason}" : string.Empty));
        return true;
    }

    private void NextLeg(long now)
    {
        leg++;
        path = null;
        rung = 0;
        progress.Restart(now);
    }

    // The walk stalled: the next rung of the ladder, after which the way is
    // asked for afresh from wherever the step left the character.
    private void Stall(VisualBarSample bars, string reason, bool canSkipVia)
    {
        stallReason = reason;
        path = null;
        rung++;
        ForeverUnstick step = RungFor(rung, ForeverBindings.MoveBackward.IsBoundIn(bars), canSkipVia);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever travel stalled ({Reason}); unstick step {Rung}: {Step}", reason, rung, step);
        switch (step)
        {
            case ForeverUnstick.SkipVia:
                NextLeg(GetTimestamp());
                break;
            case ForeverUnstick.BackOut:
                turnRate = Random.Shared.Next(2) == 0 ? -BackOutTurnRate : BackOutTurnRate;
                stepMs = Random.Shared.Next(BackOutMinMs, BackOutMaxMs + 1);
                machine.Go(ForeverTravelState.BackOut);
                break;
            default:
                side = -side;
                stepMs = Random.Shared.Next(SidestepMinMs, SidestepMaxMs + 1);
                machine.Go(ForeverTravelState.Sidestep);
                break;
        }
    }

    private void Sidestep(VisualBarSample bars)
    {
        if (machine.ElapsedMs >= stepMs)
        {
            machine.Go(ForeverTravelState.Walk);
            return;
        }
        switch (navigator.Sidestep(bars, side, out string reason))
        {
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                break;
            // Nowhere to step: walk again; the next stall climbs on.
            case ForeverNavigationResult.Blocked:
                stallReason = reason;
                machine.Go(ForeverTravelState.Walk);
                break;
        }
    }

    private void BackOut(VisualBarSample bars)
    {
        if (machine.ElapsedMs >= stepMs)
        {
            machine.Go(ForeverTravelState.Walk);
            return;
        }
        switch (navigator.BackOut(bars, turnRate, out string reason))
        {
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                break;
            case ForeverNavigationResult.Blocked:
                stallReason = reason;
                machine.Go(ForeverTravelState.Walk);
                break;
        }
    }

    /// <summary>
    /// The unstick step for the <paramref name="rung"/>th stall since the last
    /// progress, 1 first: step aside, back out, skip the point on the way, then
    /// step aside and back out in turn. Without a move-backward key it steps
    /// aside instead, and with no point on the way left it does not skip.
    /// </summary>
    public static ForeverUnstick RungFor(int rung, bool canBackOut, bool canSkipVia) => rung switch
    {
        3 when canSkipVia => ForeverUnstick.SkipVia,
        _ when rung % 2 == 0 && canBackOut => ForeverUnstick.BackOut,
        _ => ForeverUnstick.Sidestep,
    };

    /// <summary>
    /// Where to head on <paramref name="path"/> from <paramref name="here"/>:
    /// the point <see cref="CarrotYards"/> along it, and how far is left
    /// along it. <paramref name="next"/> is the first path point not yet
    /// passed; it only moves forward, and past a corner the walk cut.
    /// </summary>
    public static Vector2 Follow(IReadOnlyList<Vector2> path, ref int next, Vector2 here, out float remaining)
    {
        int last = path.Count - 1;
        next = Math.Clamp(next, 0, last);
        int nearest = next;
        float nearestDistance = Vector2.Distance(here, path[next]);
        for (int i = next + 1; i <= Math.Min(last, next + LookAheadPoints); i++)
        {
            float distance = Vector2.Distance(here, path[i]);
            if (distance < nearestDistance)
            {
                nearest = i;
                nearestDistance = distance;
            }
        }
        next = nearest;
        while (next < last && Vector2.Distance(here, path[next]) < PassYards)
            next++;

        remaining = Vector2.Distance(here, path[next]);
        for (int i = next + 1; i <= last; i++)
            remaining += Vector2.Distance(path[i - 1], path[i]);

        float budget = CarrotYards - Vector2.Distance(here, path[next]);
        if (budget <= 0)
            return path[next];
        for (int i = next + 1; i <= last; i++)
        {
            float segment = Vector2.Distance(path[i - 1], path[i]);
            if (segment >= budget)
                return Vector2.Lerp(path[i - 1], path[i], budget / segment);
            budget -= segment;
        }
        return path[last];
    }

    /// <summary>How far <paramref name="here"/> is from the path's leg leading to point <paramref name="next"/>.</summary>
    public static float OffPath(IReadOnlyList<Vector2> path, int next, Vector2 here)
    {
        next = Math.Clamp(next, 0, path.Count - 1);
        if (next == 0)
            return Vector2.Distance(here, path[0]);
        Vector2 a = path[next - 1];
        Vector2 ab = path[next] - a;
        float lengthSquared = ab.LengthSquared();
        float t = lengthSquared == 0 ? 0 : Math.Clamp(Vector2.Dot(here - a, ab) / lengthSquared, 0, 1);
        return Vector2.Distance(here, a + ab * t);
    }
}

/// <summary>
/// Upstream's progress rule for a walk: only a new closest approach counts,
/// by at least <see cref="ForeverTravel.ProgressYards"/>; without one for
/// <see cref="ForeverTravel.GiveUpMs"/> the walk has given up.
/// </summary>
public sealed class ForeverProgress
{
    private float best = float.MaxValue;
    private long bestAt;

    /// <summary>A fresh window from <paramref name="now"/>; the next reading is a new best.</summary>
    public void Restart(long now)
    {
        best = float.MaxValue;
        bestAt = now;
    }

    /// <summary>True when <paramref name="remaining"/> is a new closest approach.</summary>
    public bool Observe(float remaining, long now)
    {
        if (remaining >= best - ForeverTravel.ProgressYards)
            return false;
        best = remaining;
        bestAt = now;
        return true;
    }

    public bool GaveUp(long now) =>
        (now - bestAt) * 1000.0 / Frequency > ForeverTravel.GiveUpMs;
}
