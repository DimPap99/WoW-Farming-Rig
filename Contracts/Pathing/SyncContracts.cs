namespace Contracts.Pathing;

/// <summary>
/// The sets of authored files an agent keeps copies of (<see cref="PathingApi.Sync"/>). Each is a
/// folder of the data root, and a file's name in a set is its path in that folder, <c>/</c> between
/// subfolders.
/// </summary>
public static class SyncSets
{
    /// <summary>Recorded paths and path groups (<c>Json/path</c>, without the Forever route folder).</summary>
    public const string Paths = "paths";

    /// <summary>WoW: Forever's route files (<c>Json/path/_forever</c>).</summary>
    public const string ForeverRoutes = "forever-routes";

    /// <summary>The folder of the path library that holds the Forever route files: the one spelling of <c>_forever</c>.</summary>
    public const string ForeverRouteFolder = "_forever";

    /// <summary>One era's roads and danger zones (<c>Json/road/&lt;era&gt;</c>); the only set asked for by era.</summary>
    public const string CostZones = "costzones";

    public static readonly string[] All = [Paths, ForeverRoutes, CostZones];
}

/// <summary>One file of a sync set, as the service has it.</summary>
/// <param name="Name">Its path in the set's folder, <c>/</c> between subfolders.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">
/// The hash of its bytes with every CRLF read as LF, in lower-case hex, so a copy that differs only
/// in line endings (a Windows checkout) counts as the same file. <c>ServiceClients.Sync.SyncHash</c>
/// computes it, on both ends.
/// </param>
public sealed record SyncFile(string Name, long Bytes, string Sha256);

/// <summary>Every file of one sync set, by name.</summary>
/// <param name="Era">The era asked for; null for a set that has none.</param>
public sealed record SyncManifest(string Set, string? Era, SyncFile[] Files);
