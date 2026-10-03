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

namespace Core.Goals;

/// <summary>
/// One bounded controller run for a verified Forever trial profile. A person
/// selects and finishes the target; automatic acquisition and sustained combat
/// are deliberately outside this trial.
/// </summary>
public sealed class ForeverTrialGoal : GoapGoal, IGoapEventListener
{
    private enum Phase { Initial, Stable, VerifyStep, AwaitTarget, AwaitRecovery, Passed, Failed }

    public override float Cost => 1;
    public override bool CanRun() => !machine.IsTerminal;
    public string TrialStatus => machine.State.ToString();
    public string LastFailure => machine.LastFailure;

    public bool TryReadOnlyRoutePreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) ||
            bars.TargetFlags is not { } flags ||
            !IdleForMove(bars, flags))
        {
            reason = "requires a complete verified-setup frame, high health, mana, and no target or combat";
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
    private readonly ForeverStateMachine<Phase> machine;

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

        machine = new ForeverStateMachine<Phase>(logger, "Forever bounded trial",
                Phase.Initial, Phase.Failed, Phase.Passed)
            .On(Phase.Initial, Initial, exits: [Phase.Stable])
            .On(Phase.Stable, Stable, exits: [Phase.VerifyStep])
            .On(Phase.VerifyStep, VerifyStep, exits: [Phase.AwaitTarget])
            .On(Phase.AwaitTarget, AwaitTarget, exits: [Phase.AwaitRecovery],
                timeoutMs: 90000, onTimeout: "No safe selected target within the trial window")
            .On(Phase.AwaitRecovery, AwaitRecovery, exits: [Phase.Passed],
                timeoutMs: 90000, onTimeout: "Combat recovery was not verified within the trial window");
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is AbortEvent)
            machine.Fail("Controller stopped");
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
                if (machine.State == Phase.Initial && machine.ElapsedMs <= 10000)
                    return;
                machine.Fail("Visual controller frame is missing or stale");
                return;
            }

            if (!ForeverFrame.IsVerifiedSetup(bars) ||
                bars.Position is null || bars.WorldPosition is null ||
                bars.TargetFlags is not { PlayerDead: false })
            {
                machine.Fail("Trial map, class, or combat flags changed");
                return;
            }

            machine.Tick(bars);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forever bounded trial stopped");
            machine.Fail("Trial preflight or controller update threw an exception");
        }
    }

    private void Initial(VisualBarSample bars)
    {
        if (!IdleForMove(bars, bars.TargetFlags!.Value))
        {
            machine.Fail("Route start requires high health, mana, and no target or combat");
            return;
        }
        VisualWorldPositionSample world = bars.WorldPosition!.Value;
        origin = new((float)world.X, (float)world.Y);
        direction = bars.Position!.Value.Direction;
        firstFrameId = bars.FrameId;
        machine.Go(Phase.Stable);
    }

    private void Stable(VisualBarSample bars)
    {
        bool timedOut = machine.ElapsedMs > 5000;
        if (bars.FrameId == firstFrameId && !timedOut)
            return;
        VisualWorldPositionSample world = bars.WorldPosition!.Value;
        string reason = "player moved or resources changed";
        if (timedOut || !IdleForMove(bars, bars.TargetFlags!.Value) ||
            Vector2.Distance(origin, new((float)world.X, (float)world.Y)) > 0.3f ||
            AngularDifference(direction, bars.Position!.Value.Direction) > 0.1 ||
            !ForeverBindings.MoveForward.IsBoundTo(bars, input.ForwardKey) ||
            !RouteReady(bars, out reason))
        {
            machine.Fail($"Route preflight failed: {reason}");
            return;
        }

        // A second full capture immediately preceded this one input.
        // PressFixed releases the key; the finally is a belt-and-braces
        // release if the process or cancellation interrupts the press.
        pressFrameId = bars.FrameId;
        ConsoleKey forwardKey = input.ForwardKey;
        try
        {
            input.PressFixed(forwardKey, 200, token);
        }
        finally
        {
            input.SetKeyState(forwardKey, false, true);
        }
        machine.Go(Phase.VerifyStep);
    }

    private void VerifyStep(VisualBarSample bars)
    {
        bool timedOut = machine.ElapsedMs > 5000;
        if (bars.FrameId == pressFrameId && !timedOut)
            return;
        if (!IdleForMove(bars, bars.TargetFlags!.Value))
        {
            machine.Fail("Combat or target changed during the route step");
            return;
        }
        VisualWorldPositionSample world = bars.WorldPosition!.Value;
        Vector2 delta = new((float)world.X - origin.X, (float)world.Y - origin.Y);
        Vector2 forward = new(MathF.Cos((float)direction), MathF.Sin((float)direction));
        float along = Vector2.Dot(delta, forward);
        float lateral = MathF.Abs(delta.X * forward.Y - delta.Y * forward.X);
        if (along >= 0.2f && along < 4f && lateral < 2f)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever route step verified: {Distance:F2} yd forward", along);
            machine.Go(Phase.AwaitTarget);
        }
        else if (timedOut || along >= 4f || lateral >= 2f)
            machine.Fail("Route step displacement was not verified");
    }

    private void AwaitTarget(VisualBarSample bars)
    {
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (!ForeverFrame.CanOpenFight(bars, inCombat: true))
        {
            machine.Fail("Low resources before the trial cast");
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
            machine.Fail("Selected target changed before the trial cast");
            return;
        }
        if (bars.FrameId == candidateFrameId)
            return;

        KeyAction? opener = config.Pull.Sequence.SingleOrDefault();
        if (opener is null ||
            !ForeverCastGate.CanPress(bars, opener, spells,
                maxLevelsAbove: config.NPCMaxLevels_Above))
            return;

        if (caster.TryCast(opener, () => token.IsCancellationRequested) !=
            ForeverCastResult.Succeeded)
        {
            machine.Fail($"{opener.Name} success event was not confirmed");
            return;
        }
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever trial {Action} confirmed", opener.Name);
        sawCombat |= flags.PlayerCombat;
        machine.Go(Phase.AwaitRecovery);
    }

    private void AwaitRecovery(VisualBarSample bars)
    {
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        sawCombat |= flags.PlayerCombat;
        sawDeadTarget |= flags.Present && flags.Dead;
        if ((sawCombat || sawDeadTarget) && !flags.Present &&
            !flags.PlayerCombat && ForeverFrame.CanOpenFight(bars, inCombat: true))
        {
            machine.Go(Phase.Passed);
            logger.LogInformation("Forever bounded route, action, and recovery trial passed");
        }
    }

    private bool RouteReady(VisualBarSample bars, out string reason)
    {
        reason = "unknown route condition";
        if (config.Paths is not { Length: 0 } ||
            config.Loot || config.UseMount ||
            bars.Position is not { } map || bars.WorldPosition is not { } world ||
            !ForeverNavmesh.TryGetVerifiedArea(maps, map.UIMapId, out WorldMapArea area))
        {
            reason = "profile or map does not match the local trial";
            return false;
        }

        Vector2 here = new((float)world.X, (float)world.Y);
        Vector3 mapWorld = map.ToWorld(area);
        Vector2 forward = new(MathF.Cos((float)map.Direction), MathF.Sin((float)map.Direction));
        Vector2 ahead = here + forward * 3f;
        if (!ForeverBindings.MoveForward.IsBoundTo(bars, input.ForwardKey) ||
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
        ForeverFrame.CanOpenFight(bars, inCombat: false);

    private static double AngularDifference(double a, double b) =>
        Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));
}
