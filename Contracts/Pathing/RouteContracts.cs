namespace Contracts.Pathing;

/// <summary>Which coordinates a route request and its answer use.</summary>
public enum CoordinateSpace
{
    /// <summary>World yards in, world yards out.</summary>
    World,

    /// <summary>The zone's map coordinates (0 to 100) in and out. A destination's height is ignored.</summary>
    Map
}

/// <summary>How a route request ended. A caller tells "no route" from "ask again later" by this.</summary>
public enum RouteStatus
{
    Found,

    /// <summary>
    /// The route goes part of the way: the search found no way on near the straight line between
    /// the points. <see cref="RouteResponse.Points"/> hold the part found; <see cref="RouteResponse.Detail"/>
    /// says where it stops and how far short.
    /// </summary>
    Partial,

    /// <summary>Both points are on the navmesh, but it connects no route between them.</summary>
    NoPath,

    /// <summary>The continent, or the tile under a point, has no baked navmesh.</summary>
    NotBaked,

    /// <summary>The request is malformed or names a zone or client this service does not have.</summary>
    BadRequest,

    /// <summary>The service could not answer now (busy, down, timed out); asking again may work.</summary>
    Unavailable
}

/// <summary>How far from the straight line between the points a route request looks for its way.</summary>
public enum RouteSearch
{
    /// <summary>
    /// A band around the straight line, as the bot searches: fast, and the route a bot would walk.
    /// A route that must stray far from the line answers <see cref="RouteStatus.Partial"/>.
    /// </summary>
    Band,

    /// <summary>
    /// The whole continent: the way across is found first, then searched leg by leg along it. Slower
    /// (the first request after a bake also builds the continent's graph, about 10 s), for long routes.
    /// </summary>
    Continent
}

/// <summary>Shapes one route; every field applies to this request only. The service refuses more than 10 yards of either.</summary>
/// <param name="JitterYards">Random offset of interior corners in yards, so repeated trips do not retrace one line; 0 disables it.</param>
/// <param name="JitterSeed">Makes the jitter reproducible; null draws a fresh seed.</param>
/// <param name="EdgeMarginYards">How far corners are pushed off walls; null keeps the service's default.</param>
public sealed record RouteTuning(float JitterYards = 0f, int? JitterSeed = null, float? EdgeMarginYards = null);

/// <summary>A route between two points of one zone's continent.</summary>
/// <param name="Client">The client data the route is for (<c>som</c>, <c>forever</c>, ...); empty means the service's own.</param>
/// <param name="UiMapId">The zone the points belong to; it also picks the continent.</param>
/// <param name="Space">World yards or zone map coordinates, for the points and the answer.</param>
/// <param name="From">Start. A height of 0 means "unknown": the walkable surface under the point is used.</param>
/// <param name="To">Destination, with the same height rule.</param>
/// <param name="Tuning">Optional jitter and edge margin.</param>
/// <param name="Search">The band around the straight line (the default, as the bot searches) or the whole continent.</param>
public sealed record RouteRequest(string? Client, int UiMapId, CoordinateSpace Space, Point3 From, Point3 To,
    RouteTuning? Tuning = null, RouteSearch Search = RouteSearch.Band);

/// <summary>What a route request found.</summary>
/// <param name="Status">How it ended; <see cref="Points"/> is empty unless <see cref="RouteStatus.Found"/> or <see cref="RouteStatus.Partial"/>.</param>
/// <param name="Points">The route, in the request's <see cref="CoordinateSpace"/>; for a partial route, as far as it goes.</param>
/// <param name="Smoothed">The points are a dense, validated spline: re-request rather than patch a partial route.</param>
/// <param name="ElapsedMs">Time the service spent, including any wait for the continent.</param>
/// <param name="Detail">Why nothing, or only part of the way, was found, for a person to read; null when found.</param>
public sealed record RouteResponse(RouteStatus Status, Point3[] Points, bool Smoothed, double ElapsedMs, string? Detail)
{
    public static RouteResponse Failed(RouteStatus status, string detail)
    {
        return new(status, [], false, 0, detail);
    }
}
