using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System;
using System.IO;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverNavigationResult { Waiting, StepSent, StepVerified, NoTargetPosition, Blocked, Failed }

/// <summary>Short, checked local mesh steps from the current live position.</summary>
public sealed class ForeverNavigator : IDisposable
{
    private enum PendingAction { None, Forward, Turn }

    private readonly ILogger<ForeverNavigator> logger;
    private readonly ConfigurableInput input;
    private readonly MouseCameraInput camera;
    private readonly WorldMapAreaDB maps;
    private readonly DataConfig data;
    private readonly CancellationToken token;

    // Movement is pressed on the controller thread, but a stop arrives on
    // another one. The lock orders the two, and once halted, an update that
    // was already running cannot press forward, strafe or mouse-look again.
    private readonly object inputLock = new();
    private bool halted;

    private NavmeshPathfinder? nav;
    private Vector2? roamOrigin;
    private Vector2? pursuitOrigin;
    private VisualTargetIdentity? pursuitTarget;
    private Vector2 actionOrigin;
    private double actionDirection;
    private long actionFrame;
    private long actionStarted;
    private PendingAction pending;
    private bool pursuitForwardHeld;
    private ConsoleKey? pursuitStrafeKey;
    private Vector2 pursuitProgressPoint;
    private long pursuitProgressAt;
    private long markerMissingAt;
    private string? navCache;
    private Vector2? detourWaypoint;

    public ForeverNavigator(ILogger<ForeverNavigator> logger,
        ConfigurableInput input, MouseCameraInput camera,
        WorldMapAreaDB maps, DataConfig data,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.logger = logger;
        this.input = input;
        this.camera = camera;
        this.maps = maps;
        this.data = data;
        token = cts.Token;
    }

    public bool TryReadOnlyPreflight(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { UIMapId: ForeverPaladin.UIMapId } map ||
            bars.WorldPosition is not { InstanceMapId: 0 } world ||
            !ForeverBindings.MoveForward.IsBoundIn(bars) ||
            !ForeverBindings.TurnLeft(input.TurnLeftKey).IsBoundIn(bars) ||
            !ForeverBindings.TurnRight(input.TurnRightKey).IsBoundIn(bars) ||
            input.ForwardKey != ForeverBindings.MoveForward.Key)
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
        return TrySafeHeading(here, (float)world.Z, map.Direction,
            out _, out reason);
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
            return CheckPending(bars.FrameId, here, map.Direction, out reason);

        double desired = map.Direction;
        if (Vector2.Distance(here, roamOrigin.Value) >= 25)
            desired = Bearing(here, roamOrigin.Value);
        if (!TrySafeHeading(here, (float)world.Z, desired,
                out desired, out reason))
            return ForeverNavigationResult.Failed;
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
            return CheckPending(bars.FrameId, here, map.Direction, out reason);

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
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }

        Vector2 here = new((float)world.X, (float)world.Y);
        if (pursuitTarget != target)
        {
            StopPursuit();
            markerMissingAt = 0;
            pursuitTarget = target;
            pursuitOrigin = here;
            detourWaypoint = null;
        }
        if (pending != PendingAction.None)
            return CheckPending(bars.FrameId, here, map.Direction, out reason);
        if (Vector2.Distance(here, pursuitOrigin!.Value) >= 35)
        {
            StopPursuit();
            reason = "target stayed out of range after 35 yards of local search";
            return ForeverNavigationResult.Failed;
        }
        if (detourWaypoint is { } waypoint)
            return FollowDetour(bars, here, waypoint, out reason);

        if (bars.TargetScreenPosition is not { } screen)
        {
            StopTranslation();
            if (markerMissingAt == 0)
                markerMissingAt = GetTimestamp();
            double missingSeconds = GetElapsedTime(markerMissingAt).TotalSeconds;
            if (missingSeconds > 5)
            {
                reason = "selected nameplate marker stayed unavailable";
                return ForeverNavigationResult.Failed;
            }
            if (missingSeconds < 0.3)
                return ForeverNavigationResult.Waiting;
            return TurnTowardMarker(0.25, out reason);
        }
        markerMissingAt = 0;

        // Turning in place needs no walkable segment in the old direction.
        // Align first; otherwise a wall beside us can prevent facing a clear path.
        double horizontalError = screen.X - 0.5;
        if (Math.Abs(horizontalError) > 0.15)
        {
            StopTranslation();
            return TurnTowardMarker(horizontalError, out reason);
        }

        if (pursuitForwardHeld || pursuitStrafeKey is not null)
        {
            float travel = Vector2.Distance(here, pursuitProgressPoint);
            if (travel >= 0.20f)
            {
                pursuitProgressPoint = here;
                pursuitProgressAt = GetTimestamp();
            }
            else if (GetElapsedTime(pursuitProgressAt).TotalSeconds > 3)
            {
                StopPursuit();
                reason = "held forward movement was not verified";
                return ForeverNavigationResult.Failed;
            }
        }

        if (!ForeverBindings.MoveForward.IsBoundIn(bars))
        {
            StopPursuit();
            reason = "forward binding changed";
            return ForeverNavigationResult.Failed;
        }

        bool forward = true;
        ConsoleKey? strafe = null;
        if (!HasShortPath(here, (float)world.Z,
                here + Heading(map.Direction) * 3,
                out string forwardReason))
        {
            // Prefer a diagonal step toward the marker, then a pure sidestep.
            int towardMarker = screen.X < 0.5 ? -1 : 1;
            strafe = FindStrafe(Math.PI / 4);
            if (strafe is null && (strafe = FindStrafe(Math.PI / 2)) is not null)
                forward = false;
            if (strafe is null)
            {
                StopPursuit();
                if (TryDetour(here, (float)world.Z, map.Direction, out Vector2 nextWaypoint))
                {
                    detourWaypoint = nextWaypoint;
                    return ForeverNavigationResult.Waiting;
                }
                reason = $"no checked forward or sidestep path toward target; forward: {forwardReason}";
                return ForeverNavigationResult.Blocked;
            }

            ConsoleKey? FindStrafe(double angle)
            {
                foreach (int side in new[] { towardMarker, -towardMarker })
                {
                    ForeverBinding binding = side < 0
                        ? ForeverBindings.StrafeLeft : ForeverBindings.StrafeRight;
                    if (binding.IsBoundIn(bars) &&
                        HasShortPath(here, (float)world.Z,
                            here + Heading(map.Direction - side * angle) * 3, out _))
                        return binding.Key;
                }
                return null;
            }
        }

        // Acquire foreground before key-down: WoW releases movement keys on
        // focus changes, so sending forward first can leave only our held flag.
        if (TurnTowardMarker(horizontalError, out reason) == ForeverNavigationResult.Failed)
            return ForeverNavigationResult.Failed;
        bool starting = !pursuitForwardHeld && pursuitStrafeKey is null;
        SetPursuitMovement(here, forward, strafe);
        return starting ? ForeverNavigationResult.StepSent : ForeverNavigationResult.StepVerified;
    }

    private bool TryDetour(Vector2 here, float z, double direction, out Vector2 waypoint)
    {
        waypoint = default;
        foreach (float distance in new[] { 3f, 6f, 9f, 12f })
        {
            Vector2 end = here + Heading(direction) * distance;
            // The client reports no player height, so z is 0 and both ends
            // resolve to the ground under their own X/Y (PreferGroundUnderPoint).
            var path = nav!.FindPath(new(here.X, here.Y, z), new(end.X, end.Y, z), null);
            if (path is not { locations.Count: >= 2 } ||
                Vector2.Distance(here, new(path.locations[0].X, path.locations[0].Y)) > 0.5f ||
                Vector2.Distance(end, new(path.locations[^1].X, path.locations[^1].Y)) > 1)
                continue;
            foreach (Vector3 point in path.locations)
            {
                Vector2 next = new(point.X, point.Y);
                if (Vector2.Distance(here, next) < 1) continue;
                if (HasShortPath(here, z, next, out _))
                {
                    waypoint = next;
                    return true;
                }
                break;
            }
        }
        return false;
    }

    private ForeverNavigationResult FollowDetour(VisualBarSample bars, Vector2 here,
        Vector2 waypoint, out string reason)
    {
        reason = string.Empty;
        float distance = Vector2.Distance(here, waypoint);
        if (distance < 0.65f)
        {
            detourWaypoint = null;
            StopTranslation();
            return ForeverNavigationResult.StepVerified;
        }
        double delta = SignedAngle(Bearing(here, waypoint) - bars.Position!.Value.Direction);
        if (Math.Abs(delta) > 0.20)
        {
            StopTranslation();
            return TurnTowardMarker(-delta / Math.PI, out reason);
        }
        if (!ForeverBindings.MoveForward.IsBoundIn(bars) ||
            !HasShortPath(here, (float)bars.WorldPosition!.Value.Z,
                here + Heading(bars.Position.Value.Direction) * Math.Min(3, distance), out reason))
        {
            StopPursuit();
            detourWaypoint = null;
            return ForeverNavigationResult.Blocked;
        }
        ForeverNavigationResult turn = TurnTowardMarker(-delta / Math.PI, out reason);
        if (turn != ForeverNavigationResult.Failed)
            SetPursuitMovement(here, true, null);
        return turn;
    }

    private ForeverNavigationResult TurnTowardMarker(double horizontalError, out string reason)
    {
        reason = string.Empty;
        lock (inputLock)
        {
            if (halted)
            {
                reason = "controller stopped";
                return ForeverNavigationResult.Failed;
            }
            if (!camera.Turn(horizontalError))
            {
                StopPursuit();
                reason = "game lost focus during mouse steering";
                return ForeverNavigationResult.Failed;
            }
        }
        return ForeverNavigationResult.StepSent;
    }

    private void SetPursuitMovement(Vector2 here, bool forward,
        ConsoleKey? strafe)
    {
        lock (inputLock)
        {
            if (halted ||
                (pursuitForwardHeld == forward && pursuitStrafeKey == strafe))
                return;
            if (pursuitStrafeKey is { } oldStrafe)
                input.SetKeyState(oldStrafe, false, true);
            if (pursuitForwardHeld && !forward)
                input.StopForward(true);
            if (forward && !pursuitForwardHeld)
                input.StartForward(false);
            if (strafe is { } newStrafe)
                input.SetKeyState(newStrafe, true, false);
            pursuitForwardHeld = forward;
            pursuitStrafeKey = strafe;
            pursuitProgressPoint = here;
            pursuitProgressAt = GetTimestamp();
        }
    }

    public void StopPursuit()
    {
        lock (inputLock)
        {
            StopTranslation();
            camera.Stop();
        }
    }

    /// <summary>
    /// Releases held movement and refuses any further movement input from this
    /// navigator. Call it when the controller stops; it is safe from any thread.
    /// </summary>
    public void Halt()
    {
        lock (inputLock)
        {
            halted = true;
            StopPursuit();
        }
    }

    private void StopTranslation()
    {
        lock (inputLock)
        {
            if (pursuitForwardHeld)
                input.StopForward(true);
            if (pursuitStrafeKey is { } strafe)
                input.SetKeyState(strafe, false, true);
            pursuitForwardHeld = false;
            pursuitStrafeKey = null;
        }
    }

    private ForeverNavigationResult SendStep(VisualBarSample bars,
        Vector2 here, double desired, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map)
            return ForeverNavigationResult.Failed;
        // A fixed step assumes the character is standing still. Release any
        // forward, strafe or mouse-look held by SearchForTarget first, or the
        // character keeps running and the step check measures the wrong motion.
        StopPursuit();
        double delta = SignedAngle(desired - map.Direction);
        ConsoleKey key;
        int duration;
        if (Math.Abs(delta) > 0.20)
        {
            key = delta > 0 ? input.TurnLeftKey : input.TurnRightKey;
            ForeverBinding turn = delta > 0
                ? ForeverBindings.TurnLeft(key) : ForeverBindings.TurnRight(key);
            if (!turn.IsBoundIn(bars))
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
            if (!ForeverBindings.MoveForward.IsBoundIn(bars) ||
                !HasShortPath(here, (float)bars.WorldPosition!.Value.Z,
                    here + Heading(map.Direction) * 3,
                    out reason))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "forward path or binding changed" : reason;
                return ForeverNavigationResult.Failed;
            }
            key = ForeverBindings.MoveForward.Key;
            duration = 200;
            pending = PendingAction.Forward;
        }
        actionOrigin = here;
        actionDirection = map.Direction;
        actionFrame = bars.FrameId;
        actionStarted = GetTimestamp();
        lock (inputLock)
        {
            if (halted)
            {
                pending = PendingAction.None;
                reason = "controller stopped";
                return ForeverNavigationResult.Failed;
            }
            try
            {
                input.PressFixed(key, duration, token);
            }
            finally
            {
                input.SetKeyState(key, false, true);
            }
        }
        return ForeverNavigationResult.StepSent;
    }

    private ForeverNavigationResult CheckPending(long frameId, Vector2 here,
        double direction, out string reason)
    {
        reason = string.Empty;
        if (pending == PendingAction.None)
            return ForeverNavigationResult.Waiting;
        if (frameId == actionFrame)
            return ForeverNavigationResult.Waiting;
        if (pending == PendingAction.Forward)
        {
            Vector2 delta = here - actionOrigin;
            Vector2 forward = Heading(actionDirection);
            float along = Vector2.Dot(delta, forward);
            float lateral = MathF.Abs(delta.X * forward.Y - delta.Y * forward.X);
            if (along >= 0.20f && lateral < 2f)
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
            Vector3 world = targetMap.ToWorld(area);
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

    private bool HasShortPath(Vector2 here, float playerZ, Vector2 ahead,
        out string reason)
    {
        reason = string.Empty;
        if (nav is null || !float.IsFinite(playerZ))
        {
            reason = "player height or baked mesh is unavailable";
            return false;
        }
        return nav.TryWalkStraight(new(here.X, here.Y, playerZ), ahead,
            out _, out reason);
    }

    private bool TrySafeHeading(Vector2 here, float playerZ, double preferred,
        out double direction, out string reason)
    {
        foreach (double offset in new[]
            { 0.0, Math.PI / 4, -Math.PI / 4,
              Math.PI / 2, -Math.PI / 2,
              3 * Math.PI / 4, -3 * Math.PI / 4, Math.PI })
        {
            double candidate = Normalize(preferred + offset);
            if (HasShortPath(here, playerZ,
                    here + Heading(candidate) * 3, out _))
            {
                direction = candidate;
                reason = string.Empty;
                return true;
            }
        }
        direction = preferred;
        reason = "no checked three-yard segment is available from the current position";
        return false;
    }

    private bool TryNav(out string reason)
    {
        reason = string.Empty;
        if (!ForeverNavmesh.TryGetVerifiedArea(maps, out WorldMapArea area))
        {
            reason = $"Forever map {ForeverPaladin.UIMapId} is unavailable";
            return false;
        }
        string cache = ForeverNavmesh.CacheDirectory(data, area.Continent);
        if (!Directory.Exists(cache))
        {
            reason = "baked Forever navmesh is unavailable";
            return false;
        }
        if (nav is null || navCache != cache)
        {
            nav?.Dispose();
            nav = ForeverNavmesh.OpenDiskOnly(logger, cache);
            navCache = cache;
        }
        return true;
    }

    private bool PlayerMapAgrees(VisualPositionSample map, Vector2 here)
    {
        if (!maps.TryGet(map.UIMapId, out WorldMapArea area))
            return false;
        Vector3 mapped = map.ToWorld(area);
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

    public void Dispose()
    {
        Halt();
        nav?.Dispose();
    }
}
