using System;

namespace Contracts.Pathing;

/// <summary>A recorded path in the library, without its points.</summary>
/// <param name="Name">Relative to the path folder, with <c>/</c> between subfolders; ends in <c>.json</c>.</param>
/// <param name="Bytes">File size.</param>
/// <param name="ModifiedUtc">Last write time.</param>
/// <param name="Screenshot">The screenshot saved beside it (same name, <c>.jpg</c>), if there is one.</param>
public sealed record RecordedPathInfo(string Name, long Bytes, DateTimeOffset ModifiedUtc, string? Screenshot);

/// <summary>A recorded path: zone map coordinates (0 to 100), as the bot's path files hold them.</summary>
public sealed record RecordedPath(string Name, Point3[] Points);

/// <summary>The area id and walkable height at a world point; 0 for what is not baked.</summary>
/// <param name="AreaId">The finest area the baked grid knows there: a zone, or a sub-area such as a town.</param>
/// <param name="UiMapId">The zone map the area lies in - its own, or a sub-area's parent zone's; 0 when not known.</param>
public sealed record AreaAtPoint(int AreaId, float Z, int UiMapId = 0);
