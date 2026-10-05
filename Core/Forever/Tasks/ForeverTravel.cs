using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// Carries out <see cref="ForeverTravelTask"/>: a <see cref="ForeverWalker"/>
/// through each point on the way to the destination, flowing through the
/// points on the way, which a stuck walk may skip. A walk to the destination
/// that makes no progress is walked again from where the character stands,
/// <see cref="LegTries"/> walks in all, before the travel fails and the
/// agenda falls back. With <see cref="ForeverPulls.OnTheWay"/> it takes the
/// units standing on its way, and with <see cref="ForeverPulls.Any"/> any unit
/// in view, as hunting does.
/// </summary>
public sealed class ForeverTravel : IForeverTaskRunner
{
    /// <summary>The destination counts as reached within this many yards.</summary>
    public const float ArriveYards = 3;
    /// <summary>Walks to the destination, each from where the last one stalled, before the travel fails.</summary>
    public const int LegTries = 3;
    /// <summary>A point on the way counts as passed within this many yards, so the walk flows through it.</summary>
    public const float ViaYards = 6;
    /// <summary>
    /// <see cref="ForeverPulls.OnTheWay"/>: a unit this near the way still to
    /// walk stands on it, a few steps aside at most.
    /// </summary>
    public const float OnTheWayYards = 20;
    /// <summary><see cref="ForeverPulls.OnTheWay"/>: how far ahead along the way a unit is taken.</summary>
    public const float AheadYards = 80;

    private readonly ILogger logger;
    private readonly ForeverTravelTask task;
    private readonly ForeverNavigator navigator;
    private readonly ForeverPulling pulling;
    private readonly WorldMapAreaDB maps;
    private readonly ForeverWalker walker;

    private List<(Vector2 World, ForeverMapPoint Map)>? legs;
    private int leg;
    // Walks of the current leg so far.
    private int tries;
    private string? failure;

    public ForeverTravel(ILogger logger, ForeverTravelTask task, ForeverEngageActivity engage,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config, WorldMapAreaDB maps)
    {
        this.logger = logger;
        this.task = task;
        this.navigator = navigator;
        pulling = new(logger, combat, config, engage, navigator);
        this.maps = maps;
        walker = new(logger, navigator);
    }

    public string Status => walker.State == ForeverWalkState.Walk ? task.Name : $"{task.Name}/{walker.State}";
    public ForeverAllowed Allowed => walker.Allowed;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (failure is not null)
            return;
        if (legs is null)
        {
            if (!TryResolve(task, bars, navigator, maps, out legs, out string reason))
            {
                failure = reason;
                return;
            }
            BeginLeg();
            return;
        }
        // Resumed after a fight or a rest: the way is asked for afresh from
        // where the character stands.
        walker.Resume();
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress done)
    {
        if (failure is not null)
            return ForeverActivityResult.Failed(failure);
        if (task.Pulls != ForeverPulls.None && bars.WorldPosition is { } world)
        {
            Vector2 here = new((float)world.X, (float)world.Y);
            Func<Vector2, bool>? onTheWay = task.Pulls == ForeverPulls.OnTheWay
                ? point => walker.IsOnTheWay(here, point, OnTheWayYards, AheadYards)
                : null;
            switch (pulling.TryPull(bars, out string unusable, onTheWay))
            {
                case ForeverPull.Failed:
                    return ForeverActivityResult.Failed(unusable);
                case ForeverPull.Offered or ForeverPull.Turning:
                    return ForeverActivityResult.Running;
            }
        }
        switch (walker.Update(bars, out string reason))
        {
            case ForeverWalkResult.Arrived when leg == legs!.Count - 1:
                walker.Stop();
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever travel arrived at {Point}", legs[leg].Map);
                return ForeverActivityResult.Done;
            case ForeverWalkResult.Arrived:
                leg++;
                BeginLeg();
                break;
            // A point on the way the walk cannot get past is passed by.
            case ForeverWalkResult.Skipped:
            case ForeverWalkResult.Failed when walker.NoProgress && leg < legs!.Count - 1:
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever travel skipped {Point} on the way: {Reason}", legs![leg].Map, reason);
                leg++;
                BeginLeg();
                break;
            // The destination is walked again from where the walk stalled.
            case ForeverWalkResult.Failed when walker.NoProgress && tries < LegTries:
                tries++;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever travel: {Reason}; walking again from here, walk {Try} of {Tries}",
                        reason, tries, LegTries);
                Walk();
                break;
            case ForeverWalkResult.Failed:
                return ForeverActivityResult.Failed(reason);
        }
        return ForeverActivityResult.Running;
    }

    public void Exit() => walker.Stop();

    private void BeginLeg()
    {
        tries = 1;
        Walk();
    }

    private void Walk()
    {
        bool final = leg == legs!.Count - 1;
        walker.Begin(legs[leg].World, final ? ArriveYards : ViaYards, mayGiveUp: !final, legs[leg].Map.ToString());
    }

    /// <summary>
    /// Every point of <paramref name="task"/> in world yards: on a verified
    /// map, on the player's continent and covered by the baked mesh, or false
    /// with why. The run's start-up check asks the same before anything moves.
    /// </summary>
    public static bool TryResolve(ForeverTravelTask task, VisualBarSample bars, ForeverNavigator navigator,
        WorldMapAreaDB maps, out List<(Vector2 World, ForeverMapPoint Map)> legs, out string reason)
    {
        legs = [];
        if (bars.WorldPosition is not { } world)
        {
            reason = "the player's world position is unreadable";
            return false;
        }
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
            legs.Add((at, point));
        }
        reason = string.Empty;
        return true;
    }
}
