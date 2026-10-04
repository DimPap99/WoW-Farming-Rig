namespace Frontend;

/// <summary>
/// The keys the Key Bindings page draws: the rows of a US keyboard, the console
/// key each label stands for, and the action bar slot a key presses by default.
/// </summary>
public static class KeyboardKeys
{
    public static readonly string[] NumberRow = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="];
    public static readonly string[] QwertyRow = ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\"];
    public static readonly string[] AsdfRow = ["A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'"];
    public static readonly string[] ZxcvRow = ["Z", "X", "C", "V", "B", "N", "M", ",", ".", "/"];

    /// <summary>The first slot of the bar the function keys press (F1 is 61).</summary>
    public const int FunctionKeySlotBase = 60;

    /// <summary>The console key a key cap's label stands for; NoName for none.</summary>
    public static ConsoleKey ConsoleKeyOf(string label)
    {
        return label switch
        {
            "1" => ConsoleKey.D1, "2" => ConsoleKey.D2, "3" => ConsoleKey.D3,
            "4" => ConsoleKey.D4, "5" => ConsoleKey.D5, "6" => ConsoleKey.D6,
            "7" => ConsoleKey.D7, "8" => ConsoleKey.D8, "9" => ConsoleKey.D9,
            "0" => ConsoleKey.D0, "-" => ConsoleKey.OemMinus, "=" => ConsoleKey.OemPlus,
            "F1" => ConsoleKey.F1, "F2" => ConsoleKey.F2, "F3" => ConsoleKey.F3,
            "F4" => ConsoleKey.F4, "F5" => ConsoleKey.F5, "F6" => ConsoleKey.F6,
            "F7" => ConsoleKey.F7, "F8" => ConsoleKey.F8, "F9" => ConsoleKey.F9,
            "F10" => ConsoleKey.F10, "F11" => ConsoleKey.F11, "F12" => ConsoleKey.F12,
            _ when label.Length == 1 && char.IsLetter(label[0]) =>
                Enum.Parse<ConsoleKey>(label.ToUpperInvariant()),
            "[" => ConsoleKey.Oem4, "]" => ConsoleKey.Oem6, "\\" => ConsoleKey.Oem5,
            ";" => ConsoleKey.Oem1, "'" => ConsoleKey.Oem7,
            "," => ConsoleKey.OemComma, "." => ConsoleKey.OemPeriod, "/" => ConsoleKey.Oem2,
            _ => ConsoleKey.NoName
        };
    }

    public static ConsoleKey NumpadKey(int digit)
    {
        return digit switch
        {
            0 => ConsoleKey.NumPad0, 1 => ConsoleKey.NumPad1, 2 => ConsoleKey.NumPad2,
            3 => ConsoleKey.NumPad3, 4 => ConsoleKey.NumPad4, 5 => ConsoleKey.NumPad5,
            6 => ConsoleKey.NumPad6, 7 => ConsoleKey.NumPad7, 8 => ConsoleKey.NumPad8,
            9 => ConsoleKey.NumPad9, _ => ConsoleKey.NoName
        };
    }

    /// <summary>Main bar: 1-9 are slots 1-9, 0 is 10, - is 11 and = is 12; 0 for any other key.</summary>
    public static int MainBarSlot(string label)
    {
        return label switch
        {
            "1" => 1, "2" => 2, "3" => 3, "4" => 4, "5" => 5,
            "6" => 6, "7" => 7, "8" => 8, "9" => 9, "0" => 10,
            "-" => 11, "=" => 12,
            _ => 0
        };
    }

    /// <summary>Numpad: 1-9 are slots 49-57, 0 is 58.</summary>
    public static int NumpadSlot(int digit)
    {
        return digit == 0 ? 58 : 48 + digit;
    }
}
