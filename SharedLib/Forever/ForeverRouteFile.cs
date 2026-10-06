using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;

namespace SharedLib.Forever;

/// <summary>A route loaded from a file: its tasks in order, and whether it starts over after the last.</summary>
public sealed record ForeverRoute(string Name, IReadOnlyList<ForeverTask> Tasks, bool Repeat);

/// <summary>
/// Route files: the agenda as data, kept in <c>Json/path/_forever</c>. A route
/// is a list of steps on one zone map, in the coordinates a player reads (0 to
/// 100), each step a <c>Travel</c> or a <c>Roam</c>; a step may name another
/// map. The file loads into the same tasks the <see cref="ForeverTasks"/> API
/// builds, and a mistake is refused with the step it is in. A field the format
/// does not know is refused too, so a misspelt name never silently does nothing.
/// </summary>
public static class ForeverRouteFile
{
    /// <summary>The folder under <c>Json/path</c> that holds the route files (<see cref="DataConfig.ForeverRoutes"/>).</summary>
    public const string Folder = DataConfig.ForeverRouteFolder;

    private static readonly JsonSerializerSettings Settings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Error
    };

    /// <summary>The file <paramref name="name"/> names: a path as given when it exists, else one in the route folder.</summary>
    public static string Resolve(DataConfig data, string name) =>
        File.Exists(name) ? Path.GetFullPath(name) : Path.Combine(data.Path, Folder, name);

    public static bool TryLoad(string path, WorldMapAreaDB maps, out ForeverRoute route, out string error)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            route = null!;
            error = $"{Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
        return TryParse(text, Path.GetFileName(path), maps, out route, out error);
    }

    /// <summary>
    /// Reads a route from <paramref name="text"/>, the contents of the file
    /// <paramref name="fileName"/>, exactly as <see cref="TryLoad"/> reads the file: a
    /// route the Control Center is about to save is checked by the same rules a run applies.
    /// </summary>
    public static bool TryParse(string text, string fileName, WorldMapAreaDB maps, out ForeverRoute route, out string error)
    {
        route = null!;
        string file = Path.GetFileName(fileName);
        RouteFile? parsed;
        try
        {
            parsed = JsonConvert.DeserializeObject<RouteFile>(text, Settings);
        }
        catch (JsonException ex)
        {
            error = $"{file}: {ex.Message}";
            return false;
        }
        if (parsed?.Steps is not { Count: > 0 } steps)
        {
            error = $"{file} has no steps";
            return false;
        }
        string name = string.IsNullOrWhiteSpace(parsed.Name) ? Path.GetFileNameWithoutExtension(file) : parsed.Name;
        List<ForeverTask> tasks = new(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            if (!TryStep(steps[i], parsed.UIMapId, maps, out ForeverTask task, out string why))
            {
                error = $"{name}, step {i + 1}: {why}";
                return false;
            }
            tasks.Add(task);
        }
        route = new(name, tasks, parsed.Repeat);
        error = string.Empty;
        return true;
    }

    private static bool TryStep(RouteStep step, int routeMap, WorldMapAreaDB maps, out ForeverTask task, out string why)
    {
        task = null!;
        if ((step.Travel is null) == (step.Roam is null))
        {
            why = "a step is either a Travel or a Roam";
            return false;
        }
        if (step.Travel is { } travel)
        {
            int map = travel.UIMapId ?? routeMap;
            if (!TryPoint(travel.To, map, maps, "To", out ForeverMapPoint to, out why))
                return false;
            List<ForeverMapPoint> via = [];
            foreach (double[] point in travel.Via ?? [])
            {
                if (!TryPoint(point, map, maps, "Via", out ForeverMapPoint on, out why))
                    return false;
                via.Add(on);
            }
            task = ForeverTasks.TravelTo(to, via, travel.Pulls);
            return true;
        }

        RoamStep roam = step.Roam!;
        int roamMap = roam.UIMapId ?? routeMap;
        if (roam.Area is not { Count: >= 2 } area)
        {
            why = "a Roam needs an Area: two opposite corners, or three or more in order";
            return false;
        }
        List<(double X, double Y)> corners = new(area.Count);
        foreach (double[] corner in area)
        {
            if (!TryPoint(corner, roamMap, maps, "Area", out ForeverMapPoint at, out why))
                return false;
            corners.Add((at.X, at.Y));
        }
        ForeverArea shape = ForeverArea.FromCorners(roamMap, corners);
        if (!shape.TryToWorld(maps, out _, out _, out why))
            return false;
        if (roam.Until is not { } until || until.Kills is null && until.Minutes is null && until.Level is null)
        {
            why = "a Roam needs an Until with Kills, Minutes or Level, or it would never end";
            return false;
        }
        if (until.Kills is < 1 || until.Minutes is <= 0 || until.Level is < 1)
        {
            why = "Until needs Kills and Level of at least 1, and Minutes above 0";
            return false;
        }
        task = ForeverTasks.RoamArea(shape, new ForeverUntil(until.Kills, Minutes: until.Minutes, Level: until.Level),
            roam.Pulls, roam.Seed);
        return true;
    }

    private static bool TryPoint(double[]? xy, int uiMapId, WorldMapAreaDB maps, string field,
        out ForeverMapPoint point, out string why)
    {
        point = default;
        if (xy is not [double x, double y])
        {
            why = $"{field} needs a point as [x, y]";
            return false;
        }
        point = new(uiMapId, x, y);
        return point.TryToWorld(maps, out _, out _, out why);
    }

    // The file's shape; the property names are the file format.
    internal sealed class RouteFile
    {
        public string? Name { get; set; }
        public int UIMapId { get; set; }
        public List<RouteStep>? Steps { get; set; }
        public bool Repeat { get; set; }
    }

    internal sealed class RouteStep
    {
        public TravelStep? Travel { get; set; }
        public RoamStep? Roam { get; set; }
    }

    internal sealed class TravelStep
    {
        public int? UIMapId { get; set; }
        public double[]? To { get; set; }
        public List<double[]>? Via { get; set; }
        public ForeverPulls Pulls { get; set; } = ForeverPulls.None;
    }

    internal sealed class RoamStep
    {
        public int? UIMapId { get; set; }
        public List<double[]>? Area { get; set; }
        public UntilStep? Until { get; set; }
        public ForeverPulls Pulls { get; set; } = ForeverPulls.Any;
        public int? Seed { get; set; }
    }

    internal sealed class UntilStep
    {
        public int? Kills { get; set; }
        public double? Minutes { get; set; }
        public int? Level { get; set; }
    }
}
