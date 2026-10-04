using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverWalkState { Idle, Walk, Sidestep, BackOut, Failed }

/// <summary>What the walk does when it stalls, one step per stall.</summary>
public enum ForeverUnstick { Sidestep, BackOut, SkipVia }

/// <summary>
/// How one frame of a walk ended. <see cref="Arrived"/>: within reach of the
/// destination, forward still held, so the caller either sets off toward the
/// next point, flowing through, or stops. <see cref="Skipped"/>: the walk
/// gave this destination up, which the caller allowed.
/// </summary>
public enum ForeverWalkResult { Walking, Arrived, Skipped, Failed }

/// <summary>
/// Walks to one world point along the baked mesh, held forward and steered
/// with the mouse, and gets itself unstuck on the way: the engine under
/// travelling and roaming. A long path costs tens of milliseconds, so the
/// walk asks for the whole way now and then and heads for a point
/// <see cref="CarrotYards"/> along it. When it stalls, a ladder borrowed from
/// upstream's StuckDetector climbs: step aside, back out turning, give the
/// destination up where the caller allows it. Only a new closest approach
/// counts as progress, and without one for <see cref="GiveUpMs"/> the walk
/// fails with why.
/// </summary>
public sealed class ForeverWalker
{
    /// <summary>The walk heads for the point this far along the path.</summary>
    public const float CarrotYards = 20;
    /// <summary>A path point this close counts as passed.</summary>
    public const float PassYards = 2;
    /// <summary>A new closest approach must be this much closer to count as progress.</summary>
    public const float ProgressYards = 1;
    /// <summary>Without progress this long the walk gives up, as upstream's StuckDetector does.</summary>
    public const int GiveUpMs = 20000;

    // The path is asked for again this often, and whenever the walk strays
    // this far from it, as after an unstick step.
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
    private readonly ForeverNavigator navigator;
    private readonly ForeverStateMachine<ForeverWalkState> machine;
    private readonly ForeverProgress progress = new();

    private Vector2? destination;
    private float arriveYards;
    private bool canSkip;
    private string label = string.Empty;
    private List<Vector2>? path;
    // The height of the floor under each path point.
    private List<float>? heights;
    private int next;
    private long pathedAt;
    private int rung;
    private int side = 1;
    private double turnRate;
    private int stepMs;
    private string stallReason = string.Empty;
    private bool arrived;
    private bool skipped;

    public ForeverWalker(ILogger logger, ForeverNavigator navigator)
    {
        this.logger = logger;
        this.navigator = navigator;
        // Walking hops when snagged, and now and then for no reason; the
        // unstick steps only step and turn.
        machine = new ForeverStateMachine<ForeverWalkState>(logger, "Forever walk",
                ForeverWalkState.Idle, ForeverWalkState.Failed)
            .On(ForeverWalkState.Idle, exits: [ForeverWalkState.Walk])
            .On(ForeverWalkState.Walk, Walk,
                exits: [ForeverWalkState.Sidestep, ForeverWalkState.BackOut, ForeverWalkState.Idle],
                exit: navigator.StopPursuit, allows: ForeverAllowed.SnagHop | ForeverAllowed.IdleHops)
            .On(ForeverWalkState.Sidestep, Sidestep, exits: [ForeverWalkState.Walk, ForeverWalkState.Idle],
                exit: navigator.StopPursuit)
            .On(ForeverWalkState.BackOut, BackOut, exits: [ForeverWalkState.Walk, ForeverWalkState.Idle],
                exit: navigator.StopPursuit)
            .On(ForeverWalkState.Failed, enter: navigator.StopPursuit);
    }

    public ForeverWalkState State => machine.State;
    public ForeverAllowed Allowed => machine.Allowed;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverWalkState state) => machine.AllowedIn(state);

    /// <summary>
    /// Sets off toward <paramref name="to"/>; a walk already under way turns
    /// toward it without stopping, so it flows through a point on the way.
    /// </summary>
    /// <param name="reach">Within this many yards the destination is reached.</param>
    /// <param name="mayGiveUp">A stall may give this destination up instead of climbing on.</param>
    /// <param name="name">Names the destination in logs and failures.</param>
    public void Begin(Vector2 to, float reach, bool mayGiveUp, string name)
    {
        destination = to;
        arriveYards = reach;
        canSkip = mayGiveUp;
        label = name;
        arrived = false;
        skipped = false;
        Resume();
    }

    /// <summary>After something else ran: the way is asked for afresh from here, with a fresh progress window.</summary>
    public void Resume()
    {
        path = null;
        rung = 0;
        progress.Restart(GetTimestamp());
        if (destination is not null && machine.State == ForeverWalkState.Idle)
            machine.Go(ForeverWalkState.Walk);
    }

    /// <summary>Releases held movement; <see cref="Resume"/> or <see cref="Begin"/> sets off again.</summary>
    public void Stop()
    {
        navigator.StopPursuit();
        if (machine.State is ForeverWalkState.Walk or ForeverWalkState.Sidestep or ForeverWalkState.BackOut)
            machine.Go(ForeverWalkState.Idle);
    }

    public ForeverWalkResult Update(VisualBarSample bars, out string reason)
    {
        machine.Tick(bars);
        reason = machine.LastFailure;
        if (machine.State == ForeverWalkState.Failed)
            return ForeverWalkResult.Failed;
        if (arrived)
            return ForeverWalkResult.Arrived;
        if (skipped)
        {
            skipped = false;
            reason = stallReason;
            return ForeverWalkResult.Skipped;
        }
        return ForeverWalkResult.Walking;
    }

    private void Walk(VisualBarSample bars)
    {
        if (destination is not { } target)
            return;
        long now = GetTimestamp();
        VisualWorldPositionSample world = bars.WorldPosition!.Value;
        Vector2 here = new((float)world.X, (float)world.Y);
        arrived = Vector2.Distance(here, target) <= arriveYards;
        if (arrived)
            return;

        if (path is null || GetElapsedTime(pathedAt).TotalMilliseconds > RepathMs ||
            OffPath(path, next, here) > OffPathYards)
        {
            if (!navigator.TryFindPath(bars, target, out List<Vector3> found, out string pathReason))
            {
                if (!GiveUp(now))
                    Stall(bars, pathReason);
                return;
            }
            path = found.ConvertAll(point => new Vector2(point.X, point.Y));
            heights = found.ConvertAll(point => point.Z);
            next = 0;
            pathedAt = now;
        }
        Vector2 carrot = Follow(path, ref next, here, out float remaining);
        if (progress.Observe(remaining, now))
            rung = 0;
        else if (GiveUp(now))
            return;

        // The step keeps to the floor the whole way runs on, at both ends: the
        // column under a point may also hold a ledge or a rock top beside the trail.
        switch (navigator.WalkToward(bars, carrot, out string reason,
            destinationZ: HeightAt(path, heights!, carrot), startZ: HeightAt(path, heights!, here)))
        {
            case ForeverNavigationResult.Stuck or ForeverNavigationResult.Blocked:
                Stall(bars, reason);
                break;
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                break;
        }
    }

    /// <summary>The height of the path's floor at <paramref name="point"/>, which lies on or near the path.</summary>
    public static float HeightAt(IReadOnlyList<Vector2> path, IReadOnlyList<float> heights, Vector2 point)
    {
        float z = heights[0];
        float best = float.MaxValue;
        for (int i = 1; i < path.Count; i++)
        {
            Vector2 a = path[i - 1];
            Vector2 ab = path[i] - a;
            float length = ab.LengthSquared();
            float t = length == 0 ? 0 : Math.Clamp(Vector2.Dot(point - a, ab) / length, 0, 1);
            float distance = Vector2.DistanceSquared(point, a + ab * t);
            if (distance < best)
            {
                best = distance;
                z = heights[i - 1] + (heights[i] - heights[i - 1]) * t;
            }
        }
        return z;
    }

    // Past its limit without a new closest approach: the walk stops with why.
    private bool GiveUp(long now)
    {
        if (!progress.GaveUp(now))
            return false;
        machine.Fail($"walking to {label} made no progress for {GiveUpMs / 1000} seconds" +
            (stallReason.Length > 0 ? $": {stallReason}" : string.Empty));
        return true;
    }

    // The walk stalled: the next rung of the ladder, after which the way is
    // asked for afresh from wherever the step left the character.
    private void Stall(VisualBarSample bars, string reason)
    {
        stallReason = reason;
        path = null;
        rung++;
        ForeverUnstick step = RungFor(rung, ForeverBindings.MoveBackward.IsBoundIn(bars), canSkip);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever walk to {Label} stalled ({Reason}); unstick step {Rung}: {Step}",
                label, reason, rung, step);
        switch (step)
        {
            case ForeverUnstick.SkipVia:
                navigator.StopPursuit();
                skipped = true;
                break;
            case ForeverUnstick.BackOut:
                turnRate = Random.Shared.Next(2) == 0 ? -BackOutTurnRate : BackOutTurnRate;
                stepMs = Random.Shared.Next(BackOutMinMs, BackOutMaxMs + 1);
                machine.Go(ForeverWalkState.BackOut);
                break;
            default:
                side = -side;
                stepMs = Random.Shared.Next(SidestepMinMs, SidestepMaxMs + 1);
                machine.Go(ForeverWalkState.Sidestep);
                break;
        }
    }

    private void Sidestep(VisualBarSample bars)
    {
        if (machine.ElapsedMs >= stepMs)
        {
            machine.Go(ForeverWalkState.Walk);
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
                machine.Go(ForeverWalkState.Walk);
                break;
        }
    }

    private void BackOut(VisualBarSample bars)
    {
        if (machine.ElapsedMs >= stepMs)
        {
            machine.Go(ForeverWalkState.Walk);
            return;
        }
        switch (navigator.BackOut(bars, turnRate, out string reason))
        {
            case ForeverNavigationResult.Failed:
                machine.Fail(reason);
                break;
            case ForeverNavigationResult.Blocked:
                stallReason = reason;
                machine.Go(ForeverWalkState.Walk);
                break;
        }
    }

    /// <summary>
    /// The unstick step for the <paramref name="rung"/>th stall since the last
    /// progress, 1 first: step aside, back out, give the destination up, then
    /// step aside and back out in turn. Without a move-backward key it steps
    /// aside instead, and where the caller does not allow it, it does not give up.
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
/// by at least <see cref="ForeverWalker.ProgressYards"/>; without one for
/// <see cref="ForeverWalker.GiveUpMs"/> the walk has given up.
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
        if (remaining >= best - ForeverWalker.ProgressYards)
            return false;
        best = remaining;
        bestAt = now;
        return true;
    }

    public bool GaveUp(long now) =>
        (now - bestAt) * 1000.0 / Frequency > ForeverWalker.GiveUpMs;
}
