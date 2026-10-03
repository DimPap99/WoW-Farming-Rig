using Core.Database;
using Core.Forever;

using SharedLib;

using System;

namespace Core.Goals;

/// <summary>
/// One-capture preflight for a guarded Forever action from the class profile:
/// the profile's spell is ready on its main-bar slot and bound to the key the
/// profile names, on a verified setup. A targeted action also needs a living,
/// attackable, non-player target in the slot's range. When to use an action
/// is the profile's requirements; this gate only decides whether pressing it
/// now is safe.
/// </summary>
public static class ForeverCastGate
{
    /// <param name="selfCast">The action affects the player, so the target is not checked.</param>
    public static bool CanPress(VisualBarSample sample, KeyAction action, SpellDB spells,
        bool selfCast = false)
    {
        if (action.BaseAction || action.Item || action.HasForm ||
            action.BeforeCastDelay > 0 || action.AfterCastDelay > 0 ||
            action.BeforeCastFaceTarget || action.BeforeCastStop ||
            action.AfterCastWaitBuff || action.AfterCastWaitBag ||
            action.AfterCastWaitSwing || action.AfterCastWaitCastbar ||
            action.AfterCastWaitCombat || action.AfterCastWaitGCD ||
            action.AfterCastWaitMeleeRange ||
            action.ConsoleKey == ConsoleKey.NoName ||
            action.Modifier != ModifierKey.None ||
            !ForeverFrame.IsVerifiedSetup(sample) || sample.PlayerHealth <= 0 ||
            sample.TargetFlags is not { PlayerDead: false } flags ||
            !IsActionReady(sample, action, spells) ||
            !ForeverBindings.ActionButton(action.Slot, action.ConsoleKey, action.Modifier)
                .IsBoundIn(sample))
            return false;

        if (selfCast)
            return true;

        return ForeverFrame.HasTargetIdentity(sample) &&
            sample.TargetIdentity is { IsPlayer: false } identity &&
            sample.PlayerIdentity is { } player &&
            identity.Level <= player.Level + ForeverFrame.MaxTargetLevelsAbove &&
            sample.TargetHealth > 0 &&
            flags is { Present: true, Attackable: true, Dead: false } &&
            (flags.PlayerCombat || ForeverFrame.CanOpenFight(sample, inCombat: false)) &&
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
}
