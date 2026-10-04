using SharedLib;

using System;

namespace Core.Forever;

/// <summary>
/// One game command the Forever runtime uses: its index in the visual binding
/// grid and the command that index must hold. The key comes from the live
/// grid, the client's own key bindings, so rebinding a command in the game
/// needs no change here. The key is read from the same frame it is pressed on.
/// </summary>
public readonly record struct ForeverBinding(int Index, BindingID Command)
{
    /// <summary>Shortest key tap; each press adds up to 30 ms at random, like a person's.</summary>
    public const int PressMs = 60;

    /// <summary>The key the client binds to <see cref="Command"/>, the primary one first.</summary>
    public bool TryGetKey(VisualBarSample bars, out ConsoleKey key, out ModifierKey modifier)
    {
        key = ConsoleKey.NoName;
        modifier = ModifierKey.None;
        if (Decode(bars) is not { } decoded)
            return false;
        if (decoded.key1 != ConsoleKey.NoName)
        {
            (key, modifier) = (decoded.key1, decoded.mod1);
            return true;
        }
        if (decoded.key2 != ConsoleKey.NoName)
        {
            (key, modifier) = (decoded.key2, decoded.mod2);
            return true;
        }
        return false;
    }

    /// <summary>The client binds <see cref="Command"/> to some key.</summary>
    public bool IsBoundIn(VisualBarSample bars) => TryGetKey(bars, out _, out _);

    /// <summary>The client binds <see cref="Command"/> to this key, as primary or secondary.</summary>
    public bool IsBoundTo(VisualBarSample bars, ConsoleKey key,
        ModifierKey modifier = ModifierKey.None) =>
        Decode(bars) is { } decoded &&
        ((decoded.key1 == key && decoded.mod1 == modifier) ||
         (decoded.key2 == key && decoded.mod2 == modifier));

    /// <summary>A press of the client's key for <see cref="Command"/>.</summary>
    public bool TryGetKeyAction(VisualBarSample bars, string name, out KeyAction action)
    {
        action = null!;
        if (!TryGetKey(bars, out ConsoleKey key, out ModifierKey modifier))
            return false;
        action = ToKeyAction(name, key, modifier);
        return true;
    }

    public static KeyAction ToKeyAction(string name, ConsoleKey key, ModifierKey modifier) => new()
    {
        Name = name,
        BaseAction = true,
        ConsoleKey = key,
        Modifier = modifier,
        PressDuration = PressMs
    };

    private (BindingID bindingId, ConsoleKey key1, ModifierKey mod1, ConsoleKey key2, ModifierKey mod2)?
        Decode(VisualBarSample bars) =>
        bars.Bindings?.GetCode(Index) is > 0 and int code &&
        KeyReader.DecodeBinding(code) is { } decoded && decoded.bindingId == Command
            ? decoded : null;
}

/// <summary>
/// The game commands the guarded runtime uses. Each index is the command's
/// position in DataToColor's binding list, which the visual grid follows.
/// </summary>
public static class ForeverBindings
{
    public static readonly ForeverBinding Jump = new(1, BindingID.JUMP);
    public static readonly ForeverBinding MoveForward = new(2, BindingID.MOVEFORWARD);
    public static readonly ForeverBinding MoveBackward = new(3, BindingID.MOVEBACKWARD);
    public static readonly ForeverBinding StrafeLeft = new(4, BindingID.STRAFELEFT);
    public static readonly ForeverBinding StrafeRight = new(5, BindingID.STRAFERIGHT);
    public static readonly ForeverBinding StartAttack = new(15, BindingID.STARTATTACK);
    public static readonly ForeverBinding PetAttack = new(17, BindingID.PETATTACK);
    public static readonly ForeverBinding InteractTarget = new(18, BindingID.INTERACTTARGET);
    /// <summary>Upstream's clear-target button; bound only when its add-on is installed.</summary>
    public static readonly ForeverBinding ClearTarget = new(58, BindingID.CUSTOM_CLEARTARGET);

    /// <summary>Main action bar button <paramref name="slot"/>, 1 to 12.</summary>
    public static ForeverBinding ActionButton(int slot) =>
        new(20 + slot, BindingID.ACTIONBUTTON1 + slot - 1);

    /// <summary>The main-bar slot (1 to 12) the client binds to this key, or null.</summary>
    public static int? MainBarSlotBoundTo(VisualBarSample bars, ConsoleKey key, ModifierKey modifier)
    {
        for (int slot = 1; slot <= VisualCooldownGridSnapshot.SlotCount; slot++)
        {
            if (ActionButton(slot).IsBoundTo(bars, key, modifier))
                return slot;
        }
        return null;
    }
}
