using System.Collections.Generic;

namespace Contracts.Pathing;

/// <summary>A continent of the served client and how much of it has a baked navmesh.</summary>
/// <param name="Name">As the navmesh and the cost zones name it: <c>Azeroth</c>, <c>Kalimdor</c>, <c>Expansion01</c>.</param>
/// <param name="MapId">The instance map id the game reports for it.</param>
/// <param name="Tiles">Baked navmesh tiles on disk.</param>
/// <param name="Art">Where its map art lies, when the service has it; null without.</param>
public sealed record ContinentInfo(string Name, int MapId, int Tiles, MapArt? Art = null);

/// <summary>
/// Where a continent's map art lies: the game's minimap, stitched into a pyramid of 256-pixel
/// tiles served at <c>/tiles/{continent}/z{z}x{x}y{y}.webp</c>, from <paramref name="MinZoom"/>
/// to <paramref name="MaxZoom"/>. At <paramref name="MaxZoom"/> an ADT (533.33 yards) is 512
/// pixels, and the picture's pixel (px, py) lies at world y = 17066.67 - (px / 512 +
/// <paramref name="Column"/>) x 533.33 and world x = 17066.67 - (py / 512 + <paramref name="Row"/>)
/// x 533.33; each zoom below halves the pixels.
/// </summary>
/// <param name="Column">The ADT column of the picture's left edge (Leaflet's <c>offset.min.y</c>).</param>
/// <param name="Row">The ADT row of its top edge (Leaflet's <c>offset.min.x</c>).</param>
/// <param name="Width">Its width at <paramref name="MaxZoom"/>, in pixels (Leaflet's <c>resX</c>).</param>
/// <param name="Height">Its height at <paramref name="MaxZoom"/>, in pixels (Leaflet's <c>resY</c>).</param>
public sealed record MapArt(int Column, int Row, int Width, int Height, int MinZoom, int MaxZoom);

/// <summary>
/// A zone map: what a route request's <c>UiMapId</c> names. The four bounds are the world
/// rectangle its map coordinates (0 to 100) span, as <c>WorldMapArea.json</c> holds them: map x
/// runs along world y from <paramref name="LocLeft"/> to <paramref name="LocRight"/>, and map y
/// along world x from <paramref name="LocTop"/> to <paramref name="LocBottom"/>.
/// </summary>
public sealed record ZoneInfo(int UiMapId, int AreaId, string Name, string Continent, int MapId,
    float LocLeft, float LocRight, float LocTop, float LocBottom);

/// <summary>What the served client's world is made of, for pickers and coordinate conversion.</summary>
/// <param name="Client">The client data served (<c>som</c>, <c>forever</c>, ...).</param>
/// <param name="Era">Its navmesh and minimap era.</param>
/// <param name="HasMap">Whether minimap art and a map configuration exist for the era.</param>
/// <param name="Continents">The client's continents.</param>
/// <param name="Zones">Every zone map, by name.</param>
/// <param name="NpcFlags">NPC types the map can show, lower-case name to flag bit.</param>
public sealed record WorldInfo(string Client, string Era, bool HasMap, ContinentInfo[] Continents, ZoneInfo[] Zones,
    Dictionary<string, uint> NpcFlags);

/// <summary>An NPC of the requested type and where it first spawns, in world yards.</summary>
public sealed record NpcInfo(int Entry, string Name, Point3 Position);

/// <summary>One thing at one place in a zone: a creature's spawn, a herb or a vein, a mailbox; in world yards.</summary>
/// <param name="Id">The creature's or the node's id; 0 for a mailbox.</param>
/// <param name="Detail">For people: "Elite, level 12-13".</param>
public sealed record SpawnPoint(int Id, string Name, Point3 At, string? Detail = null);

/// <summary>What <see cref="PathingApi.Spawns"/> lists in a zone.</summary>
public static class SpawnLayers
{
    /// <summary>Elite, rare and boss creatures, every spawn.</summary>
    public const string Elite = "elite";

    /// <summary>Herb nodes.</summary>
    public const string Herb = "herb";

    /// <summary>Mining veins.</summary>
    public const string Vein = "vein";

    /// <summary>Creatures that can be skinned, every spawn.</summary>
    public const string Skinnable = "skinnable";

    public const string Mailbox = "mailbox";

    public static readonly string[] All = [Elite, Herb, Vein, Skinnable, Mailbox];
}

/// <summary>A place the route search offers, in world yards on <paramref name="Continent"/>.</summary>
public sealed record NamedLocation(string Name, string Continent, int MapId, Point3 Position);

/// <summary>
/// A known trouble case collected while tuning the engine ("Z ..." starts or ends without a
/// height), as the requests to send.
/// </summary>
/// <param name="Zone">Groups the routes for people.</param>
/// <param name="Name">For people.</param>
/// <param name="Forward">The route.</param>
/// <param name="Reverse">The same route back, when the case is about direction.</param>
public sealed record TestRoute(string Zone, string Name, RouteRequest Forward, RouteRequest? Reverse);
