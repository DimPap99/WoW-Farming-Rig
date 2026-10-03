namespace SharedLib;

/// <summary>
/// Query-time navmesh parameters, bound from configuration (section
/// <see cref="Position"/>). None of these affect the baked tiles, so changing
/// them needs no rebake and does not enter the settings hash.
/// </summary>
public sealed class NavmeshQueryOptions
{
    public const string Position = "Navmesh:Query";

    /// <summary>Distance interior corners are pushed off boundary walls, yards. 0 disables the push.</summary>
    public float EdgeMargin { get; set; } = 1.5f;

    /// <summary>Catmull-Rom spline point spacing, yards. 0 restores the fixed per-segment subdivision.</summary>
    public float PathSpacing { get; set; } = 2.5f;

    /// <summary>Corridor band half-width loaded around the straight route, tiles.</summary>
    public int CorridorTiles { get; set; } = 2;

    /// <summary>Ceiling the widen-and-retry escalation may grow the band to, tiles.</summary>
    public int CorridorMaxTiles { get; set; } = 4;

    /// <summary>Widen the loaded band and retry when a search stalls short of the goal.</summary>
    public bool CorridorWiden { get; set; } = true;

    /// <summary>Skip widening when the route is shorter than the band - a stall there is geometry, not residency.</summary>
    public bool SkipWidenShortRoutes { get; set; } = true;

    /// <summary>Road-attractor core half-width, yards.</summary>
    public float RoadCore { get; set; } = 8f;

    /// <summary>
    /// For a position with no height, keep only the surfaces directly under its
    /// X/Y before choosing a floor, so a taller neighbour inside the search box
    /// cannot win. Off by default; the Forever runtime turns it on.
    /// </summary>
    public bool PreferGroundUnderPoint { get; set; }
}
