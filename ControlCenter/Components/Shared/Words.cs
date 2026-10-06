using System.Globalization;

namespace ControlCenter.Components.Shared;

/// <summary>Numbers in words for the pages, the same in every culture.</summary>
public static class Words
{
    /// <summary>"1 waypoint", "1,204 waypoints".</summary>
    public static string Count(int count, string noun)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
    }

    /// <summary>"2,807 yd".</summary>
    public static string Yards(float yards)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{yards:N0} yd");
    }

    /// <summary>"12,300 sq yd".</summary>
    public static string SquareYards(float area)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{area:N0} sq yd");
    }
}
