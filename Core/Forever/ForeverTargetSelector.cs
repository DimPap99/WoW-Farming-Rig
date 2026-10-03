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
        bars.PlayerHealth >= 0.70 &&
        bars.PlayerPower >= (flags.PlayerCombat ? 0.25 : 0.60) &&
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
            bars.PlayerHealth < 0.70 || bars.PlayerPower < 0.60 ||
            !ForeverBindings.TargetNearestEnemy.IsBoundIn(bars))
            return false;
        PressTargetNearestEnemy();
        return true;
    }

    public static bool TrySafeTarget(VisualBarSample bars,
        out VisualTargetIdentity identity) =>
        TryEligibleTarget(bars, out identity) && bars.PlayerHealth >= 0.70 &&
        bars.PlayerPower >= (bars.TargetFlags!.Value.PlayerCombat ? 0.25 : 0.60);

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
            target.Level > player.Level + 1 ||
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

    public static bool InJudgementRange(VisualBarSample bars) =>
        bars.ActionBar?.GetSlot(ForeverPaladin.JudgementSlot) is
            { Kind: VisualActionKind.Spell, Id: ForeverPaladin.Judgement } &&
        bars.ActionStates?.GetSlot(ForeverPaladin.JudgementSlot) is { InRange: true };

    public static bool InStrikeRange(VisualBarSample bars) =>
        bars.ActionBar?.GetSlot(ForeverPaladin.HolyStrikeSlot) is
            { Kind: VisualActionKind.Spell,
              Id: ForeverPaladin.HolyStrikeRank1 or ForeverPaladin.HolyStrikeRank2 } &&
        bars.ActionStates?.GetSlot(ForeverPaladin.HolyStrikeSlot) is { InRange: true };

    public void ClearTarget() => command.Run("/cleartarget");

    public void RestoreLastTarget() => command.Run("/targetlasttarget");

    private void PressTargetNearestEnemy()
    {
        ConsoleKey key = ForeverBindings.TargetNearestEnemy.Key;
        try { input.PressFixed(key, ForeverBinding.PressMs, token); }
        finally { input.SetKeyState(key, false, true); }
    }
}
