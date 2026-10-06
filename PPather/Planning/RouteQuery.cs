#nullable enable
using DotRecast.Detour;

using PPather.Navmesh;

using System.Numerics;

namespace PPather.Planning;

/// <summary>Which coordinates a route query and its answer use.</summary>
public enum RouteSpace
{
    /// <summary>World yards in, world yards out.</summary>
    World,

    /// <summary>
    /// Zone map coordinates (0 to 100) of the query's uiMap in, map coordinates out. The
    /// destination's height is ignored and resolved from the mesh, as the bot's map route does.
    /// </summary>
    Map
}

/// <summary>How far from the straight line a route query looks for its way.</summary>
public enum RouteReach
{
    /// <summary>
    /// A band of tiles around the straight line, as the bot searches: fast, and the route a bot
    /// would walk. A route that must stray far from the line stops short (<see cref="RoutePlanStatus.Partial"/>).
    /// </summary>
    Band,

    /// <summary>
    /// The whole continent: the way across is found on its <see cref="NavmeshTileGraph"/> first,
    /// then searched leg by leg along it. Slower; for long routes and for planning them ahead.
    /// </summary>
    Continent
}

/// <summary>How a route query ended.</summary>
public enum RoutePlanStatus
{
    Found,

    /// <summary>
    /// The route stops short of the destination: the search found no way on inside the band it
    /// loads around the straight line. The points go as far as it got.
    /// </summary>
    Partial,

    /// <summary>Both endpoints are on baked tiles, but the mesh connects no route between them.</summary>
    NoPath,

    /// <summary>The continent, or the tile under an endpoint, has no baked navmesh.</summary>
    NotBaked,

    /// <summary>The uiMap is not in the client's map data.</summary>
    UnknownMap,

    /// <summary>The continent's lane stayed busy past the planner's wait budget.</summary>
    Busy,

    /// <summary>The query asks for more work than the planner does: tuning past <see cref="NavmeshQueryTuning"/>'s caps.</summary>
    Refused
}

/// <summary>One route request to <see cref="RoutePlanner.Find"/>.</summary>
/// <param name="UiMapId">The zone the coordinates belong to; it also decides the continent.</param>
/// <param name="Space">Whether <paramref name="From"/>/<paramref name="To"/> are world or map coordinates.</param>
/// <param name="From">Start. A height of 0 means "none given": the walkable surface under the point is used.</param>
/// <param name="To">Destination, with the same height rule.</param>
/// <param name="Tuning">Jitter and edge margin for this query only.</param>
/// <param name="Reach">The band around the straight line, or the whole continent.</param>
public readonly record struct RouteQuery(int UiMapId, RouteSpace Space, Vector3 From, Vector3 To,
    NavmeshQueryTuning Tuning = default, RouteReach Reach = RouteReach.Band);

/// <summary>What <see cref="RoutePlanner.Find"/> answered.</summary>
/// <param name="Status">How the query ended.</param>
/// <param name="Points">The route in the query's <see cref="RouteSpace"/>; empty unless found or partial.</param>
/// <param name="Continent">The continent the query ran on; empty when the uiMap was unknown.</param>
/// <param name="ElapsedMs">Time spent, including any wait for the lane.</param>
/// <param name="Detail">Why nothing, or only part of the way, was found, for a person to read; null when found.</param>
public sealed record RouteOutcome(RoutePlanStatus Status, Vector3[] Points, string Continent, double ElapsedMs, string? Detail)
{
    public bool Found => Status == RoutePlanStatus.Found;
}

/// <summary>What <see cref="RoutePlanner.RoutePlanned"/> reports: one answer, always in world yards.</summary>
/// <param name="Continent">The continent the query ran on.</param>
/// <param name="From">The start, with the height it was resolved to when the query got that far.</param>
/// <param name="To">The destination, likewise.</param>
/// <param name="Points">The route in world yards; empty unless found or partial.</param>
/// <param name="Status">How the query ended.</param>
/// <param name="ElapsedMs">Time spent.</param>
/// <param name="Detail">Why nothing, or only part of the way, was found; null when found.</param>
public sealed record PlannedRoute(string Continent, Vector3 From, Vector3 To, Vector3[] Points, RoutePlanStatus Status,
    double ElapsedMs, string? Detail);

/// <summary>A navmesh tile a planner lane loaded (with its mesh data) or evicted (no data).</summary>
public readonly record struct LaneTile(string Continent, int X, int Z, DtMeshData? Data);
