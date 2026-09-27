using System;
using Game;
using SharedLib;

namespace Core.Goals;

/// <summary>
/// One-capture preflight for the guarded Forever Paladin action set.
/// The controller must still be stopped at startup until its state flow has
/// a separate bounded live run.
/// </summary>
public static class ForeverCastGate
{
    public static bool CanPress(VisualBarSample sample, KeyAction action,
        SpellBookReader spellBook)
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
            sample.PlayerPowerType != 0 || sample.PlayerHealth <= 0 ||
            sample.TargetFlags is not { PlayerDead: false } flags ||
            flags.Present != sample.TargetPresent ||
            sample.Position is not { UIMapId: 1420 } ||
            sample.WorldPosition is not { InstanceMapId: 0 } ||
            sample.LastCastEvent is null ||
            sample.Bindings is not { } bindings ||
            !ActionBarCooldownReader.IsForeverReady(sample, action, spellBook))
            return false;

        if (action.Slot is < 1 or > 5 ||
            !MatchesTrialAction(action))
            return false;

        int? code = bindings.GetCode(20 + action.Slot);
        if (code is not > 0 ||
            KeyReader.DecodeBinding(code.Value) is not { } binding ||
            binding.bindingId != BindingID.ACTIONBUTTON1 + action.Slot - 1 ||
            !((binding.key1 == action.ConsoleKey &&
                binding.mod1 == action.Modifier) ||
               (binding.key2 == action.ConsoleKey &&
                binding.mod2 == action.Modifier)))
            return false;

        if (action.SpellId == 635)
            return sample.PlayerPower >= 0.12;

        if (action.SpellId == 20287)
            return sample.SealOfRighteousnessActive == false &&
                sample.PlayerHealth >= 0.70 &&
                sample.PlayerPower >= 0.25;

        if (action.SpellId == 20271 && sample.SealOfRighteousnessActive != true)
            return false;
        bool resourcesSafe = flags.PlayerCombat
            ? sample.PlayerHealth > 0
            : sample.PlayerHealth >= 0.70 && sample.PlayerPower >= 0.60;
        return AddonReader.HasForeverTargetIdentity(sample) &&
            sample.TargetIdentity is { IsPlayer: false } identity &&
            sample.PlayerIdentity is { } player &&
            identity.Level <= player.Level + 1 &&
            sample.TargetHealth > 0 &&
            flags is
            { Present: true, Attackable: true, Dead: false } &&
            resourcesSafe &&
            sample.ActionStates?.GetSlot(action.Slot) is { InRange: true };
    }

    private static bool MatchesTrialAction(KeyAction action) =>
        (action.Slot, action.SpellId, action.Name) switch
        {
            (1, 635, "Holy Light") => true,
            (2, 20271, "Judgement") => true,
            (3, 679, "Holy Strike") => true,
            (3, 678, "Holy Strike") => true,
            (5, 20287, "Seal of Righteousness") => true,
            _ => false
        };
}
