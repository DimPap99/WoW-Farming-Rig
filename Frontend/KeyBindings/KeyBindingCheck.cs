using Core;

using SharedLib;

namespace Frontend;

/// <summary>How a key stands against the bindings the bot needs.</summary>
public enum KeyBindingState
{
    /// <summary>Nothing to say: unbound and not needed, or needed but bound elsewhere.</summary>
    None,
    /// <summary>Bound in game to a binding the bot needs.</summary>
    Bound,
    /// <summary>Bound in game to a binding the bot does not use.</summary>
    InGame,
    /// <summary>The bot needs a binding on this key, and the game has it on no key at all.</summary>
    Unbound
}

/// <summary>
/// The key bindings the bot needs against those the game reports: the defaults
/// (<see cref="KeyBindingDefaults"/>) with the profile's base actions over them,
/// and the game's primary and secondary bindings. <see cref="Refresh"/> reads
/// the game's bindings again.
/// </summary>
public sealed class KeyBindingCheck
{
    private readonly KeyBindingsReader reader;

    // What the bot needs, both ways round.
    private readonly Dictionary<ConsoleKey, BindingID> expectedByKey = [];
    private readonly Dictionary<BindingID, ConsoleKey> expectedById = [];

    // What the game has, by key.
    private readonly Dictionary<ConsoleKey, BindingID> primaryByKey = [];
    private readonly Dictionary<ConsoleKey, BindingID> secondaryByKey = [];

    private readonly List<BindingID> missing = [];

    public KeyBindingCheck(KeyBindingsReader reader, IEnumerable<KeyAction> baseActions)
    {
        this.reader = reader;

        foreach (KeyBinding binding in KeyBindingDefaults.Bindings.Values)
        {
            expectedByKey[binding.ConsoleKey] = binding.BindingID;
            expectedById[binding.BindingID] = binding.ConsoleKey;
        }

        // The profile's keys take precedence over the defaults.
        foreach (KeyAction action in baseActions)
        {
            if (action.BindingID == BindingID.None || action.ConsoleKey == ConsoleKey.NoName)
                continue;

            // The binding moved to another key: the default key no longer needs it.
            if (expectedById.TryGetValue(action.BindingID, out ConsoleKey oldKey) && oldKey != action.ConsoleKey)
                expectedByKey.Remove(oldKey);

            expectedByKey[action.ConsoleKey] = action.BindingID;
            expectedById[action.BindingID] = action.ConsoleKey;
        }

        Refresh();
    }

    /// <summary>Needed bindings the game has on some key, any modifier.</summary>
    public int BoundCount { get; private set; }

    /// <summary>Needed bindings the game has on no key.</summary>
    public IReadOnlyList<BindingID> Missing => missing;

    public bool MissingActionBarBindings => missing.Exists(id =>
        id.ToString().StartsWith("MULTIACTIONBAR") || id.ToString().StartsWith("ACTIONBUTTON"));

    public bool MissingCustomActions => missing.Exists(id => id.ToString().StartsWith("CUSTOM_"));

    /// <summary>Reads the game's bindings again and rechecks the needed ones against them.</summary>
    public void Refresh()
    {
        primaryByKey.Clear();
        foreach ((BindingID id, (ConsoleKey key, ModifierKey _)) in reader.Bindings)
            primaryByKey[key] = id;

        secondaryByKey.Clear();
        foreach ((BindingID id, (ConsoleKey key, ModifierKey _)) in reader.SecondaryBindings)
            secondaryByKey[key] = id;

        BoundCount = 0;
        missing.Clear();
        foreach (BindingID id in expectedById.Keys)
        {
            if (IsBoundInGame(id))
                BoundCount++;
            else
                missing.Add(id);
        }
    }

    public KeyBindingState StateOf(ConsoleKey key)
    {
        if (primaryByKey.TryGetValue(key, out BindingID id) || secondaryByKey.TryGetValue(key, out id))
            return expectedById.ContainsKey(id) ? KeyBindingState.Bound : KeyBindingState.InGame;

        return expectedByKey.TryGetValue(key, out BindingID expected) && !IsBoundInGame(expected)
            ? KeyBindingState.Unbound
            : KeyBindingState.None;
    }

    /// <summary>The short name of the binding the key needs, else of the one it has; null for neither.</summary>
    public string? Label(ConsoleKey key)
    {
        if (expectedByKey.TryGetValue(key, out BindingID id) || primaryByKey.TryGetValue(key, out id))
            return ShortName(id);

        return null;
    }

    /// <summary>The game's primary binding on the key.</summary>
    public bool TryGetInGame(ConsoleKey key, out BindingID id) => primaryByKey.TryGetValue(key, out id);

    /// <summary>What the game has on the key, and the needed binding it lacks; the key's name for nothing.</summary>
    public string Tooltip(ConsoleKey key)
    {
        List<string> parts = [];

        if (primaryByKey.TryGetValue(key, out BindingID primary))
            parts.Add($"In-Game: {ModifierPrefixOf(key)}{primary}");

        if (secondaryByKey.TryGetValue(key, out BindingID secondary))
            parts.Add($"Secondary: {secondary}");

        if (expectedByKey.TryGetValue(key, out BindingID expected) && !IsBoundInGame(expected))
            parts.Add($"Default: {expected} (NOT BOUND)");

        return parts.Count > 0 ? string.Join(" | ", parts) : key.ToString();
    }

    /// <summary>Every binding the game reports, movement first, then targeting, the bars, custom actions and interaction.</summary>
    public IEnumerable<BindingID> InGameBindings()
    {
        HashSet<BindingID> all = [.. reader.Bindings.Keys, .. reader.SecondaryBindings.Keys];
        return all.OrderBy(Category).ThenBy(id => id.ToString());
    }

    public static string FormatKey(ConsoleKey key, ModifierKey modifier)
    {
        string prefix = modifier != ModifierKey.None ? modifier.ToPrefix() : "";
        return $"{prefix}{key}";
    }

    public static string ShortName(BindingID id)
    {
        return id switch
        {
            BindingID.JUMP => "Jump",
            BindingID.INTERACTTARGET => "Interact",
            BindingID.INTERACTMOUSEOVER => "IntMO",
            BindingID.TARGETLASTTARGET => "LastTgt",
            BindingID.SITORSTAND => "Sit",
            BindingID.STOPATTACK => "Stop",
            BindingID.TARGETNEARESTENEMY => "TabTgt",
            BindingID.ASSISTTARGET => "Assist",
            BindingID.TARGETPET => "TgtPet",
            BindingID.PETATTACK => "PetAtk",
            BindingID.TARGETFOCUS => "Focus",
            BindingID.FOLLOWTARGET => "Follow",
            BindingID.STARTATTACK => "Attack",
            BindingID.MOVEFORWARD => "Fwd",
            BindingID.MOVEBACKWARD => "Back",
            BindingID.STRAFELEFT => "StrfL",
            BindingID.STRAFERIGHT => "StrfR",
            BindingID.TURNLEFT => "TurnL",
            BindingID.TURNRIGHT => "TurnR",
            _ => id.ToString()
        };
    }

    private bool IsBoundInGame(BindingID id) =>
        reader.TryGetBinding(id, out _, out _) || reader.TryGetSecondaryBinding(id, out _, out _);

    // The modifier of the first primary binding on the key, as the game lists them.
    private string ModifierPrefixOf(ConsoleKey key)
    {
        foreach ((ConsoleKey bound, ModifierKey modifier) in reader.Bindings.Values)
        {
            if (bound == key)
                return modifier != ModifierKey.None ? modifier.ToPrefix() : "";
        }

        return "";
    }

    private static int Category(BindingID id)
    {
        string name = id.ToString();
        if (name.StartsWith("MOVE") || name.StartsWith("STRAFE") || name.StartsWith("TURN") || name == "JUMP" || name == "SITORSTAND")
            return 0;
        if (name.StartsWith("TARGET") || name.StartsWith("ASSIST") || name.StartsWith("FOCUS"))
            return 1;
        if (name.StartsWith("ACTIONBUTTON"))
            return 2;
        if (name.StartsWith("MULTIACTIONBAR2"))
            return 3;
        if (name.StartsWith("MULTIACTIONBAR1"))
            return 4;
        if (name.StartsWith("CUSTOM_"))
            return 5;
        if (name.StartsWith("PET") || name.StartsWith("INTERACT") || name.StartsWith("FOLLOW"))
            return 6;
        return 99;
    }
}
