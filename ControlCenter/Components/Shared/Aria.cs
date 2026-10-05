namespace ControlCenter.Components.Shared;

/// <summary>
/// ARIA states as their values are spelled. Blazor writes a <c>bool</c> attribute as present or
/// absent, <c>true</c> as an empty value: right for <c>disabled</c>, wrong for
/// <c>aria-pressed</c> and <c>aria-expanded</c>, which take "true" or "false". Written as a
/// <c>bool</c>, a pressed toggle's style (<c>[aria-pressed="true"]</c>) never showed.
/// </summary>
public static class Aria
{
    public static string State(bool on)
    {
        return on ? "true" : "false";
    }
}
