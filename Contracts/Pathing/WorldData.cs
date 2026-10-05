namespace Contracts.Pathing;

/// <summary>
/// Where the world data files are served, as the map script fetches them. The Pathing Service
/// serves them from its data folder, BlazorServer from the bot's, and the Control Center forwards
/// exactly these prefixes to the service, so a browser on either host reads them same-origin.
/// </summary>
public static class WorldData
{
    /// <summary>Minimap tiles: <c>{Tiles}/{continent}/z{z}x{x}y{y}.webp</c>.</summary>
    public const string Tiles = "/tiles";

    /// <summary>The client's DBC exports: <c>WorldMapArea.json</c>, <c>creatures.json</c> and the rest.</summary>
    public const string Dbc = "/dbc";

    /// <summary>Zone data by area id: <c>{Area}/{areaId}.json</c>.</summary>
    public const string Area = "/area";

    /// <summary>NPC spawn positions by instance map: <c>{NpcSpawnLocations}/{mapId}.json</c>.</summary>
    public const string NpcSpawnLocations = "/npcspawnlocations";

    /// <summary>Mailbox positions by instance map: <c>{MailboxLocations}/{mapId}.json</c>.</summary>
    public const string MailboxLocations = "/mailboxlocations";

    /// <summary>Recorded path files and their screenshots, by their library name.</summary>
    public const string RecordedPaths = "/path";

    /// <summary>Named places: <c>{Locations}/teleport_locations.txt</c>.</summary>
    public const string Locations = "/locations";

    /// <summary>Every prefix above.</summary>
    public static readonly string[] All = [Tiles, Dbc, Area, NpcSpawnLocations, MailboxLocations, RecordedPaths, Locations];
}
