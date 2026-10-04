using Core;

namespace Frontend;

/// <summary>A stance or form bar: its name and the slot before its first.</summary>
public sealed record StanceBar(string Name, int BaseSlot);

/// <summary>
/// The profile's actions by the action bar slot they are on, a stance bar's
/// slot for an action that needs a form. An action without a form inherits one
/// from another action of the same name that has it; a warrior's main bar
/// actions are on the bar of the stance the player is in.
/// <para>
/// <see cref="ActionBarSlotValidator"/> resolves slots the same way for its
/// checks, apart from Prowl: it keeps a Prowl action on the Cat Form bar,
/// where this puts an action that inherits Prowl on the Prowl bar.
/// </para>
/// </summary>
public sealed class ActionBarSlotMap
{
    private readonly Dictionary<int, KeyAction> bySlot = [];

    public ActionBarSlotMap(ClassConfiguration config, PlayerReader player)
    {
        List<KeyAction> actions =
        [
            .. config.Pull.Sequence,
            .. config.Combat.Sequence,
            .. config.Adhoc.Sequence,
            .. config.Parallel.Sequence,
            .. config.NPC.Sequence,
            .. config.Flee.Sequence,
            .. config.AssistFocus.Sequence,
        ];

        Dictionary<string, Form> formBySpell = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyAction action in actions)
        {
            if (action.HasForm && !string.IsNullOrEmpty(action.Name))
                formBySpell.TryAdd(action.Name, action.FormValue);
        }

        // The first action on a slot keeps it.
        foreach (KeyAction action in actions)
        {
            if (action.Slot > 0)
                bySlot.TryAdd(SlotOf(action, formBySpell, player), action);
        }
    }

    public KeyAction? this[int slot] => bySlot.GetValueOrDefault(slot);

    /// <summary>The stance and form bars <paramref name="class"/> has, each 12 slots.</summary>
    public static StanceBar[] StanceBars(UnitClass @class)
    {
        return @class switch
        {
            UnitClass.Warrior => [
                new("Battle Stance", 72),
                new("Defensive Stance", 84),
                new("Berserker Stance", 96)
            ],
            UnitClass.Druid => [
                new("Cat Form", 72),
                new("Cat Form (Prowl)", 84),
                new("Bear Form", 96),
                new("Moonkin Form", 108)
            ],
            UnitClass.Rogue => [
                new("Stealth", 72)
            ],
            UnitClass.Priest => [
                new("Shadowform", 72)
            ],
            _ => []
        };
    }

    private static int SlotOf(KeyAction action, Dictionary<string, Form> formBySpell, PlayerReader player)
    {
        if (action.HasForm)
            return Stance.ToSlot(action, player);

        if (!string.IsNullOrEmpty(action.Name) &&
            formBySpell.TryGetValue(action.Name, out Form inferred) &&
            inferred != Form.None &&
            action.Slot <= ActionBar.MAIN_ACTIONBAR_SLOT)
        {
            return action.Slot + (int)BarOf(player.Class, inferred);
        }

        // Warriors are always in a stance, so the main bar is a stance bar.
        if (player.Class == UnitClass.Warrior &&
            action.Slot <= ActionBar.MAIN_ACTIONBAR_SLOT &&
            player.Form != Form.None)
        {
            return action.Slot + (int)BarOf(UnitClass.Warrior, player.Form);
        }

        return action.Slot;
    }

    private static StanceActionBar BarOf(UnitClass @class, Form form)
    {
        return @class switch
        {
            UnitClass.Druid => form switch
            {
                Form.Druid_Cat => StanceActionBar.DruidCat,
                Form.Druid_Cat_Prowl => StanceActionBar.DruidCatProwl,
                Form.Druid_Bear => StanceActionBar.DruidBear,
                Form.Druid_Moonkin => StanceActionBar.DruidMoonkin,
                _ => StanceActionBar.None
            },
            UnitClass.Warrior => form switch
            {
                Form.Warrior_BattleStance => StanceActionBar.WarriorBattleStance,
                Form.Warrior_DefensiveStance => StanceActionBar.WarriorDefensiveStance,
                Form.Warrior_BerserkerStance => StanceActionBar.WarriorBerserkerStance,
                _ => StanceActionBar.None
            },
            UnitClass.Rogue => form == Form.Rogue_Stealth ? StanceActionBar.RogueStealth : StanceActionBar.None,
            UnitClass.Priest => form == Form.Priest_Shadowform ? StanceActionBar.PriestShadowform : StanceActionBar.None,
            _ => StanceActionBar.None
        };
    }
}
