using Game;

using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// One navigation step's outcome. <see cref="Blocked"/>, <see cref="Stuck"/>,
/// <see cref="TargetLost"/> and <see cref="OutOfReach"/> are about the unit or
/// the way to it, which another unit may not share; <see cref="Failed"/> is
/// about the run itself, such as lost focus or a changed binding.
/// </summary>
public enum ForeverNavigationResult
{
    Waiting, StepSent, StepVerified, NoTargetPosition,
    /// <summary>No walkable way toward the target from here.</summary>
    Blocked,
    /// <summary>Held movement did not move the character.</summary>
    Stuck,
    /// <summary>The target's marker stayed out of sight.</summary>
    TargetLost,
    /// <summary>The target stayed out of range for the whole local pursuit.</summary>
    OutOfReach,
    Failed
}

/// <summary>Short, checked local mesh steps from the current live position.</summary>
public sealed class ForeverNavigator : IForeverFacing, IDisposable
{
    // A person keeps running through a turn up to about a right angle; a
    // larger correction, or a way ahead that is not walkable, is turned standing.
    private const double MaxRunningTurn = Math.PI / 2;
    // The steering marker may drift this far from screen center while running.
    private const double MaxRunningMarkerError = 0.35;
    // A target whose marker is this close to screen centre is in front; once
    // in front it may drift to the wider band before the camera turns again,
    // so the camera does not twitch at the edge of the band.
    private const double FacedMarkerError = 0.10;
    private const double FacedMarkerRelease = 0.14;
    // Out of view, the target is looked for at full steering speed.
    private const double LookForTargetRate = 1.0;
    // Turning leads a moving marker by its screen velocity over this many
    // seconds, so a mob running across the view is kept up with, not chased.
    private const double MarkerLeadSeconds = 0.15;
    // Half the camera's horizontal field of view, the client's default 90
    // degrees; it turns a marker's screen position into a bearing.
    private const double HalfFieldOfView = Math.PI / 4;
    // Within this angle of the bearing a target was last seen at, the turn
    // toward it is done.
    private const double BearingReached = 0.15;
    // Idle hops come only while the walk is going well: the last progress is this recent.
    private const double WalkingWellSeconds = 0.5;

    private readonly ILogger<ForeverNavigator> logger;
    private readonly ConfigurableInput input;
    private readonly MouseCameraInput camera;
    private readonly WorldMapAreaDB maps;
    private readonly DataConfig data;

    // Movement is pressed on the controller thread, but a stop arrives on
    // another one. The lock orders the two, and once halted, an update that
    // was already running cannot press forward, strafe or mouse-look again.
    private readonly object inputLock = new();
    private bool halted;

    private NavmeshPathfinder? nav;
    private Vector2? roamOrigin;
    private Vector2? pursuitOrigin;
    private VisualTargetIdentity? pursuitTarget;
    private bool pursuitForwardHeld;
    private ConsoleKey? pursuitStrafeKey;
    private ConsoleKey? backwardKey;
    private Vector2 pursuitProgressPoint;
    private long pursuitProgressAt;
    // When to hop; what the current state allows decides whether.
    private readonly ForeverHops hops = new(Random.Shared);
    private long markerMissingAt;
    private string? navCache;
    private Vector2? detourWaypoint;
    private VisualTargetIdentity? facedTarget;
    private double? lastTargetMarkerX;
    private double lastMarkerError;
    private long lastMarkerAt;
    private double markerVelocity;
    private double? lastSeenBearing;
    private bool bearingReached;
    private bool inFront;
    private double? lookFacing;
    private double lookTurned;
    private string facingState = string.Empty;

    public ForeverNavigator(ILogger<ForeverNavigator> logger,
        ConfigurableInput input, MouseCameraInput camera,
        WorldMapAreaDB maps, DataConfig data)
    {
        this.logger = logger;
        this.input = input;
        this.camera = camera;
        this.maps = maps;
        this.data = data;
    }

    /// <summary>
    /// What the automaton's current state allows besides walking, such as
    /// hops. The goal sets it on every frame; until then nothing is allowed.
    /// </summary>
    public ForeverAllowed Allowed { get; set; }

    public bool TryReadOnlyPreflight(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !ForeverVerifiedSetup.IsVerifiedMap(map.UIMapId, world.InstanceMapId) ||
            !ForeverBindings.MoveForward.IsBoundTo(bars, input.ForwardKey))
        {
            reason = "position or movement bindings are incomplete";
            return false;
        }
        if (!TryNav(map.UIMapId, out reason))
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

    /// <summary>
    /// Walks on within 25 yards of where roaming began, held forward and
    /// mouse-steered onto a walkable heading. The caller stops it with
    /// <see cref="StopPursuit"/> on any frame it does not roam.
    /// </summary>
    public ForeverNavigationResult Roam(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !TryNav(map.UIMapId, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }
        Vector2 here = new((float)world.X, (float)world.Y);
        roamOrigin ??= here;

        double desired = map.Direction;
        if (Vector2.Distance(here, roamOrigin.Value) >= 25)
            desired = Bearing(here, roamOrigin.Value);
        if (!TrySafeHeading(here, (float)world.Z, desired,
                out desired, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }
        return Steer(bars, here, (float)world.Z, desired, 3, out reason);
    }

    /// <summary>
    /// Turns standing, as a person looking around: movement keys up, the mouse
    /// turning at <paramref name="rate"/> of full steering, positive to the right.
    /// </summary>
    public ForeverNavigationResult TurnInPlace(VisualBarSample bars, double rate, out string reason)
    {
        StopTranslation();
        return TurnTowardMarker(bars, rate, out reason);
    }

    /// <summary>
    /// Turns standing until the target's marker is near screen centre, leading
    /// a moving marker so a mob that runs across the view is kept in front. A
    /// target out of view is turned to the bearing it was last seen at, then
    /// looked for further round, for at most one full circle; after that it
    /// counts as in front, since nothing more can be learned by turning. When
    /// the game will not take the turn (it lost focus) the fight goes on
    /// unturned rather than stand still under attack.
    /// </summary>
    public bool FaceTarget(VisualBarSample bars)
    {
        // Nothing to face: no turning, and nothing waits on it.
        if (bars.TargetFlags is not { Present: true, Dead: false })
        {
            camera.StopTurning();
            return true;
        }
        TrackTarget(bars);
        ForeverNavigationResult turned;
        if (bars.TargetScreenPosition is { } marker)
        {
            double error = marker.X - 0.5;
            double rate = MarkerRate(error);
            double band = inFront ? FacedMarkerRelease : FacedMarkerError;
            if (Math.Abs(error) <= band && Math.Abs(rate) <= band)
            {
                inFront = true;
                SetFacingState("target in front");
                camera.StopTurning();
                return true;
            }
            inFront = false;
            SetFacingState("turning to the target's marker");
            StopTranslation();
            turned = TurnTowardMarker(bars, rate, out _);
        }
        else
        {
            inFront = false;
            if (!LookForTarget(bars, out turned))
                return true;
        }
        return turned == ForeverNavigationResult.Failed;
    }

    // Remembers, per target, where its marker was seen and how fast it moved
    // across the screen, and the bearing that puts it in front.
    private void TrackTarget(VisualBarSample bars)
    {
        if (bars.TargetIdentity != facedTarget)
        {
            facedTarget = bars.TargetIdentity;
            lastTargetMarkerX = null;
            lastSeenBearing = null;
            lastMarkerAt = 0;
            markerVelocity = 0;
            bearingReached = false;
            inFront = false;
            lookFacing = null;
            lookTurned = 0;
        }
        if (bars.TargetScreenPosition is not { } marker)
            return;

        double error = marker.X - 0.5;
        double seconds = lastMarkerAt == 0 ? 0 : GetElapsedTime(lastMarkerAt).TotalSeconds;
        // A gap in sight or a repeated frame gives no velocity.
        markerVelocity = seconds is > 0.01 and < 0.3
            ? 0.5 * markerVelocity + 0.5 * (error - lastMarkerError) / seconds
            : 0;
        lastMarkerError = error;
        lastMarkerAt = GetTimestamp();
        lastTargetMarkerX = marker.X;
        if (bars.Position is { } map)
            lastSeenBearing = BearingOf(map, marker);
        bearingReached = false;
        lookFacing = null;
        lookTurned = 0;
    }

    /// <summary>
    /// The world bearing to what a screen marker shows: the facing, turned by
    /// the marker's offset from the centre under the camera's field of view.
    /// Facing grows to the left, so a marker right of centre lies at a smaller bearing.
    /// </summary>
    public static double BearingOf(VisualPositionSample position, VisualScreenPointSample marker) =>
        position.Direction - Math.Atan((marker.X - 0.5) * 2 * Math.Tan(HalfFieldOfView));

    /// <summary>The world point <paramref name="yards"/> from <paramref name="from"/> along <paramref name="bearing"/>.</summary>
    public static Vector2 Along(Vector2 from, double bearing, float yards) => from + Heading(bearing) * yards;

    // How hard to turn for a marker this far from centre: the error plus a
    // lead for where the marker is heading, as a hand follows a moving mob.
    private double MarkerRate(double error) => error + MarkerLeadSeconds * markerVelocity;

    // Turns standing, as a person whips round after a mob that ran out of
    // view: first straight to the bearing it was last seen at, then on round
    // in the same direction. False, without turning, once a whole circle has
    // passed without seeing it.
    private bool LookForTarget(VisualBarSample bars, out ForeverNavigationResult turned)
    {
        if (bars.Position is { } map)
        {
            if (!bearingReached && lastSeenBearing is { } bearing)
            {
                double delta = SignedAngle(bearing - map.Direction);
                if (Math.Abs(delta) > BearingReached)
                {
                    SetFacingState("target out of view: turning to where it was last seen");
                    StopTranslation();
                    turned = TurnTowardMarker(bars, Math.Clamp(-delta / (Math.PI / 2), -1, 1), out _);
                    return true;
                }
                bearingReached = true;
            }
            if (lookFacing is { } previous)
                lookTurned += Math.Abs(SignedAngle(map.Direction - previous));
            lookFacing = map.Direction;
        }
        if (lookTurned >= 2 * Math.PI)
        {
            SetFacingState("target not found in a full circle");
            camera.StopTurning();
            turned = ForeverNavigationResult.Waiting;
            return false;
        }
        SetFacingState("target out of view: looking further round");
        StopTranslation();
        turned = TurnTowardMarker(bars, lastTargetMarkerX is < 0.5 ? -LookForTargetRate : LookForTargetRate, out _);
        return true;
    }

    private void SetFacingState(string state)
    {
        if (state == facingState)
            return;
        facingState = state;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever facing: {State}; last marker X {X:F2}, velocity {Velocity:F2}/s",
                state, lastTargetMarkerX, markerVelocity);
    }

    public ForeverNavigationResult Approach(VisualBarSample bars,
        VisualTargetIdentity target, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            bars.TargetIdentity != target || !TryNav(map.UIMapId, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }
        Vector2 here = new((float)world.X, (float)world.Y);

        // Without a target position the marker search takes over this frame.
        if (!TryTargetPoint(bars, out Vector2 destination))
            return ForeverNavigationResult.NoTargetPosition;
        if (Vector2.Distance(here, destination) > 45)
        {
            StopPursuit();
            reason = "target is too far for local pursuit";
            return ForeverNavigationResult.OutOfReach;
        }
        return WalkToward(bars, destination, out reason);
    }

    /// <summary>
    /// Walks toward a world point along the baked mesh: forward held while
    /// the mouse steers onto the path, as pursuit walks to a target's position.
    /// The caller decides when the point is reached and stops with
    /// <see cref="StopPursuit"/>.
    /// </summary>
    public ForeverNavigationResult WalkToward(VisualBarSample bars, Vector2 destination, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !TryNav(map.UIMapId, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }
        Vector2 here = new((float)world.X, (float)world.Y);
        if (!TryPathWaypoint(here, destination, out Vector2 waypoint, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Blocked;
        }
        // The way ahead is checked only as far as the waypoint: past it the
        // path bends, and a destination by an edge would stop the walk short.
        return Steer(bars, here, (float)world.Z, Bearing(here, waypoint),
            Math.Min(3, Vector2.Distance(here, waypoint)), out reason);
    }

    /// <summary>Whether the baked mesh covers the world point <paramref name="point"/> on the player's map.</summary>
    public bool IsOnMesh(VisualBarSample bars, Vector2 point) =>
        bars.Position is { } map && TryNav(map.UIMapId, out _) &&
        nav!.TryGetHeight(point.X, point.Y, out _);

    /// <summary>
    /// The whole way from the player to <paramref name="destination"/> along
    /// the baked mesh, as world X/Y points, when a complete path exists over
    /// tiles already on disk. A long way costs tens of milliseconds, so a
    /// traveller asks for it now and then and walks toward a point along it.
    /// </summary>
    public bool TryFindPath(VisualBarSample bars, Vector2 destination,
        out List<Vector2> path, out string reason)
    {
        path = [];
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !TryNav(map.UIMapId, out reason))
        {
            reason = "position or baked mesh is unavailable";
            return false;
        }
        Vector2 here = new((float)world.X, (float)world.Y);
        if (!nav!.TryGetHeight(here.X, here.Y, out float startZ))
        {
            reason = "the player is outside the baked mesh";
            return false;
        }
        if (!nav.TryGetHeight(destination.X, destination.Y, out float endZ))
        {
            reason = "the destination is outside the baked mesh";
            return false;
        }
        var found = nav.FindPath(new(here.X, here.Y, startZ),
            new(destination.X, destination.Y, endZ), null);
        if (found is not { locations.Count: >= 2 } || nav.LastStats.TilesBaked != 0 ||
            Vector2.Distance(new(found.locations[^1].X, found.locations[^1].Y), destination) > 1)
        {
            reason = "no complete disk-only path to the destination";
            return false;
        }
        foreach (Vector3 location in found.locations)
            path.Add(new(location.X, location.Y));
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Steps sideways, strafe held without forward, to whichever side has a
    /// walkable three yards, <paramref name="side"/> first (-1 left, +1
    /// right), as a person slides off a post or a fence they ran into. The
    /// caller decides how long and stops it with <see cref="StopPursuit"/>.
    /// </summary>
    public ForeverNavigationResult Sidestep(VisualBarSample bars, int side, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !TryNav(map.UIMapId, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }
        Vector2 here = new((float)world.X, (float)world.Y);
        foreach (int way in new[] { side, -side })
        {
            ForeverBinding binding = way < 0 ? ForeverBindings.StrafeLeft : ForeverBindings.StrafeRight;
            // A held strafe is a plain key-down: a modified binding cannot be held alone.
            if (!binding.TryGetKey(bars, out ConsoleKey key, out ModifierKey modifier) ||
                modifier != ModifierKey.None ||
                !HasShortPath(here, (float)world.Z, here + Heading(map.Direction - way * Math.PI / 2) * 3, out _))
                continue;
            camera.StopTurning();
            ReleaseBackward();
            SetPursuitMovement(here, false, key);
            return ForeverNavigationResult.StepSent;
        }
        reason = "no walkable side to step to";
        return ForeverNavigationResult.Blocked;
    }

    /// <summary>
    /// Backs away, move-backward held, while the camera turns at
    /// <paramref name="rate"/> of full steering (positive to the right), as a
    /// person backs off an obstacle and turns away from it. The caller decides
    /// how long and stops it with <see cref="StopPursuit"/>.
    /// </summary>
    public ForeverNavigationResult BackOut(VisualBarSample bars, double rate, out string reason)
    {
        reason = string.Empty;
        if (!ForeverBindings.MoveBackward.TryGetKey(bars, out ConsoleKey key, out ModifierKey modifier) ||
            modifier != ModifierKey.None)
        {
            reason = "move backward is not bound to a plain key";
            return ForeverNavigationResult.Blocked;
        }
        // Acquire foreground before key-down, as the walk does; a turn that has
        // to wait for a clear place to start holds the step too.
        ForeverNavigationResult turning = TurnTowardMarker(bars, rate, out reason);
        if (turning is ForeverNavigationResult.Failed or ForeverNavigationResult.Waiting)
        {
            StopTranslation();
            return turning;
        }
        lock (inputLock)
        {
            if (halted)
            {
                reason = "controller stopped";
                return ForeverNavigationResult.Failed;
            }
            if (pursuitForwardHeld || pursuitStrafeKey is not null)
                StopTranslation();
            if (backwardKey != key)
            {
                ReleaseBackward();
                input.SetKeyState(key, true, false);
                backwardKey = key;
            }
        }
        return ForeverNavigationResult.StepSent;
    }

    public ForeverNavigationResult SearchForTarget(VisualBarSample bars,
        VisualTargetIdentity target, out string reason)
    {
        reason = string.Empty;
        if (bars.Position is not { } map ||
            bars.WorldPosition is not { } world ||
            bars.TargetIdentity != target || !TryNav(map.UIMapId, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Failed;
        }

        Vector2 here = new((float)world.X, (float)world.Y);
        TrackTarget(bars);
        if (pursuitTarget != target)
        {
            StopPursuit();
            markerMissingAt = 0;
            pursuitTarget = target;
            pursuitOrigin = here;
            detourWaypoint = null;
        }
        if (Vector2.Distance(here, pursuitOrigin!.Value) >= 35)
        {
            StopPursuit();
            reason = "target stayed out of range after 35 yards of local search";
            return ForeverNavigationResult.OutOfReach;
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
                return ForeverNavigationResult.TargetLost;
            }
            if (missingSeconds < 0.3)
                return ForeverNavigationResult.Waiting;
            LookForTarget(bars, out ForeverNavigationResult turned);
            return turned;
        }
        markerMissingAt = 0;

        // Keep running while the mouse steers the marker toward the center.
        // Only a marker far to the side is turned toward standing; turning in
        // place needs no walkable segment in the old direction, so a wall
        // beside us cannot prevent facing a clear path.
        double horizontalError = screen.X - 0.5;
        if (Math.Abs(horizontalError) > MaxRunningMarkerError)
        {
            StopTranslation();
            return TurnTowardMarker(bars, MarkerRate(horizontalError), out reason);
        }

        if ((pursuitForwardHeld || pursuitStrafeKey is not null) &&
            !MovementProgressed(bars, here, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Stuck;
        }

        if (!ForeverBindings.MoveForward.IsBoundTo(bars, input.ForwardKey))
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
                    // A held strafe is a plain key-down: a modified binding cannot be held alone.
                    if (binding.TryGetKey(bars, out ConsoleKey key, out ModifierKey modifier) &&
                        modifier == ModifierKey.None &&
                        HasShortPath(here, (float)world.Z,
                            here + Heading(map.Direction - side * angle) * 3, out _))
                        return key;
                }
                return null;
            }
        }

        // Acquire foreground before key-down: WoW releases movement keys on
        // focus changes, so sending forward first can leave only our held flag.
        // A turn that has to wait for a clear place to start holds the step too.
        ForeverNavigationResult turning = TurnTowardMarker(bars, MarkerRate(horizontalError), out reason);
        if (turning is ForeverNavigationResult.Failed or ForeverNavigationResult.Waiting)
        {
            StopTranslation();
            return turning;
        }
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
        double desired = Bearing(here, waypoint);
        double delta = SignedAngle(desired - bars.Position!.Value.Direction);
        // Once facing the waypoint, a way that is still not walkable means the
        // detour is blocked; until then keep turning toward it.
        if (Math.Abs(delta) <= 0.20 &&
            !HasShortPath(here, (float)bars.WorldPosition!.Value.Z,
                here + Heading(bars.Position.Value.Direction) * Math.Min(3, distance), out reason))
        {
            StopPursuit();
            detourWaypoint = null;
            return ForeverNavigationResult.Blocked;
        }
        return Steer(bars, here, (float)bars.WorldPosition!.Value.Z, desired,
            Math.Min(3, distance), out reason);
    }

    /// <summary>
    /// Walks toward <paramref name="desired"/> the way a person does: forward
    /// held while the mouse steers, re-checking every frame that the next
    /// <paramref name="lookAhead"/> yards along the current facing are walkable.
    /// A correction beyond <see cref="MaxRunningTurn"/>, or a way ahead that is
    /// not walkable, is turned standing.
    /// </summary>
    private ForeverNavigationResult Steer(VisualBarSample bars, Vector2 here,
        float z, double desired, float lookAhead, out string reason)
    {
        reason = string.Empty;
        double direction = bars.Position!.Value.Direction;
        double delta = SignedAngle(desired - direction);
        // The mouse turns right for a positive error; a positive bearing
        // change is to the left.
        double steering = -delta / Math.PI;
        if (Math.Abs(delta) > MaxRunningTurn ||
            !HasShortPath(here, z, here + Heading(direction) * lookAhead, out _))
        {
            StopTranslation();
            return TurnTowardMarker(bars, steering, out reason);
        }
        if (!ForeverBindings.MoveForward.IsBoundTo(bars, input.ForwardKey))
        {
            StopPursuit();
            reason = "forward binding changed";
            return ForeverNavigationResult.Failed;
        }
        if (pursuitForwardHeld && !MovementProgressed(bars, here, out reason))
        {
            StopPursuit();
            return ForeverNavigationResult.Stuck;
        }
        // Acquire foreground before key-down: WoW releases movement keys on
        // focus changes, so sending forward first can leave only our held flag.
        // A turn that has to wait for a clear place to start holds the step too.
        ForeverNavigationResult turning = TurnTowardMarker(bars, steering, out reason);
        if (turning is ForeverNavigationResult.Failed or ForeverNavigationResult.Waiting)
        {
            StopTranslation();
            return turning;
        }
        bool starting = !pursuitForwardHeld;
        SetPursuitMovement(here, true, null);
        // Where the state allows it, a hop or a few now and then while the
        // walk is going well, as a person does for no reason.
        if (!starting && Allowed.HasFlag(ForeverAllowed.IdleHops) &&
            GetElapsedTime(pursuitProgressAt).TotalSeconds < WalkingWellSeconds &&
            hops.Idle(GetTimestamp()))
            Hop(bars, "idle");
        return starting ? ForeverNavigationResult.StepSent : ForeverNavigationResult.StepVerified;
    }

    // Held movement must cover 0.2 yards every 3 seconds; anything less means
    // the character is stuck, or the key never reached the game. Where the
    // state allows it, a stall hops once on the way, which clears a fence or
    // a rock the mesh lacks.
    private bool MovementProgressed(VisualBarSample bars, Vector2 here, out string reason)
    {
        reason = string.Empty;
        if (Vector2.Distance(here, pursuitProgressPoint) >= 0.20f)
        {
            pursuitProgressPoint = here;
            pursuitProgressAt = GetTimestamp();
            hops.Progressed();
            return true;
        }
        double stalled = GetElapsedTime(pursuitProgressAt).TotalSeconds;
        if (Allowed.HasFlag(ForeverAllowed.SnagHop) && hops.Snag(stalled))
            Hop(bars, "held movement stalled");
        if (stalled <= 3)
            return true;
        reason = "held forward movement was not verified";
        return false;
    }

    // One tap of the client's jump key, unless the navigator was halted.
    private void Hop(VisualBarSample bars, string why)
    {
        lock (inputLock)
        {
            if (halted || !ForeverBindings.Jump.TryGetKeyAction(bars, "Forever Jump", out KeyAction jump))
                return;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever hop: {Why}", why);
            input.PressRandom(jump);
        }
    }

    // A new hold presses the right button on empty ground, away from every
    // nameplate and the unit under it: a press there would target that unit.
    // With nowhere clear on this frame, the turn waits for the next.
    private ForeverNavigationResult TurnTowardMarker(VisualBarSample bars, double horizontalError,
        out string reason)
    {
        reason = string.Empty;
        lock (inputLock)
        {
            if (halted)
            {
                reason = "controller stopped";
                return ForeverNavigationResult.Failed;
            }
            (double X, double Y) press = (0.5, 2.0 / 3);
            if (!camera.IsHeld)
            {
                if (ForeverDragPoint.Choose(bars) is not { } clear)
                {
                    reason = "no clear place to start turning";
                    return ForeverNavigationResult.Waiting;
                }
                press = clear;
            }
            if (!camera.Turn(horizontalError, press.X, press.Y))
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
            // Forward or a strafe never runs against a held move-backward.
            if (!halted)
                ReleaseBackward();
            if (halted ||
                (pursuitForwardHeld == forward && pursuitStrafeKey == strafe))
                return;
            if (pursuitStrafeKey is { } oldStrafe)
                input.SetKeyState(oldStrafe, false, true);
            if (pursuitForwardHeld && !forward)
            {
                input.StopForward(true);
                hops.Stopped();
            }
            if (forward && !pursuitForwardHeld)
            {
                input.StartForward(false);
                hops.Started(GetTimestamp());
            }
            if (strafe is { } newStrafe)
                input.SetKeyState(newStrafe, true, false);
            pursuitForwardHeld = forward;
            pursuitStrafeKey = strafe;
            pursuitProgressPoint = here;
            pursuitProgressAt = GetTimestamp();
            hops.Progressed();
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
            ReleaseBackward();
            hops.Stopped();
        }
    }

    private void ReleaseBackward()
    {
        lock (inputLock)
        {
            if (backwardKey is { } held)
                input.SetKeyState(held, false, true);
            backwardKey = null;
        }
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

    private bool TryNav(int uiMapId, out string reason)
    {
        reason = string.Empty;
        if (!ForeverNavmesh.TryGetVerifiedArea(maps, uiMapId, out WorldMapArea area))
        {
            reason = $"Forever map {uiMapId} is not verified";
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

    // The session container disposes this once per registration (as itself
    // and as IForeverFacing), so a second call does nothing.
    public void Dispose()
    {
        Halt();
        nav?.Dispose();
        nav = null;
    }
}
