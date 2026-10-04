using Core.Forever;

using SharedLib;

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Frontend;

/// <summary>A route file as the runs page lists it: where it is, its name and steps, or why it is refused.</summary>
public sealed record ForeverRouteChoice(string FullPath, string File, string Name, IReadOnlyList<string> Steps,
    bool Repeat, string Error);

/// <summary>The route files a WoW: Forever run can start from.</summary>
public static class ForeverRouteCatalog
{
    /// <summary>The route files in <paramref name="folder"/> by name, each loaded and checked as a run would.</summary>
    public static IReadOnlyList<ForeverRouteChoice> Load(string folder, WorldMapAreaDB maps)
    {
        if (!Directory.Exists(folder))
            return [];
        List<ForeverRouteChoice> routes = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*.json").OrderBy(Path.GetFileName))
        {
            string file = Path.GetFileName(path);
            routes.Add(ForeverRouteFile.TryLoad(path, maps, out ForeverRoute route, out string error)
                ? new(path, file, route.Name, [.. route.Tasks.Select(task => task.Name)], route.Repeat, string.Empty)
                : new(path, file, file, [], false, error));
        }
        return routes;
    }
}
