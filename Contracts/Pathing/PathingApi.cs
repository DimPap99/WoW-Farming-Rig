namespace Contracts.Pathing;

/// <summary>
/// The Pathing Service's routes. The service maps exactly these and the clients call exactly
/// these, so a renamed route breaks the build on both sides instead of at run time.
/// </summary>
public static class PathingApi
{
    /// <summary>The contract version the routes and types in this namespace describe.</summary>
    public const int Version = 1;

    /// <summary>Where the service listens unless told otherwise.</summary>
    public const int DefaultPort = 5010;

    public static readonly string DefaultUrl = $"http://localhost:{DefaultPort}";

    public const string Root = "/api/v1";

    /// <summary><c>POST</c> a <see cref="RouteRequest"/>; answers a <see cref="RouteResponse"/>.</summary>
    public const string FindRoute = Root + "/routes/find";

    /// <summary><c>GET</c> the <see cref="PathingCapabilities"/>.</summary>
    public const string Capabilities = Root + "/capabilities";

    /// <summary><c>GET</c> the <see cref="PathingHealth"/>.</summary>
    public const string Health = Root + "/health";

    /// <summary>
    /// <c>GET</c> lists <see cref="RecordedPathInfo"/> (optional <c>?filter=</c>); <c>GET</c>,
    /// <c>PUT</c> and <c>DELETE</c> under <c>{Paths}/{name}</c> work on one path, where
    /// <c>name</c> is relative to the path folder and may include subfolders.
    /// </summary>
    public const string Paths = Root + "/paths";

    /// <summary><c>GET ?mapId=&amp;x=&amp;y=</c>: the <see cref="AreaAtPoint"/> at a world point.</summary>
    public const string Area = Root + "/area";

    /// <summary>
    /// The WoW: Forever route library: <c>GET</c> lists <see cref="ForeverRouteInfo"/>; <c>GET</c>
    /// (a <see cref="ForeverRouteDocument"/>), <c>PUT</c> (a <see cref="ForeverRouteText"/>) and
    /// <c>DELETE</c> under <c>{ForeverRoutes}/{name}</c> work on one route file, where <c>name</c> is
    /// a file name in the route folder. Routes are checked against Forever's map data whatever
    /// client the service serves.
    /// </summary>
    public const string ForeverRoutes = Root + "/forever/routes";

    /// <summary><c>POST</c> a <see cref="ForeverRouteDraft"/>: what a run makes of the text, without saving it.</summary>
    public const string ForeverRouteCheck = ForeverRoutes + "/check";

    /// <summary>
    /// <c>GET {CostZones}/{continent}</c> answers a <see cref="CostZoneSet"/>;
    /// <c>PUT {CostZones}/{continent}/{Roads|Danger}/{uiMapId}</c> saves one map's zones.
    /// </summary>
    public const string CostZones = Root + "/costzones";

    /// <summary><c>POST</c>: re-read every authored zone file now.</summary>
    public const string CostZonesReload = CostZones + "/reload";

    /// <summary>The cost-zone route segment for roads.</summary>
    public const string Roads = "roads";

    /// <summary>The cost-zone route segment for danger zones.</summary>
    public const string Danger = "danger";

    /// <summary><c>GET</c> the <see cref="WorldInfo"/>: the served client's continents, zones and NPC types.</summary>
    public const string World = Root + "/world";

    /// <summary><c>GET ?uiMapId=&amp;flag=</c>: the <see cref="NpcInfo"/> of one NPC type whose spawn is in a zone.</summary>
    public const string Npcs = World + "/npcs";

    /// <summary><c>GET ?mapId=&amp;name=</c>: the <see cref="NpcInfo"/> of creatures on a continent whose name holds the text.</summary>
    public const string NpcSearch = Npcs + "/search";

    /// <summary><c>GET ?uiMapId=&amp;layer=</c>: the <see cref="SpawnPoint"/>s of one of the <see cref="SpawnLayers"/> in a zone.</summary>
    public const string Spawns = World + "/spawns";

    /// <summary><c>GET</c> the <see cref="NamedLocation"/>s the route search offers.</summary>
    public const string Locations = Root + "/catalog/locations";

    /// <summary><c>GET</c> the <see cref="TestRoute"/>s: known trouble cases, ready to send.</summary>
    public const string TestRoutes = Root + "/catalog/testroutes";

    /// <summary>
    /// <c>GET {Navmesh}/{continent}/{Tiles}</c> lists the baked <see cref="NavmeshTile"/>s;
    /// <c>DELETE {Navmesh}/{continent}</c> deletes them and answers <see cref="CacheCleared"/>.
    /// </summary>
    public const string Navmesh = Root + "/navmesh";

    /// <summary>
    /// The navmesh route segment that lists a continent's tiles; <c>GET {Navmesh}/{continent}/{Tiles}/{x}/{z}</c>
    /// answers one tile's <see cref="NavmeshTileGeometry"/>.
    /// </summary>
    public const string Tiles = "tiles";

    /// <summary><c>GET {Navmesh}/{continent}/{Overview}</c>: the continent's <see cref="NavmeshOverview"/>.</summary>
    public const string Overview = "overview";

    /// <summary>
    /// The one navmesh bake the service runs at a time: <c>GET</c> its <see cref="BakeStatus"/>,
    /// <c>POST</c> a <see cref="BakeRequest"/> to start one.
    /// </summary>
    public const string Bake = Root + "/bake";

    /// <summary><c>POST</c>: cancels the running bake; tiles already written stay.</summary>
    public const string BakeCancel = Bake + "/cancel";

    /// <summary><c>POST ?continent=</c>: extracts the area-id grid from the game files, in the background.</summary>
    public const string AreaGrids = Root + "/areagrids";

    /// <summary>Drawing for the watch hub's viewers without planning anything.</summary>
    public const string Watch = Root + "/watch";

    /// <summary><c>POST</c> a <see cref="WatchPath"/>: shows its points as a path in every viewer.</summary>
    public const string WatchPaths = Watch + "/paths";

    /// <summary><c>POST</c> a <see cref="WatchMarker"/>: marks a point in every viewer.</summary>
    public const string WatchMarkers = Watch + "/markers";

    /// <summary><c>POST</c>: wipes every viewer's scene.</summary>
    public const string WatchClear = Watch + "/clear";

    /// <summary>
    /// The SignalR hub (MessagePack) that streams routes and navmesh tiles to the 3D view and the
    /// map; its methods are <see cref="WatchHubMethods"/>. Outside <see cref="Root"/> because the
    /// browser connects to it by this path.
    /// </summary>
    public const string WatchHub = "/watchHub";

    /// <summary>The Swagger UI and its OpenAPI document.</summary>
    public const string Swagger = "/swagger";
}
