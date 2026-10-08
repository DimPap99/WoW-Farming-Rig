using Contracts.Pathing;

using PathingService.Hosting;
using PathingService.Navmesh;
using PathingService.Paths;

using ServiceClients;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PathingService.Forever;

/// <summary>
/// WoW: Forever's route files (<see cref="ForeverRouteFile.Folder"/> under the path folder), the
/// routes agents are assigned and the Control Center authors. Each is checked exactly as a run
/// loads it (<see cref="ForeverRouteFile.TryParse"/>), against Forever's own map data whatever
/// client this instance serves: <see cref="WorldMapAreaDB"/> is per instance, so nothing shared is
/// involved. Files are read and written as text, so the <c>//</c> comments that document a route
/// survive a save, and a route a run would refuse is never written.
/// </summary>
public sealed class ForeverRouteLibrary(DataConfig dataConfig)
{
    /// <summary>A route file is a few kilobytes; a guard against runaway requests.</summary>
    public const int MaxBytes = 256 * 1024;

    private const string MapDataFile = "WorldMapArea.json";

    private readonly Lock loading = new();
    private WorldMapAreaDB? maps;

    /// <summary>Where the route files are.</summary>
    public string Folder { get; } = Path.GetFullPath(dataConfig.ForeverRoutes);

    /// <summary>Every route file by name, each checked as a run would load it.</summary>
    public IReadOnlyList<ForeverRouteInfo> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        WorldMapAreaDB? db = Maps(out string? problem);
        List<ForeverRouteInfo> routes = [];
        foreach (string file in Directory.EnumerateFiles(Folder, "*" + PathName.Extension).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);
            // A file no request could name is not offered: nothing could open, save or assign it.
            if (TryResolve(name, out _, out _) && TryRead(file, out string text))
            {
                routes.Add(Describe(name, text, db, problem, new FileInfo(file)));
            }
        }

        return routes;
    }

    /// <summary>One route file's text, comments and all, and what a run makes of it.</summary>
    public StoreResult<ForeverRouteDocument> Get(string name)
    {
        if (!TryResolve(name, out string file, out string error))
        {
            return StoreResult<ForeverRouteDocument>.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return StoreResult<ForeverRouteDocument>.NotFound;
        }

        if (!TryRead(file, out string text))
        {
            return StoreResult<ForeverRouteDocument>.Unreadable($"{name} could not be read");
        }

        WorldMapAreaDB? db = Maps(out string? problem);
        return StoreResult<ForeverRouteDocument>.Done(new ForeverRouteDocument(name, text, Describe(name, text, db, problem, new FileInfo(file))));
    }

    /// <summary>
    /// Creates or replaces <paramref name="name"/> with <paramref name="text"/>, written exactly as
    /// given, once a run would load it; a refused route leaves the file as it was.
    /// </summary>
    public StoreResult Save(string name, string? text)
    {
        if (!TryResolve(name, out string file, out string error))
        {
            return StoreResult.Invalid(error);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return StoreResult.Invalid("a route file needs text");
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            return StoreResult.Invalid($"a route file is at most {MaxBytes / 1024} KB");
        }

        if (PathName.StoredSpelling(Folder, name) is string stored)
        {
            return StoreResult.Conflict(PathName.CaseTwinRefusal(name, stored));
        }

        if (Maps(out string? problem) is not { } db)
        {
            return StoreResult.Unreadable(problem!);
        }

        if (!TryParse(text, name, db, out _, out string refused))
        {
            return StoreResult.Invalid(refused);
        }

        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllText(file, text);
        return StoreResult.Done;
    }

    public StoreResult Delete(string name)
    {
        if (!TryResolve(name, out string file, out string error))
        {
            return StoreResult.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return StoreResult.NotFound;
        }

        File.Delete(file);
        return StoreResult.Done;
    }

    /// <summary>What a run would make of <paramref name="draft"/>'s text, saved as its name; nothing is written.</summary>
    public StoreResult<ForeverRouteInfo> Check(ForeverRouteDraft? draft)
    {
        if (!TryResolve(draft?.Name, out _, out string error))
        {
            return StoreResult<ForeverRouteInfo>.Invalid(error);
        }

        if (Maps(out string? problem) is not { } db)
        {
            return StoreResult<ForeverRouteInfo>.Unreadable(problem!);
        }

        return StoreResult<ForeverRouteInfo>.Done(Describe(draft!.Name, draft.Text ?? string.Empty, db, null, null));
    }

    /// <summary>
    /// The file a route <paramref name="name"/> refers to: one plain file name ending in
    /// <c>.json</c>, in <see cref="Folder"/>; false with a reason a person can act on.
    /// </summary>
    public bool TryResolve(string? name, out string file, out string error)
    {
        file = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "a route name is required";
            return false;
        }

        if (name.Length > PathName.MaxLength)
        {
            error = $"a route name is at most {PathName.MaxLength} characters";
            return false;
        }

        if (name.Contains('/') || !PathName.IsPlainSegment(name))
        {
            error = $"'{name}' is not a valid route name: use a plain file name such as my_route{PathName.Extension}";
            return false;
        }

        if (name.Length <= PathName.Extension.Length || !name.EndsWith(PathName.Extension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{name}' must end in {PathName.Extension}";
            return false;
        }

        file = Path.GetFullPath(Path.Join(Folder, name));
        if (!PathName.IsUnder(Folder, file))
        {
            error = $"'{name}' is outside the route folder";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static ForeverRouteInfo Describe(string name, string text, WorldMapAreaDB? db, string? mapProblem, FileInfo? saved)
    {
        long bytes = saved?.Length ?? 0;
        DateTimeOffset? modified = saved is null ? null : new DateTimeOffset(saved.LastWriteTimeUtc, TimeSpan.Zero);
        string untitled = Path.GetFileNameWithoutExtension(name);

        if (db is null)
        {
            return new ForeverRouteInfo(name, untitled, [], false, mapProblem, bytes, modified);
        }

        return TryParse(text, name, db, out ForeverRoute route, out string error)
            ? new ForeverRouteInfo(name, route.Name, [.. route.Tasks.Select(task => task.Name)], route.Repeat, null, bytes, modified)
            : new ForeverRouteInfo(name, untitled, [], false, error, bytes, modified);
    }

    // What a run makes of the text, and a text the reader cannot even read is refused with why: one
    // file must never fail the whole library, and with it every page that lists it (known issue 70).
    private static bool TryParse(string text, string name, WorldMapAreaDB db, out ForeverRoute route, out string error)
    {
        try
        {
            return ForeverRouteFile.TryParse(text, name, db, out route, out error);
        }
        catch (Exception ex)
        {
            route = null!;
            error = $"{name} cannot be read: {ex.Message}";
            return false;
        }
    }

    private static bool TryRead(string file, out string text)
    {
        try
        {
            text = File.ReadAllText(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
            return false;
        }
    }

    // Read on first use, and again after a failure: the data may arrive after the service started.
    private WorldMapAreaDB? Maps(out string? problem)
    {
        lock (loading)
        {
            if (maps is null)
            {
                DataConfig forever = new() { Root = dataConfig.Root, Exp = ForeverGameFiles.ForeverClient };
                try
                {
                    maps = new WorldMapAreaDB(forever);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
                {
                    problem = $"Forever's map data (dbc/{ForeverGameFiles.ForeverClient}/{MapDataFile}) could not be read, " +
                              $"so no route can be checked: {ex.Message}";
                    return null;
                }
            }

            problem = null;
            return maps;
        }
    }
}
