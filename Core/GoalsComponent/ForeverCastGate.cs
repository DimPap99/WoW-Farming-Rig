using Core.Database;
using Core.Forever;

using SharedLib;

using System;

namespace Core.Goals;

/// <summary>
/// One-capture preflight for the guarded Forever Paladin action set.
/// The controller must still be stopped at startup until its state flow has
/// a separate bounded live run.
/// </summary>
public static class ForeverCastGate
{
    public static bool CanPress(VisualBarSample sample, KeyAction action, SpellDB spells)
    {
        if (action.BaseAction || action.Item || action.HasForm ||
            action.BeforeCastDelay > 0 || action.AfterCastDelay > 0 ||
            action.BeforeCastFaceTarget || action.BeforeCastStop ||
            action.AfterCastWaitBuff || action.AfterCastWaitBag ||
            action.AfterCastWaitSwing || action.AfterCastWaitCastbar ||
            action.AfterCastWaitCombat || action.AfterCastWaitGCD ||
            action.AfterCastWaitMeleeRange || action.AfterCastAuraExpected ||
            !string.IsNullOrEmpty(action.MacroText) ||
            action.ConsoleKey == ConsoleKey.NoName ||
            action.Modifier != ModifierKey.None ||
            sample.PlayerIdentity is not { ClassId: (int)UnitClass.Paladin } ||
            sample.PlayerPowerType != ForeverFrame.ManaPowerType || sample.PlayerHealth <= 0 ||
            sample.TargetFlags is not { PlayerDead: false } flags ||
            flags.Present != sample.TargetPresent ||
            sample.Position is not { UIMapId: ForeverPaladin.UIMapId } ||
            sample.WorldPosition is not { InstanceMapId: 0 } ||
            sample.LastCastEvent is null ||
            sample.Bindings is null ||
            !IsActionReady(sample, action, spells))
            return false;

        if (action.Slot is < 1 or > 5 ||
            !MatchesTrialAction(action) ||
            !ForeverBindings.ActionButton(action.Slot, action.ConsoleKey, action.Modifier)
                .IsBoundIn(sample))
            return false;

        if (action.SpellId == ForeverPaladin.HolyLight)
            return sample.PlayerPower >= 0.12;

        if (action.SpellId == ForeverPaladin.SealOfRighteousness)
            return sample.SealOfRighteousnessActive == false &&
                sample.PlayerHealth >= 0.70 &&
                sample.PlayerPower >= 0.25;

        if (action.SpellId == ForeverPaladin.Judgement &&
            sample.SealOfRighteousnessActive != true)
            return false;
        bool resourcesSafe = flags.PlayerCombat
            ? sample.PlayerHealth > 0
            : sample.PlayerHealth >= 0.70 && sample.PlayerPower >= 0.60;
        return ForeverFrame.HasTargetIdentity(sample) &&
            sample.TargetIdentity is { IsPlayer: false } identity &&
            sample.PlayerIdentity is { } player &&
            identity.Level <= player.Level + 1 &&
            sample.TargetHealth > 0 &&
            flags is
            { Present: true, Attackable: true, Dead: false } &&
            resourcesSafe &&
            sample.ActionStates?.GetSlot(action.Slot) is { InRange: true };
    }

    /// <summary>
    /// The profile's spell is on its slot, learned, usable with enough mana and
    /// off cooldown. Every input comes from the same capture, so a press can
    /// never act on a spellbook, action state and cooldown from different frames.
    /// </summary>
    public static bool IsActionReady(VisualBarSample sample, KeyAction action, SpellDB spells)
    {
        if (action.Slot is < 1 or > VisualCooldownGridSnapshot.SlotCount ||
            sample.ActionBar is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } bar ||
            sample.ActionStates is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } states ||
            sample.Cooldowns is not
                { ReadableCount: VisualCooldownGridSnapshot.SlotCount } cooldowns ||
            sample.SpellBook is not { Count: > 0 } book ||
            book.ReadableCount != book.Count ||
            bar.GetSlot(action.Slot) is not
                { Kind: VisualActionKind.Spell, Id: > 0 } placed ||
            (action.SpellId > 0 && action.SpellId != placed.Id) ||
            !HasLearnedSpell(book, placed.Id) ||
            !spells.Spells.TryGetValue(placed.Id, out Spell spell) ||
            !string.Equals(spell.Name, action.Name,
                StringComparison.OrdinalIgnoreCase) ||
            states.GetSlot(action.Slot) is not { Usable: true, NoMana: false } ||
            cooldowns.GetSlot(action.Slot) is not { } fill)
            return false;

        // A pixel-width remnant is still a running timer. Require an empty
        // bar; no fraction-to-milliseconds estimate is made.
        return fill == 0;
    }

    // Flyout entries carry an action ID, not a spell ID, so only spell entries count.
    private static bool HasLearnedSpell(VisualSpellBookSnapshot book, int id)
    {
        for (int slot = 1; slot <= book.Count; slot++)
            if (book.GetSlot(slot) is
                { Source: VisualSpellIdSource.SpellId, Id: var learnedId } &&
                learnedId == id)
                return true;
        return false;
    }

    private static bool MatchesTrialAction(KeyAction action) =>
        (action.Slot, action.SpellId, action.Name) switch
        {
            (ForeverPaladin.HolyLightSlot, ForeverPaladin.HolyLight, "Holy Light") => true,
            (ForeverPaladin.JudgementSlot, ForeverPaladin.Judgement, "Judgement") => true,
            (ForeverPaladin.HolyStrikeSlot, ForeverPaladin.HolyStrikeRank1, "Holy Strike") => true,
            (ForeverPaladin.HolyStrikeSlot, ForeverPaladin.HolyStrikeRank2, "Holy Strike") => true,
            (ForeverPaladin.SealSlot, ForeverPaladin.SealOfRighteousness, "Seal of Righteousness") => true,
            _ => false
        };
}
