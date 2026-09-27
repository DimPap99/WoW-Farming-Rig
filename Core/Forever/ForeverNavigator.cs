using Core.Database;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System;
using System.IO;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverNavigationResult { Waiting, StepSent, StepVerified, NoTargetPosition, Failed }

/// <summary>Short, checked local mesh steps from the current live position.</summary>
public sealed class ForeverNavigator : IDisposable
{
    private enum PendingAction { None, Forward, Turn }

    private readonly ILogger<ForeverNavigator> logger;
    private readonly ConfigurableInput input;
    private readonly ForeverVisualBarTracker tracker;
    private readonly WorldMapAreaDB maps;
    private readonly DataConfig data;
    private readonly CancellationToken token;
    private NavmeshPathfinder? nav;
    private Vector2? roamOrigin;
    private Vector2? pursuitOrigin;
    private VisualTargetIdentity? pursuitTarget;
    private Vector2 actionOrigin;
    private double actionDirection;
    private long actionFrame;
    private long actionStarted;
    private PendingAction pending;
    private string? navCache;

    public ForeverNavigator(ILogger<ForeverNavigator> logger,
        ConfigurableInput input, ForeverVisualBarTracker tracker,
        WorldMapAreaDB maps, DataConfig data,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.logger = logger;
        this.input = input;
        this.tracker = tracker;
        this.maps = maps;
        this.data = data;
        token = cts.Token;
    }

    public bool TryReadOnlyPreflight(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { UIMapId: 1420 } map ||
            bars.WorldPosition is not { InstanceMapId: 0 } world ||
            !ForeverTargetSelector.HasBinding(bars, 2,
                BindingID.MOVEFORWARD, ConsoleKey.UpArrow, ModifierKey.None) ||
            !ForeverTargetSelector.HasBinding(bars, 6,
                BindingID.TURNLEFT, input.TurnLeftKey, ModifierKey.None) ||
            !ForeverTargetSelector.HasBinding(bars, 7,
                BindingID.TURNRIGHT, input.TurnRightKey, ModifierKey.None) ||
            input.ForwardKey != ConsoleKey.UpArrow)
        {
            reason = "position or movement bindings are incomplete";
            return false;
        }
        if (!TryNav(out reason))
            return false;
        Vector2 here = new((float)world.X, (float)world.Y);
        if (!PlayerMapAgrees(map, here))
        {
            reason = "map and world position disagree";
            return false;
        }
        Vector2 ahead = here + Heading(map.Direction) * 3;
        return HasShortPath(here, ahead, out reason);
    }

    public ForeverNavigationResult Roam(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !TryNav(out reason))
            return ForeverNavigationResult.Failed;
        Vector2 here = new((float)world.X, (float)world.Y);
        roamOrigin ??= here;
        if (pending != PendingAction.None)
            return CheckPending(here, map.Direction, out reason);

        double desired = map.Direction;
        if (Vector2.Distance(here, roamOrigin.Value) >= 25)
            desired = Bearing(here, roamOrigin.Value);
        else if (!HasShortPath(here, here + Heading(desired) * 3, out _))
            desired = Normalize(desired + Math.PI / 2);
        return SendStep(bars, here, desired, out reason);
    }

    public ForeverNavigationResult Approach(VisualBarSample bars,
        VisualTargetIdentity target, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            bars.TargetIdentity != target || !TryNav(out reason))
            return ForeverNavigationResult.Failed;
        Vector2 here = new((float)world.X, (float)world.Y);
        if (pending != PendingAction.None)
            return CheckPending(here, map.Direction, out reason);

        if (!TryTargetPoint(bars, out Vector2 destination))
            return ForeverNavigationResult.NoTargetPosition;
        if (Vector2.Distance(here, destination) > 45)
        {
            reason = "target is too far for local pursuit";
            return ForeverNavigationResult.Failed;
        }
        if (!TryPathWaypoint(here, destination, out Vector2 waypoint,
                out reason))
            return ForeverNavigationResult.Failed;
        return SendStep(bars, here, Bearing(here, waypoint), out reason);
    }

    public ForeverNavigationResult SearchForTarget(VisualBarSample bars,
        VisualTargetIdentity target, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map ||
            bars.WorldPosition is not { } world ||
            bars.TargetIdentity != target || !TryNav(out reason))
            return ForeverNavigationResult.Failed;

        Vector2 here = new((float)world.X, (float)world.Y);
        if (pursuitTarget != target)
        {
            pursuitTarget = target;
            pursuitOrigin = here;
        }
        if (pending != PendingAction.None)
            return CheckPending(here, map.Direction, out reason);
        if (Vector2.Distance(here, pursuitOrigin!.Value) >= 35)
        {
            reason = "target stayed out of range after 35 yards of local search";
            return ForeverNavigationResult.Failed;
        }
        double desired = map.Direction;
        if (bars.TargetScreenPosition is { } screen)
        {
            if (screen.X < 0.42)
                desired = Normalize(desired + 0.45);
            else if (screen.X > 0.58)
                desired = Normalize(desired - 0.45);
        }
        if (desired == map.Direction &&
            !HasShortPath(here, here + Heading(desired) * 3, out _))
            desired = Normalize(desired + Math.PI / 2);
        return SendStep(bars, here, desired, out reason);
    }

    private ForeverNavigationResult SendStep(VisualBarSample bars,
        Vector2 here, double desired, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map)
            return ForeverNavigationResult.Failed;
        double delta = SignedAngle(desired - map.Direction);
        ConsoleKey key;
        int duration;
        if (Math.Abs(delta) > 0.20)
        {
            key = delta > 0 ? input.TurnLeftKey : input.TurnRightKey;
            int index = delta > 0 ? 6 : 7;
            BindingID command = delta > 0 ? BindingID.TURNLEFT : BindingID.TURNRIGHT;
            if (!ForeverTargetSelector.HasBinding(bars, index, command,
                    key, ModifierKey.None))
            {
                reason = "turn binding changed";
                return ForeverNavigationResult.Failed;
            }
            duration = Math.Min(120, Math.Max(45,
                (int)(Math.Abs(delta) * 1000 / Math.PI)));
            pending = PendingAction.Turn;
        }
        else
        {
            if (!ForeverTargetSelector.HasBinding(bars, 2,
                    BindingID.MOVEFORWARD, ConsoleKey.UpArrow,
                    ModifierKey.None) ||
                !HasShortPath(here, here + Heading(map.Direction) * 3,
                    out reason))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "forward path or binding changed" : reason;
                return ForeverNavigationResult.Failed;
            }
            key = ConsoleKey.UpArrow;
            duration = 200;
            pending = PendingAction.Forward;
        }
        actionOrigin = here;
        actionDirection = map.Direction;
        actionFrame = tracker.LatestFrameId;
        actionStarted = GetTimestamp();
        try
        {
            input.PressFixed(key, duration, token);
        }
        finally
        {
            input.SetKeyState(key, false, true);
        }
        return ForeverNavigationResult.StepSent;
    }

    private ForeverNavigationResult CheckPending(Vector2 here,
        double direction, out string reason)
    {
        reason = string.Empty;
        if (pending == PendingAction.None)
            return ForeverNavigationResult.Waiting;
        if (tracker.LatestFrameId == actionFrame)
            return ForeverNavigationResult.Waiting;
        if (pending == PendingAction.Forward)
        {
            Vector2 delta = here - actionOrigin;
            Vector2 forward = Heading(actionDirection);
            float along = Vector2.Dot(delta, forward);
            float lateral = MathF.Abs(delta.X * forward.Y - delta.Y * forward.X);
            if (along is >= 0.20f and < 4f && lateral < 2f)
            {
                pending = PendingAction.None;
                return ForeverNavigationResult.StepVerified;
            }
        }
        else if (Math.Abs(SignedAngle(direction - actionDirection)) >= 0.05 &&
                 Vector2.Distance(here, actionOrigin) < 1)
        {
            pending = PendingAction.None;
            return ForeverNavigationResult.StepVerified;
        }
        if (GetElapsedTime(actionStarted).TotalSeconds < 3)
            return ForeverNavigationResult.Waiting;
        reason = "movement or turn was not verified";
        pending = PendingAction.None;
        return ForeverNavigationResult.Failed;
    }

    private bool TryTargetPoint(VisualBarSample bars, out Vector2 point)
    {
        if (bars.TargetWorldPosition is { } targetWorld &&
            bars.WorldPosition is { } playerWorld &&
            targetWorld.InstanceMapId == playerWorld.InstanceMapId)
        {
            point = new((float)targetWorld.X, (float)targetWorld.Y);
            return true;
        }
        if (bars.TargetMapPosition is { } targetMap &&
            bars.Position is { } playerMap &&
            targetMap.UIMapId == playerMap.UIMapId &&
            maps.TryGet(targetMap.UIMapId, out WorldMapArea area))
        {
            Vector3 world = WorldMapAreaDB.ToWorld_FlipXY(new(
                (float)(targetMap.MapX * 100),
                (float)(targetMap.MapY * 100), 0), area);
            point = new(world.X, world.Y);
            return true;
        }
        point = default;
        return false;
    }

    private bool TryPathWaypoint(Vector2 here, Vector2 destination,
        out Vector2 waypoint, out string reason)
    {
        waypoint = default;
        reason = string.Empty;
        if (nav is null ||
            !nav.TryGetHeight(here.X, here.Y, out float startZ) ||
            !nav.TryGetHeight(destination.X, destination.Y, out float endZ))
        {
            reason = "player or target is outside the baked mesh";
            return false;
        }
        var path = nav.FindPath(new(here.X, here.Y, startZ),
            new(destination.X, destination.Y, endZ), null);
        if (path is not { locations.Count: >= 2 } ||
            nav.LastStats.TilesBaked != 0 ||
            Vector2.Distance(new(path.locations[^1].X,
                path.locations[^1].Y), destination) > 1)
        {
            reason = "no complete disk-only path to target";
            return false;
        }
        foreach (Vector3 location in path.locations)
        {
            Vector2 candidate = new(location.X, location.Y);
            if (Vector2.Distance(here, candidate) >= 1)
            {
                waypoint = candidate;
                return true;
            }
        }
        waypoint = destination;
        return true;
    }

    private bool HasShortPath(Vector2 here, Vector2 ahead,
        out string reason)
    {
        reason = string.Empty;
        if (nav is null ||
            !nav.TryGetHeight(here.X, here.Y, out float startZ) ||
            !nav.TryGetHeight(ahead.X, ahead.Y, out float endZ) ||
            Math.Abs(endZ - startZ) > 1.5f)
        {
            reason = "forward segment is outside the baked mesh";
            return false;
        }
        var path = nav.FindPath(new(here.X, here.Y, startZ),
            new(ahead.X, ahead.Y, endZ), null);
        if (path is not { locations.Count: >= 2 } ||
            nav.LastStats.TilesBaked != 0 ||
            Vector2.Distance(new(path.locations[0].X,
                path.locations[0].Y), here) > 0.5f ||
            Vector2.Distance(new(path.locations[^1].X,
                path.locations[^1].Y), ahead) > 0.5f)
        {
            reason = "forward segment has no complete disk-only path";
            return false;
        }
        return true;
    }

    private bool TryNav(out string reason)
    {
        reason = string.Empty;
        if (!maps.TryGet(1420, out WorldMapArea area) ||
            area.MapID != 0 || area.Continent != "Azeroth")
        {
            reason = "Forever map 1420 is unavailable";
            return false;
        }
        NavmeshBakeOptions bake = new();
        string version = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        string hash = NavmeshSettings.ComputeSettingsHash("forever", version,
            bake.AgentRadius, bake.AgentMaxClimb, bake.WalkableSlope);
        string cache = Path.Combine(data.Navmesh, area.Continent, hash);
        if (!Directory.Exists(cache))
        {
            reason = "baked Forever navmesh is unavailable";
            return false;
        }
        if (nav is null || navCache != cache)
        {
            nav?.Dispose();
            nav = new(logger, null, cache, bake, new NavmeshQueryOptions());
            navCache = cache;
        }
        return true;
    }

    private bool PlayerMapAgrees(VisualPositionSample map, Vector2 here)
    {
        if (!maps.TryGet(map.UIMapId, out WorldMapArea area))
            return false;
        Vector3 mapped = WorldMapAreaDB.ToWorld_FlipXY(new(
            (float)(map.MapX * 100), (float)(map.MapY * 100), 0), area);
        return Vector2.Distance(here, new(mapped.X, mapped.Y)) <= 1;
    }

    private static Vector2 Heading(double angle) =>
        new(MathF.Cos((float)angle), MathF.Sin((float)angle));
    private static double Bearing(Vector2 from, Vector2 to) =>
        Math.Atan2(to.Y - from.Y, to.X - from.X);
    private static double Normalize(double angle) =>
        (angle + Math.PI * 2) % (Math.PI * 2);
    private static double SignedAngle(double angle) =>
        Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    public void Dispose() => nav?.Dispose();
}
