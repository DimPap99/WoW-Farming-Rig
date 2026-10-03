using SharedLib;

using System;

namespace Core.Forever;

/// <summary>
/// One key binding the Forever runtime relies on: its index in the visual
/// binding grid, the command, and the key that command must be bound to.
/// The same value is checked against the live grid and then pressed, so the
/// verified key and the pressed key cannot drift apart.
/// </summary>
public readonly record struct ForeverBinding(int Index, BindingID Command,
    ConsoleKey Key, ModifierKey Modifier = ModifierKey.None)
{
    /// <summary>Shortest key tap; each press adds up to 30 ms at random, like a person's.</summary>
    public const int PressMs = 60;

    /// <summary>True when the live grid binds <see cref="Command"/> to this key, as primary or secondary.</summary>
    public bool IsBoundIn(VisualBarSample bars)
    {
        int? code = bars.Bindings?.GetCode(Index);
        if (code is not > 0 ||
            KeyReader.DecodeBinding(code.Value) is not { } decoded ||
            decoded.bindingId != Command)
            return false;
        return (decoded.key1 == Key && decoded.mod1 == Modifier) ||
            (decoded.key2 == Key && decoded.mod2 == Modifier);
    }

    public KeyAction ToKeyAction(string name) => new()
    {
        Name = name,
        BaseAction = true,
        ConsoleKey = Key,
        Modifier = Modifier,
        PressDuration = PressMs
    };
}

/// <summary>
/// The bindings the guarded runtime expects. Each index is the command's
/// position in DataToColor's binding list, which the visual grid follows.
/// </summary>
public static class ForeverBindings
{
    public static readonly ForeverBinding MoveForward =
        new(2, BindingID.MOVEFORWARD, ConsoleKey.UpArrow);
    public static readonly ForeverBinding StrafeLeft =
        new(4, BindingID.STRAFELEFT, ConsoleKey.A);
    public static readonly ForeverBinding StrafeRight =
        new(5, BindingID.STRAFERIGHT, ConsoleKey.D);
    public static readonly ForeverBinding TargetNearestEnemy =
        new(9, BindingID.TARGETNEARESTENEMY, ConsoleKey.Tab);
    public static readonly ForeverBinding StartAttack =
        new(15, BindingID.STARTATTACK, ConsoleKey.D2, ModifierKey.Shift);
    public static readonly ForeverBinding InteractTarget =
        new(18, BindingID.INTERACTTARGET, ConsoleKey.D3, ModifierKey.Shift);

    // Turning uses whatever keys the class profile configures.
    public static ForeverBinding TurnLeft(ConsoleKey key) =>
        new(6, BindingID.TURNLEFT, key);
    public static ForeverBinding TurnRight(ConsoleKey key) =>
        new(7, BindingID.TURNRIGHT, key);

    /// <summary>Main action bar button <paramref name="slot"/>, 1 to 12.</summary>
    public static ForeverBinding ActionButton(int slot, ConsoleKey key,
        ModifierKey modifier = ModifierKey.None) =>
        new(20 + slot, BindingID.ACTIONBUTTON1 + slot - 1, key, modifier);
}
