using Core.GOAP;

using SharedLib;

using System;
using System.Threading;

namespace Core.Forever;

/// <summary>Checks the live target and the game's targeting binding before input.</summary>
public sealed class ForeverTargetSelector
{
    private readonly ConfigurableInput input;
    private readonly CancellationToken token;
    private readonly ExecGameCommand command;

    public ForeverTargetSelector(ConfigurableInput input,
        ExecGameCommand command,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.input = input;
        this.command = command;
        token = cts.Token;
    }

    public static bool CanSelect(VisualBarSample bars) =>
        ForeverFrame.HasTrialInputs(bars) &&
        bars.TargetFlags is { Present: false, PlayerDead: false } flags &&
        ForeverFrame.CanOpenFight(bars, flags.PlayerCombat) &&
        ForeverBindings.TargetNearestEnemy.IsBoundIn(bars);

    public bool SelectNearest(VisualBarSample bars)
    {
        if (!CanSelect(bars))
            return false;
        PressTargetNearestEnemy();
        return true;
    }

    public bool CycleCandidate(VisualBarSample bars)
    {
        if (!ForeverFrame.HasTrialInputs(bars) ||
            bars.TargetFlags is not
                { Present: true, PlayerCombat: false, PlayerDead: false } ||
            !ForeverFrame.CanOpenFight(bars, inCombat: false) ||
            !ForeverBindings.TargetNearestEnemy.IsBoundIn(bars))
            return false;
        PressTargetNearestEnemy();
        return true;
    }

    public static bool TrySafeTarget(VisualBarSample bars,
        out VisualTargetIdentity identity) =>
        TryEligibleTarget(bars, out identity) &&
        ForeverFrame.CanOpenFight(bars, bars.TargetFlags!.Value.PlayerCombat);

    public static bool TryEligibleTarget(VisualBarSample bars,
        out VisualTargetIdentity identity)
    {
        identity = default;
        if (!ForeverFrame.HasTargetIdentity(bars) ||
            bars.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerDead: false } flags ||
            bars.TargetIdentity is not { Present: true, IsPlayer: false } target ||
            bars.PlayerIdentity is not { } player ||
            target.Level > player.Level + ForeverFrame.MaxTargetLevelsAbove ||
            bars.TargetHealth <= 0)
            return false;
        identity = target;
        return true;
    }

    public bool SelectNearbyOpponent(VisualBarSample bars)
    {
        if (bars.TargetFlags is not { PlayerCombat: true, PlayerDead: false } ||
            !ForeverBindings.TargetNearestEnemy.IsBoundIn(bars))
            return false;
        ClearTarget();
        PressTargetNearestEnemy();
        return true;
    }

    public void ClearTarget() => command.Run("/cleartarget");

    public void RestoreLastTarget() => command.Run("/targetlasttarget");

    private void PressTargetNearestEnemy()
    {
        ConsoleKey key = ForeverBindings.TargetNearestEnemy.Key;
        try { input.PressFixed(key, ForeverBinding.PressMs, token); }
        finally { input.SetKeyState(key, false, true); }
    }
}
