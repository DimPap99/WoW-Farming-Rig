namespace Contracts.Pathing;

/// <summary>
/// The Pathing Service's routes. The service maps exactly these and the clients call exactly
/// these, so a renamed route breaks the build on both sides instead of at run time.
/// </summary>
public static class PathingApi
{
    /// <summary>The contract version the routes and types in this namespace describe.</summary>
    public const int Version = 1;

    /// <summary>Where the service listens unless told otherwise. PathingAPI keeps 5001 while both exist.</summary>
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
}
