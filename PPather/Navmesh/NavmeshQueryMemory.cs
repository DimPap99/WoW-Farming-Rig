using System.Numerics;

namespace PPather.Navmesh;

/// <summary>
/// What one walker's queries remember between calls to
/// <see cref="NavmeshPathfinder.FindPath(Vector3, Vector3, bool?)"/>: the floor it was last
/// resolved onto, the corridor band that last worked, and the last search that stalled.
/// <para>
/// It belongs to the walker, not to the mesh. A pathfinder keeps one for its own caller,
/// which is right for the bot's single follower. A pathfinder shared by several callers
/// (the pathing service) gives each request its own instance through the
/// <see cref="NavmeshPathfinder.FindPath(Vector3, Vector3, bool?, NavmeshQueryMemory, in NavmeshQueryTuning, out NavmeshPathfinder.NavmeshStats)"/>
/// overload; otherwise walker B would be resolved onto walker A's floor.
/// </para>
/// </summary>
public sealed class NavmeshQueryMemory
{
    // Surface the last query resolved the caller onto, and where that was.
    // A walking caller stays on it, which settles "which floor" for positions
    // that arrive without a usable height.
    internal Vector3? LastResolvedFrom;

    // Band half-width the last query settled on, and the destination it was
    // settled for. The follower re-requests the same destination as it walks,
    // and a widen it already paid for left those tiles resident - but an
    // unrelated route should not inherit the wider band and load tiles it has
    // no use for.
    internal int StickyCorridorRadius;
    internal long StickyEndRef;
    internal Vector3 StickyFrom;
    internal long StickyStamp;

    // Last search that ran out of room, so an identical repeat can answer from
    // what was already learned instead of re-walking the whole escalation.
    internal long StalledEndRef;
    internal Vector3 StalledFrom;
    internal long StalledStamp;
}

/// <summary>
/// Per-query path shaping. Passed with each query rather than set on the pathfinder, so one
/// caller's jitter or edge margin cannot leak into the next caller's route.
/// </summary>
/// <param name="JitterYards">Random interior-corner offset in yards; 0 disables it.</param>
/// <param name="JitterSeed">Seeds the jitter; null draws a fresh seed.</param>
/// <param name="EdgeMarginYards">Edge margin override; null keeps the configured default.</param>
public readonly record struct NavmeshQueryTuning(float JitterYards = 0f, int? JitterSeed = null, float? EdgeMarginYards = null)
{
    /// <summary>
    /// The most jitter a caller may ask for. Each interior corner searches every polygon within
    /// this radius, and the query holds its pathfinder meanwhile: 100,000 yards took 36 s on one
    /// route, 1.5 yards 2 ms.
    /// </summary>
    public const float MaxJitterYards = 10f;

    /// <summary>
    /// The widest edge margin a caller may ask for (the configured default is 1.5). A push off
    /// the wall snaps a point that far out, and one beyond the world grid never comes back from
    /// Detour (<see cref="NavmeshCoords.InWorld"/>).
    /// </summary>
    public const float MaxEdgeMarginYards = 10f;

    /// <summary>Why a caller's tuning is refused, or null: each value finite, not negative and within its cap.</summary>
    public string? Problem()
    {
        if (!float.IsFinite(JitterYards) || JitterYards is < 0f or > MaxJitterYards)
        {
            return $"jitter must be 0 to {MaxJitterYards} yards";
        }

        return EdgeMarginYards is float margin && (!float.IsFinite(margin) || margin is < 0f or > MaxEdgeMarginYards)
            ? $"the edge margin must be 0 to {MaxEdgeMarginYards} yards"
            : null;
    }
}
