using Core.Database;
using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using PPather.Navmesh;

using SharedLib;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>
/// One bounded controller run for the guarded Forever Paladin profile. A person
/// selects and finishes the target; automatic acquisition and sustained combat
/// are deliberately outside this trial.
/// </summary>
public sealed class ForeverTrialGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Initial, Stable, VerifyStep, AwaitTarget, AwaitRecovery, Passed, Failed }

    public override float Cost => 1;
    public override bool CanRun() => phase is not (Phase.Passed or Phase.Failed);
    public string TrialStatus => phase.ToString();
    public string LastFailure { get; private set; } = string.Empty;

    public bool TryReadOnlyRoutePreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) ||
            bars.TargetFlags is not { } flags ||
            !IdleForMove(bars, flags))
        {
            reason = "requires a complete Paladin Tirisfal frame, high health, mana, and no target or combat";
            return false;
        }
        try
        {
            return RouteReady(bars, out reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private readonly ILogger<ForeverTrialGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ConfigurableInput input;
    private readonly ForeverCaster caster;
    private readonly SpellDB spells;
    private readonly ClassConfiguration config;
    private readonly DataConfig data;
    private readonly WorldMapAreaDB maps;
    private readonly CancellationToken token;

    private volatile Phase phase = Phase.Initial;
    private long phaseStarted;
    private Vector2 origin;
    private double direction;
    private long firstFrameId;
    private long pressFrameId;
    private long candidateFrameId;
    private VisualTargetIdentity? candidateIdentity;
    private bool sawCombat;
    private bool sawDeadTarget;

    public ForeverTrialGoal(ILogger<ForeverTrialGoal> logger,
        ForeverVisualBarTracker tracker,
        ConfigurableInput input, ForeverCaster caster,
        SpellDB spells, ClassConfiguration config, DataConfig data,
        WorldMapAreaDB maps, CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverTrialGoal))
    {
        this.logger = logger;
        this.tracker = tracker;
        this.input = input;
        this.caster = caster;
        this.spells = spells;
        this.config = config;
        this.data = data;
        this.maps = maps;
        token = cts.Token;
        phaseStarted = GetTimestamp();
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is AbortEvent)
            Fail("Controller stopped");
    }

    public override void Update()
    {
        if (!CanRun() || token.IsCancellationRequested)
            return;

        try
        {
            if (!tracker.TryGetLatest(out VisualBarSample bars) ||
                !ForeverFrame.HasTrialInputs(bars))
            {
                if (phase == Phase.Initial && !TimedOut(10000))
                    return;
                Fail("Visual controller frame is missing or stale");
                return;
            }

            if (!ForeverFrame.IsVerifiedSetup(bars) ||
                bars.Position is not { } map ||
                bars.WorldPosition is not { } world ||
                bars.TargetFlags is not { PlayerDead: false } flags)
            {
                Fail("Trial map, class, or combat flags changed");
                return;
            }

            switch (phase)
            {
                case Phase.Initial:
                    if (!IdleForMove(bars, flags))
                    {
                        Fail("Route start requires high health, mana, and no target or combat");
                        return;
                    }
                    origin = new((float)world.X, (float)world.Y);
                    direction = map.Direction;
                    firstFrameId = bars.FrameId;
                    Enter(Phase.Stable);
                    break;

                case Phase.Stable:
                    if (bars.FrameId == firstFrameId && !TimedOut(5000))
                        return;
                    string reason = "player moved or resources changed";
                    if (TimedOut(5000) || !IdleForMove(bars, flags) ||
                        Vector2.Distance(origin, new((float)world.X, (float)world.Y)) > 0.3f ||
                        AngularDifference(direction, map.Direction) > 0.1 ||
                        input.ForwardKey != ForeverBindings.MoveForward.Key ||
                        !RouteReady(bars, out reason))
                    {
                        Fail($"Route preflight failed: {reason}");
                        return;
                    }

                    // A second full capture immediately preceded this one input.
                    // PressFixed releases the key; the finally is a belt-and-braces
                    // release if the process or cancellation interrupts the press.
                    pressFrameId = bars.FrameId;
                    ConsoleKey forwardKey = ForeverBindings.MoveForward.Key;
                    try
                    {
                        input.PressFixed(forwardKey, 200, token);
                    }
                    finally
                    {
                        input.SetKeyState(forwardKey, false, true);
                    }
                    Enter(Phase.VerifyStep);
                    break;

                case Phase.VerifyStep:
                    if (bars.FrameId == pressFrameId && !TimedOut(5000))
                        return;
                    if (!IdleForMove(bars, flags))
                    {
                        Fail("Combat or target changed during the route step");
                        return;
                    }
                    Vector2 delta = new((float)world.X - origin.X, (float)world.Y - origin.Y);
                    Vector2 forward = new(MathF.Cos((float)direction), MathF.Sin((float)direction));
                    float along = Vector2.Dot(delta, forward);
                    float lateral = MathF.Abs(delta.X * forward.Y - delta.Y * forward.X);
                    if (along >= 0.2f && along < 4f && lateral < 2f)
                    {
                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation("Forever route step verified: {Distance:F2} yd forward", along);
                        Enter(Phase.AwaitTarget);
                    }
                    else if (TimedOut(5000) || along >= 4f || lateral >= 2f)
                        Fail("Route step displacement was not verified");
                    break;

                case Phase.AwaitTarget:
                    if (TimedOut(90000))
                    {
                        Fail("No safe selected target within the trial window");
                        return;
                    }
                    if (bars.PlayerHealth < 0.70 || bars.PlayerPower < 0.25)
                    {
                        Fail("Low resources before the trial cast");
                        return;
                    }
                    if (flags is not { Present: true, Attackable: true, Dead: false } ||
                        bars.TargetHealth <= 0)
                    {
                        candidateIdentity = null;
                        return;
                    }
                    if (bars.TargetIdentity is not { } selected)
                        return;
                    if (candidateIdentity is null)
                    {
                        candidateIdentity = selected;
                        candidateFrameId = bars.FrameId;
                        return;
                    }
                    if (candidateIdentity != selected)
                    {
                        Fail("Selected target changed before the trial cast");
                        return;
                    }
                    if (bars.FrameId == candidateFrameId)
                        return;

                    KeyAction? judgement = config.Pull.Sequence.SingleOrDefault();
                    if (judgement is null ||
                        !ForeverCastGate.CanPress(bars, judgement, spells))
                        return;

                    if (caster.TryCast(judgement, () => token.IsCancellationRequested) !=
                        ForeverCastResult.Succeeded)
                    {
                        Fail("Judgement success event was not confirmed");
                        return;
                    }
                    logger.LogInformation("Forever trial Judgement confirmed");
                    sawCombat |= flags.PlayerCombat;
                    Enter(Phase.AwaitRecovery);
                    break;

                case Phase.AwaitRecovery:
                    if (TimedOut(90000))
                    {
                        Fail("Combat recovery was not verified within the trial window");
                        return;
                    }
                    sawCombat |= flags.PlayerCombat;
                    sawDeadTarget |= flags.Present && flags.Dead;
                    if ((sawCombat || sawDeadTarget) && !flags.Present &&
                        !flags.PlayerCombat && bars.PlayerHealth >= 0.70 &&
                        bars.PlayerPower >= 0.25)
                    {
                        Enter(Phase.Passed);
                        logger.LogInformation("Forever bounded route, action, and recovery trial passed");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forever bounded trial stopped");
            Fail("Trial preflight or controller update threw an exception");
        }
    }

    private bool RouteReady(VisualBarSample bars, out string reason)
    {
        reason = "unknown route condition";
        if (config.Paths is not { Length: 0 } ||
            config.Loot || config.UseMount ||
            bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !ForeverNavmesh.TryGetVerifiedArea(maps, out WorldMapArea area))
        {
            reason = "profile or map does not match the local trial";
            return false;
        }

        Vector2 here = new((float)world.X, (float)world.Y);
        Vector3 mapWorld = map.ToWorld(area);
        Vector2 forward = new(MathF.Cos((float)map.Direction), MathF.Sin((float)map.Direction));
        Vector2 ahead = here + forward * 3f;
        if (!ForeverBindings.MoveForward.IsBoundIn(bars) ||
            Vector2.Distance(here, new(mapWorld.X, mapWorld.Y)) > 1f)
        {
            reason = "position or forward binding is inconsistent";
            return false;
        }

        string cache = ForeverNavmesh.CacheDirectory(data, area.Continent);
        if (!Directory.Exists(cache))
        {
            reason = "baked Forever navmesh is missing";
            return false;
        }

        using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(logger, cache);
        if (!nav.TryGetHeight(here.X, here.Y, out float z))
        {
            reason = "current position is outside the baked mesh";
            return false;
        }

        if (!nav.TryGetHeight(ahead.X, ahead.Y, out float aheadZ) ||
            MathF.Abs(aheadZ - z) > 1.5f)
        {
            reason = "forward segment is outside the baked mesh";
            return false;
        }
        var step = nav.FindPath(new(here.X, here.Y, z),
            new(ahead.X, ahead.Y, aheadZ), null);
        if (step is null || step.locations.Count < 2 || nav.LastStats.TilesBaked != 0 ||
            PathLength(step.locations) > 3.6f ||
            Vector2.Distance(new(step.locations[0].X, step.locations[0].Y), here) > 0.5f ||
            Vector2.Distance(new(step.locations[^1].X, step.locations[^1].Y), ahead) > 0.5f)
        {
            reason = "forward step has no complete disk-only path";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static float PathLength(List<Vector3> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
            length += Vector2.Distance(new(points[i - 1].X, points[i - 1].Y),
                new(points[i].X, points[i].Y));
        return length;
    }

    private static bool IdleForMove(VisualBarSample bars, VisualTargetFlags flags) =>
        !flags.Present && !flags.PlayerCombat && !flags.PlayerDead &&
        bars.PlayerHealth >= 0.70 && bars.PlayerPower >= 0.60;

    private static double AngularDifference(double a, double b) =>
        Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

    private bool TimedOut(int milliseconds) =>
        GetElapsedTime(phaseStarted).TotalMilliseconds > milliseconds;

    private void Enter(Phase next)
    {
        phase = next;
        phaseStarted = GetTimestamp();
    }

    private void Fail(string reason)
    {
        if (phase is Phase.Passed or Phase.Failed)
            return;
        LastFailure = reason;
        Enter(Phase.Failed);
        logger.LogWarning("Forever bounded trial failed closed: {Reason}", reason);
    }
}
