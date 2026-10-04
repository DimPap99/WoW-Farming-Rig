using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Core.Forever;

/// <summary>
/// Profile features the Forever visual channel cannot back. RequirementFactory
/// asks these on Forever and refuses the profile instead of building a
/// requirement that would read secret or absent data; ForeverGoalFactory asks
/// <see cref="FindUnsupportedProfile"/> about the lists the runtime executes.
/// </summary>
public static partial class ForeverProfileRules
{
    /// <summary>Numeric values a Forever requirement may compare; each has a checked visual source.</summary>
    public static readonly IReadOnlySet<string> SupportedValues =
        new HashSet<string>(["Health%", "Mana%", "TargetHealth%"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The one boolean requirement with a Forever source: a learned spell.</summary>
    public const string SpellPrefix = "Spell:";

    /// <summary>Why <paramref name="item"/> cannot run on Forever, or null.</summary>
    public static string? FindUnsupportedAction(KeyAction item)
    {
        if (item.AfterCastWaitBuff)
            return $"Forever action '{item.Name}' requires an unverified aura signal.";
        // A buff's aura is matched against its main-bar slot's icon.
        if (item.AfterCastAuraExpected &&
            item.Slot is < 1 or > VisualSlotAuraSnapshot.SlotCount)
            return $"Forever action '{item.Name}' expects an aura, which is read only for main-bar slots 1 to {VisualSlotAuraSnapshot.SlotCount}.";
        if (item.HasForm || item.MinComboPoints > 0)
            return $"Forever action '{item.Name}' requires unported form or combo-point cost data.";
        return null;
    }

    /// <summary>
    /// Why the Forever runtime cannot execute <paramref name="config"/>, or
    /// null. It runs only the Pull, Combat and Adhoc lists, presses spells on
    /// main-bar slots by their keys (never a macro typed into chat), starts
    /// melee through <c>AutoAttack</c>, and evaluates only requirements with a
    /// Forever source.
    /// </summary>
    public static string? FindUnsupportedProfile(ClassConfiguration config)
    {
        foreach ((string list, KeyActions actions) in
            new (string, KeyActions)[] { ("Parallel", config.Parallel), ("NPC", config.NPC), ("Wait", config.Wait) })
        {
            if (actions.Sequence.Length > 0)
                return $"Forever runs only the Pull, Combat and Adhoc lists; '{list}' must be empty.";
        }

        foreach ((string list, KeyActions actions) in
            new (string, KeyActions)[] { ("Pull", config.Pull), ("Combat", config.Combat), ("Adhoc", config.Adhoc) })
        {
            foreach (KeyAction action in actions.Sequence)
            {
                if (action.BaseAction)
                {
                    // Melee start is pressed once per target; its upstream
                    // requirements read Classic swing state and are not used.
                    if (list == "Combat" && action.Name == config.AutoAttack.Name)
                        continue;
                    return $"Forever {list} action '{action.Name}' is a base action; only Combat may use '{config.AutoAttack.Name}'.";
                }
                if (action.Slot is < 1 or > VisualCooldownGridSnapshot.SlotCount)
                    return $"Forever {list} action '{action.Name}' must be on main-bar slots 1 to {VisualCooldownGridSnapshot.SlotCount}.";
                // A person presses the key; nothing is typed into chat.
                if (!string.IsNullOrEmpty(action.MacroText))
                    return $"Forever {list} action '{action.Name}' uses MacroText, which would be typed into chat; Forever only presses keys.";
                if (action.Interrupts.Count > 0)
                    return $"Forever {list} action '{action.Name}' uses interrupts, which Forever does not evaluate.";
                foreach (string requirement in action.Requirements)
                {
                    if (FindUnbackedTerm(requirement) is { } term)
                        return $"Forever {list} action '{action.Name}' requirement '{requirement}' uses '{term}', which has no Forever source. Supported: {string.Join(", ", SupportedValues)} comparisons and {SpellPrefix}<id>.";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The first term of <paramref name="expression"/> without a Forever
    /// source, or null when every term is a number, a supported value or a
    /// learned-spell check.
    /// </summary>
    public static string? FindUnbackedTerm(string expression)
    {
        foreach (string part in BooleanSeparator().Split(expression))
        {
            string term = part.Trim();
            if (term.Length == 0)
                continue;

            string[] sides = ComparisonOperator().Split(term);
            if (sides.Length == 1)
            {
                if (!term.StartsWith(SpellPrefix, StringComparison.OrdinalIgnoreCase))
                    return term;
                continue;
            }
            foreach (string side in sides)
            {
                string value = side.Trim();
                if (!SupportedValues.Contains(value) &&
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return value;
            }
        }
        return null;
    }

    /// <summary>Why <paramref name="expression"/> cannot run on Forever, or null.</summary>
    public static string? FindUnsupportedExpression(string expression,
        IReadOnlySet<string> actionCooldownKeys, IEnumerable<string> auraKeys)
    {
        if (FindCooldownComparison(expression, actionCooldownKeys) is { } key)
            return $"Forever profile expression '{expression}' uses {key} as milliseconds; replace it with a readiness rule.";
        if (FindActionCost(expression) is { } cost)
            return $"Forever profile expression '{expression}' uses unavailable numeric action cost {cost}; use checked usability and no-mana state.";
        if (FindUnsupportedVital(expression) is { } vital)
            return $"Forever profile expression '{expression}' uses unavailable exact or secondary vital {vital}; use a checked percentage or action usability rule.";
        if (FindAura(expression, auraKeys) is { } aura)
            return $"Forever profile expression '{expression}' uses unverified aura {aura}.";
        return null;
    }

    public static string? FindCooldownComparison(
        string expression, IReadOnlySet<string> actionCooldownKeys)
    {
        foreach (Match match in CooldownVariable().Matches(expression))
        {
            string key = match.Value.Trim();
            if (actionCooldownKeys.Contains(key))
                return key;
        }
        return null;
    }

    public static string? FindActionCost(string expression)
    {
        Match match = ActionCostVariable().Match(expression);
        return match.Success ? match.Value.Trim() : null;
    }

    public static string? FindUnsupportedVital(string expression)
    {
        Match match = UnsupportedVital().Match(expression);
        return match.Success ? match.Value : null;
    }

    public static string? FindAura(string expression, IEnumerable<string> auraKeys)
    {
        foreach (string key in auraKeys.OrderByDescending(x => x.Length))
        {
            if (Regex.IsMatch(expression,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase))
                return key;
        }

        return null;
    }

    /// <summary>A user-defined int variable that names an aura timer.</summary>
    public static bool IsAuraVariable(string key) =>
        key.StartsWith("Buff_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("Debuff_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("TDebuff_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("TBuff_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("FBuff_", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"&&|\|\||[()!]")]
    private static partial Regex BooleanSeparator();

    [GeneratedRegex(@"<=|>=|==|<|>")]
    private static partial Regex ComparisonOperator();

    [GeneratedRegex(@"(?<!\w)CD_[^<>=&|),]+", RegexOptions.IgnoreCase)]
    private static partial Regex CooldownVariable();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])Cost(?:_[^<>=&|),]+)?(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ActionCostVariable();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(Mana|Energy|Rage|RunicPower|Focus|PetHealth%|FocusHealth%)(?![A-Za-z0-9_%])",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedVital();
}
