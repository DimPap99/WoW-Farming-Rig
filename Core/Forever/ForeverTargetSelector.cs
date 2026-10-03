using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;

using System;

namespace Core.Forever;

/// <summary>Checks the live target and the game's targeting binding before input.</summary>
public sealed class ForeverTargetSelector
{
    private readonly ConfigurableInput input;
    private readonly ExecGameCommand command;
    private readonly KeyAction? profileClearTarget;

    public ForeverTargetSelector(ConfigurableInput input,
        ExecGameCommand command, ClassConfiguration config)
    {
        this.input = input;
        this.command = command;
        profileClearTarget = ProfileClearTarget(config);
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
        return PressTargetNearestEnemy(bars);
    }

    public bool CycleCandidate(VisualBarSample bars)
    {
        if (!ForeverFrame.HasTrialInputs(bars) ||
            bars.TargetFlags is not
                { Present: true, PlayerCombat: false, PlayerDead: false } ||
            !ForeverFrame.CanOpenFight(bars, inCombat: false))
            return false;
        return PressTargetNearestEnemy(bars);
    }

    public static bool TrySafeTarget(VisualBarSample bars, int maxLevelsAbove,
        out VisualTargetIdentity identity) =>
        TryEligibleTarget(bars, maxLevelsAbove, out identity) &&
        ForeverFrame.CanOpenFight(bars, bars.TargetFlags!.Value.PlayerCombat);

    /// <param name="maxLevelsAbove">The profile's <see cref="ClassConfiguration.NPCMaxLevels_Above"/>.</param>
    public static bool TryEligibleTarget(VisualBarSample bars, int maxLevelsAbove,
        out VisualTargetIdentity identity)
    {
        identity = default;
        if (!ForeverFrame.HasTargetIdentity(bars) ||
            bars.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerDead: false } flags ||
            bars.TargetIdentity is not { Present: true, IsPlayer: false } target ||
            bars.PlayerIdentity is not { } player ||
            target.Level > player.Level + maxLevelsAbove ||
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
        ClearTarget(bars);
        return PressTargetNearestEnemy(bars);
    }

    /// <summary>
    /// Clears the target the way the player set the client up, and only while
    /// this frame shows one: with nothing selected Escape opens the game menu.
    /// Callers wait for the flags to follow before pressing again.
    /// </summary>
    public bool ClearTarget(VisualBarSample bars)
    {
        if (bars.TargetFlags is not { Present: true })
            return false;
        input.PressRandom(ClearTargetKey(bars));
        return true;
    }

    public void RestoreLastTarget() => command.Run("/targetlasttarget");

    /// <summary>
    /// The profile's <c>"ClearTarget": { "Key": ... }</c> when the client binds
    /// that key to a macro on the main bar, then upstream's clear-target
    /// binding when the client has one, then Escape.
    /// </summary>
    private KeyAction ClearTargetKey(VisualBarSample bars)
    {
        if (profileClearTarget is { } profile &&
            ForeverBindings.MainBarSlotBoundTo(bars, profile.ConsoleKey, profile.Modifier) is int slot &&
            bars.ActionBar?.GetSlot(slot) is { Kind: VisualActionKind.Macro })
            return profile;
        if (ForeverBindings.ClearTarget.TryGetKeyAction(bars, ClearTargetName, out KeyAction bound))
            return bound;
        return ForeverBinding.ToKeyAction(ClearTargetName, ConsoleKey.Escape, ModifierKey.None);
    }

    private const string ClearTargetName = "Forever Clear Target";

    // Upstream resolves ClearTarget from its binding default before its Key,
    // so the profile's Key string is read here on its own.
    private static KeyAction? ProfileClearTarget(ClassConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.ClearTarget.Key))
            return null;
        KeyAction key = new()
        {
            Name = ClearTargetName,
            Key = config.ClearTarget.Key,
            BaseAction = true,
            PressDuration = ForeverBinding.PressMs
        };
        return KeyReader.ResolveFromKeyString(NullLogger.Instance, key) ? key : null;
    }

    private bool PressTargetNearestEnemy(VisualBarSample bars)
    {
        if (!ForeverBindings.TargetNearestEnemy.TryGetKeyAction(bars, "Forever Target Nearest Enemy",
                out KeyAction tab))
            return false;
        input.PressRandom(tab);
        return true;
    }
}
