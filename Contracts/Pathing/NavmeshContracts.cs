namespace Contracts.Pathing;

/// <summary>One baked navmesh tile and its world footprint.</summary>
/// <param name="X">Tile index along world y (Detour's x: <c>(y - origin) / size</c>).</param>
/// <param name="Z">Tile index along world x (Detour's z).</param>
/// <param name="Resident">The planner has it loaded right now.</param>
public sealed record NavmeshTile(int X, int Z, float MinX, float MinY, float MaxX, float MaxY, bool Resident);

/// <summary>
/// A continent at a glance: one walkable height per baked tile, the base of a map at any zoom,
/// for any client - minimap art or not.
/// </summary>
/// <param name="Origin">The world x and y where the tile grid starts: tile (x, z) spans world y from
/// <c>Origin + x * TileSize</c> and world x from <c>Origin + z * TileSize</c>, each one tile on.</param>
/// <param name="TileSize">A tile's edge in yards.</param>
/// <param name="Tiles">Every baked tile as x, z pairs (<see cref="NavmeshTile.X"/>, <see cref="NavmeshTile.Z"/>).</param>
/// <param name="Heights">Each tile's walkable height, in the same order.</param>
public sealed record NavmeshOverview(string Continent, float Origin, float TileSize, int[] Tiles, float[] Heights);

/// <summary>One baked tile's walkable polygons, for drawing.</summary>
/// <param name="X">Tile index along world y, as <see cref="NavmeshTile.X"/>.</param>
/// <param name="Z">Tile index along world x.</param>
/// <param name="Vertices">World yards, as x, y, z triples.</param>
/// <param name="Polygons">Each polygon as its vertex count, then that many indices into the vertices.</param>
/// <param name="Water">Per polygon: 1 for water, else 0.</param>
public sealed record NavmeshTileGeometry(int X, int Z, float[] Vertices, int[] Polygons, int[] Water);

/// <summary>What to bake. Nothing set bakes every continent; an ADT needs its continent.</summary>
/// <param name="Continent">One continent; null for all of them.</param>
/// <param name="AdtX">World grid x of one ADT, with <paramref name="AdtY"/>.</param>
/// <param name="AdtY">World grid y of one ADT, with <paramref name="AdtX"/>.</param>
public sealed record BakeRequest(string? Continent = null, int? AdtX = null, int? AdtY = null);

/// <summary>The running, or last, navmesh bake.</summary>
/// <param name="Running">A bake is in progress.</param>
/// <param name="Scope">What was asked for: an ADT, a continent or all of them.</param>
/// <param name="Continent">The continent being baked now.</param>
/// <param name="AdtTotal">ADTs to visit on that continent.</param>
/// <param name="AdtDone">ADTs visited.</param>
/// <param name="TilesTotal">Tiles requested so far.</param>
/// <param name="TilesDone">Tiles this bake wrote.</param>
/// <param name="TilesAlreadyOnDisk">Requested tiles that were already baked.</param>
/// <param name="ElapsedSeconds">Time spent.</param>
/// <param name="Message">What it is doing, or how it ended (<c>done</c>, <c>cancelled</c>, <c>failed: ...</c>).</param>
public sealed record BakeStatus(bool Running, string Scope, string Continent, int AdtTotal, int AdtDone,
    int TilesTotal, int TilesDone, int TilesAlreadyOnDisk, double ElapsedSeconds, string Message)
{
    /// <summary>No bake has run since the service started.</summary>
    public static BakeStatus Idle { get; } = new(false, string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, "idle");
}

/// <summary>What deleting a continent's baked tiles freed.</summary>
public sealed record CacheCleared(string Continent, long FreedBytes);
