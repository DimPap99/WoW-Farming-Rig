using PPather;

namespace PathingAPI.Watch;

/// <summary>Colour codes the hub sends; the 3D view and the map both decode them.</summary>
public enum WatchColor
{
    Red = 1,
    Green,
    Blue,
    Teal,
    White,
    Orange,
    Yellow,
    Black,
    Magenta
}

/// <summary>
/// How a finished path is drawn. The name is also the mesh/layer id, so a later
/// path with the same name replaces the earlier one.
/// </summary>
public readonly record struct PathStyle(WatchColor Color, string Name)
{
    /// <summary>Routes requested over the HTTP or AnTCP API.</summary>
    public static readonly PathStyle Api = new(WatchColor.Red, "path");

    /// <summary>Straight lines and recorded paths drawn without searching.</summary>
    public static readonly PathStyle Draft = new(WatchColor.Orange, "route");

    /// <summary>A search from the web UI, coloured by strategy so A/B runs stay apart.</summary>
    public static PathStyle For(SearchStrategy strategy) =>
        new((WatchColor)((int)strategy + 1), strategy.ToString());
}

/// <summary>How much client geometry the 3D view receives alongside a search.</summary>
public enum GeometryMode
{
    Off = 0,
    /// <summary>Only triangles under explored spots (70-90% fewer).</summary>
    SparseFromSpots,
    /// <summary>Walkable areas near explored spots (50-80% fewer).</summary>
    WalkableOnly,
    /// <summary>The bounding box of explored spots (30-70% fewer).</summary>
    ExploredArea,
    /// <summary>The full mesh.</summary>
    All
}
