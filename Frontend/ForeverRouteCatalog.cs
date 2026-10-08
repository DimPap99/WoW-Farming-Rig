using ServiceClients;

using SharedLib;
using SharedLib.Forever;

using System.Collections.Generic;
using System.Linq;

namespace Frontend;

/// <summary>A route file as the runs page lists it: where it is, its name and steps, or why it is refused.</summary>
public sealed record ForeverRouteChoice(string FullPath, string File, string Name, IReadOnlyList<string> Steps,
    bool Repeat, string Error);

/// <summary>The route files a WoW: Forever run can start from.</summary>
public static class ForeverRouteCatalog
{
    /// <summary>
    /// The route files in <paramref name="folder"/>, as the Pathing Service's library lists them (<see cref="LibraryName.Files"/>),
    /// each loaded and checked as a run would.
    /// </summary>
    public static IReadOnlyList<ForeverRouteChoice> Load(string folder, WorldMapAreaDB maps)
    {
        List<ForeverRouteChoice> routes = [];
        foreach ((string file, string path) in LibraryName.Files(folder, nested: false))
        {
            routes.Add(ForeverRouteFile.TryLoad(path, maps, out ForeverRoute route, out string error)
                ? new(path, file, route.Name, [.. route.Tasks.Select(task => task.Name)], route.Repeat, string.Empty)
                : new(path, file, file, [], false, error));
        }
        return routes;
    }
}
