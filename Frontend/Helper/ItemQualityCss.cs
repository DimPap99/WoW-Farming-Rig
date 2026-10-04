namespace Frontend;

/// <summary>The site.css classes that colour an item by its quality, poor to heirloom.</summary>
public static class ItemQualityCss
{
    private static readonly string[] classes = ["poor", "common", "uncommon", "rare", "epic", "legendary", "artifact", "heirloom"];

    /// <summary>The class for <paramref name="quality"/>; common for a quality outside the known ones.</summary>
    public static string For(int quality)
    {
        return (uint)quality < (uint)classes.Length ? classes[quality] : classes[1];
    }
}
