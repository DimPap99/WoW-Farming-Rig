using Microsoft.Extensions.Logging;

using SharedLib;

using System.Collections.Generic;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// Carries out <see cref="ForeverTravelTask"/>: a <see cref="ForeverWalker"/>
/// through each point on the way to the destination, flowing through the
/// points on the way, which a stuck walk may skip; the destination itself it
/// does not give up. With <see cref="ForeverPulls.Any"/> it takes the units
/// in view on the way, as hunting does.
/// </summary>
public sealed class ForeverTravel : IForeverTaskRunner
{
    /// <summary>The destination counts as reached within this many yards.</summary>
    public const float ArriveYards = 3;
    /// <summary>A point on the way counts as passed within this many yards, so the walk flows through it.</summary>
    public const float ViaYards = 6;

    private readonly ILogger logger;
    private readonly ForeverTravelTask task;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly WorldMapAreaDB maps;
    private readonly ForeverWalker walker;

    private List<(Vector2 World, ForeverMapPoint Map)>? legs;
    private int leg;
    private string? failure;

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
        if (task.Pulls == ForeverPulls.Any)
        {
            switch (ForeverPulling.TryPull(bars, combat, config, engage, out string unusable))
            {
                case ForeverPull.Unusable:
                    return ForeverActivityResult.Failed(unusable);
                case ForeverPull.Offered:
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
            case ForeverWalkResult.Skipped:
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever travel skipped {Point} on the way: {Reason}", legs![leg].Map, reason);
                leg++;
                BeginLeg();
                break;
            case ForeverWalkResult.Failed:
                return ForeverActivityResult.Failed(reason);
        }
        return ForeverActivityResult.Running;
    }

    public void Exit() => walker.Stop();

    private void BeginLeg()
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
