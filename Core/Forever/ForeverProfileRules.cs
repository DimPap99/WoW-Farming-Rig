using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Core.Forever;

/// <summary>
/// Profile features the Forever visual channel cannot back. RequirementFactory
/// asks these on Forever and refuses the profile instead of building a
/// requirement that would read secret or absent data.
/// </summary>
public static partial class ForeverProfileRules
{
    /// <summary>Why <paramref name="item"/> cannot run on Forever, or null.</summary>
    public static string? FindUnsupportedAction(KeyAction item)
    {
        if (item.AfterCastWaitBuff || item.AfterCastAuraExpected)
            return $"Forever action '{item.Name}' requires an unverified aura signal.";
        if (item.HasForm || item.MinComboPoints > 0)
            return $"Forever action '{item.Name}' requires unported form or combo-point cost data.";
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

    [GeneratedRegex(@"(?<!\w)CD_[^<>=&|),]+", RegexOptions.IgnoreCase)]
    private static partial Regex CooldownVariable();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])Cost(?:_[^<>=&|),]+)?(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ActionCostVariable();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(Mana|Energy|Rage|RunicPower|Focus|PetHealth%|FocusHealth%)(?![A-Za-z0-9_%])",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedVital();
}
