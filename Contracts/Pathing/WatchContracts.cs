namespace Contracts.Pathing;

/// <summary>Colours on the watch hub; the 3D view and the map decode the same codes.</summary>
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
/// The methods of the watch hub (<see cref="PathingApi.WatchHub"/>). The hub speaks MessagePack, and a
/// point travels as <c>[x, y, z]</c> in world yards. A name is also the mesh or layer id, so a later
/// drawing with the same name replaces the earlier one.
/// </summary>
public static class WatchHubMethods
{
    /// <summary>To viewers: <c>(points[], color, name)</c>, a path.</summary>
    public const string DrawPath = "drawPath";

    /// <summary>To viewers: <c>(point, height, color, name)</c>, a vertical marker line.</summary>
    public const string DrawLine = "drawLine";

    /// <summary>To viewers: <c>(point, color, name)</c>, a sphere marker.</summary>
    public const string DrawSphere = "drawSphere";

    /// <summary>To viewers: <c>(name)</c>, removes one drawing.</summary>
    public const string RemoveMesh = "removeMesh";

    /// <summary>To viewers: wipes the scene.</summary>
    public const string Clear = "clear";

    /// <summary>To viewers: <c>(message)</c>, a status line.</summary>
    public const string Log = "log";

    /// <summary>
    /// To viewers: <c>(name, positions[], groundIndices[], liquidIndices[])</c>, a navmesh tile as
    /// triangles; positions are <c>x, y, z</c> triples in world yards.
    /// </summary>
    public const string AddNavmeshTile = "addNavmeshTile";

    /// <summary>
    /// To one viewer: <c>(name, positions[], terrain[], liquid[], objects[], models[])</c>, the game
    /// geometry under one navmesh tile, by kind; positions are <c>x, y, z</c> triples in world yards.
    /// </summary>
    public const string AddTerrainTile = "addTerrainTile";

    /// <summary>
    /// From a viewer: sends that viewer the navmesh tiles the planner holds and the last routes, so a
    /// view opened late shows what the others see.
    /// </summary>
    public const string Replay = "Replay";

    /// <summary>
    /// From a viewer: <c>(continent, [x0, z0, x1, z1, ...])</c>, the navmesh tiles to show the game
    /// geometry under. Answers null when the work started, or why it cannot; the meshes follow as
    /// <see cref="AddTerrainTile"/>. Needs game files where the service runs
    /// (<see cref="PathingCapabilities.GameFiles"/>).
    /// </summary>
    public const string Terrain = "Terrain";
}

/// <summary>Points to show as a path in every viewer, without planning a route.</summary>
/// <param name="Name">The drawing's id; a later one with the same name replaces it.</param>
/// <param name="UiMapId">The zone the points belong to; it picks the continent and, in map space, the conversion.</param>
/// <param name="Space">World yards, or the zone's map coordinates (heights then come from the navmesh).</param>
/// <param name="Points">The points, in order.</param>
/// <param name="Color">How it is drawn.</param>
public sealed record WatchPath(string Name, int UiMapId, CoordinateSpace Space, Point3[] Points,
    WatchColor Color = WatchColor.Orange);

/// <summary>A point to mark in every viewer.</summary>
public sealed record WatchMarker(string Name, int UiMapId, CoordinateSpace Space, Point3 At,
    WatchColor Color = WatchColor.Teal);
