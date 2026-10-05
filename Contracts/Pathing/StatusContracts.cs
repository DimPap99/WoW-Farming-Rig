namespace Contracts.Pathing;

/// <summary>What a Pathing Service instance serves.</summary>
/// <param name="ApiVersion">The <see cref="PathingApi.Version"/> it implements.</param>
/// <param name="Client">The client data it serves (<c>som</c>, <c>forever</c>, ...).</param>
/// <param name="Era">The navmesh era of that client (<c>precata</c>, <c>cata</c>, ...).</param>
/// <param name="PathsAreSmoothed">Routes are dense validated splines.</param>
/// <param name="BakedContinents">Continents with at least one baked navmesh tile.</param>
/// <param name="GameFiles">The client's game files are installed where the service runs, so it can
/// bake, extract area grids and show terrain.</param>
public sealed record PathingCapabilities(int ApiVersion, string Client, string Era, bool PathsAreSmoothed,
    string[] BakedContinents, bool GameFiles);

/// <summary>Baked navmesh tiles on disk for one continent.</summary>
public sealed record ContinentCoverage(string Continent, int Tiles);

/// <summary>One continent the planner has open.</summary>
/// <param name="ResidentTiles">Tiles currently in memory.</param>
/// <param name="Queries">Routes answered since the service started.</param>
/// <param name="LastMs">Duration of the last route answered.</param>
public sealed record PlannerLane(string Continent, int ResidentTiles, long Queries, double LastMs);

/// <summary>The service's state, for a status page.</summary>
/// <param name="Bake">The running, or last, navmesh bake.</param>
public sealed record PathingHealth(string Client, string Era, ContinentCoverage[] Coverage, PlannerLane[] Lanes,
    BakeStatus Bake);
