using Contracts.Pathing;

using PathingService.Forever;
using PathingService.Hosting;
using PathingService.Paths;

using ServiceClients.Bots;
using ServiceClients.Sync;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PathingService.Sync;

/// <summary>
/// The authored files agents keep copies of (<see cref="SyncSets"/>), served exactly as stored: a
/// set's manifest (each file's name, size and <see cref="SyncHash"/>) and one file's bytes. An agent
/// compares its copies with the manifest and fetches what differs, so the bot goes on reading the
/// same files from its own disk. Each set is a folder of the data root, and its names are checked
/// by the rules that guard that folder's own endpoints: <see cref="PathName"/> for recorded paths,
/// <see cref="ForeverRouteLibrary.TryResolve"/> for route files, plain relative names for one era's
/// cost zones, whatever client this instance serves.
/// </summary>
public sealed class SyncLibrary(DataConfig dataConfig, ForeverRouteLibrary routes)
{
    /// <summary>A file is sent as it is stored, not as JSON.</summary>
    public const string ContentType = "application/octet-stream";

    /// <summary>Every file of <paramref name="set"/> that a request could name, sorted by name.</summary>
    public StoreResult<SyncManifest> Manifest(string set, string? era)
    {
        if (!TryFolder(set, era, out string folder, out string error))
        {
            return StoreResult<SyncManifest>.Invalid(error);
        }

        List<SyncFile> files = [];
        foreach ((string name, string file) in Files(set, folder))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return StoreResult<SyncManifest>.Unreadable($"{name} could not be read: {ex.Message}");
            }

            files.Add(new SyncFile(name, bytes.Length, SyncHash.Of(bytes)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return StoreResult<SyncManifest>.Done(new SyncManifest(set, era, [.. files]));
    }

    /// <summary>Where the file <paramref name="name"/> of <paramref name="set"/> is stored, once it is there.</summary>
    public StoreResult<string> Find(string set, string name, string? era)
    {
        if (!TryFolder(set, era, out string folder, out string error) || !TryResolve(set, folder, name, out string file, out error))
        {
            return StoreResult<string>.Invalid(error);
        }

        return File.Exists(file) ? StoreResult<string>.Done(file) : StoreResult<string>.NotFound;
    }

    private bool TryFolder(string set, string? era, out string folder, out string error)
    {
        folder = string.Empty;
        error = string.Empty;
        switch (set)
        {
            case SyncSets.Paths or SyncSets.ForeverRoutes when era is not null:
                error = $"the {set} set has no era";
                return false;
            case SyncSets.Paths:
                folder = Path.GetFullPath(dataConfig.Path);
                return true;
            case SyncSets.ForeverRoutes:
                folder = routes.Folder;
                return true;
            case SyncSets.CostZones when era is null || !DataConfig.Eras.Contains(era):
                error = $"the {set} set is asked for by era: one of {string.Join(", ", DataConfig.Eras)}";
                return false;
            case SyncSets.CostZones:
                folder = Path.GetFullPath(dataConfig.RoadOf(era));
                return true;
            default:
                error = $"'{set}' is not a sync set: one of {string.Join(", ", SyncSets.All)}";
                return false;
        }
    }

    // What a set offers is what a request could name; route files sit in one folder, the others nest.
    private IEnumerable<(string Name, string File)> Files(string set, string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        SearchOption depth = set == SyncSets.ForeverRoutes ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        foreach (string file in Directory.EnumerateFiles(folder, "*" + PathName.Extension, depth))
        {
            string name = PathName.Relative(folder, file);
            if (TryResolve(set, folder, name, out _, out _))
            {
                yield return (name, file);
            }
        }
    }

    private bool TryResolve(string set, string folder, string? name, out string file, out string error)
    {
        switch (set)
        {
            case SyncSets.Paths:
                // Refuses the Forever route folder too: those files are a set of their own.
                return PathName.TryResolve(folder, name, out _, out file, out error);
            case SyncSets.ForeverRoutes:
                return routes.TryResolve(name, out file, out error);
            default:
                file = string.Empty;
                if (!AssignmentRules.IsRelativeName(name))
                {
                    error = $"'{name}' is not a zone file name: continent/uiMap{PathName.Extension} with plain names";
                    return false;
                }

                file = Path.GetFullPath(Path.Join(folder, name));
                if (!PathName.IsUnder(folder, file))
                {
                    error = $"'{name}' is outside the cost zone folder";
                    return false;
                }

                error = string.Empty;
                return true;
        }
    }
}
