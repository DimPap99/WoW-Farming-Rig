using Newtonsoft.Json;

using SharedLib.Data;

using System;
using System.Collections.Generic;
using System.IO;

namespace SharedLib.Forever;

/// <summary>A route loaded from a file: its tasks in order, and whether it starts over after the last.</summary>
/// <param name="ApplyCostZones">Its run plans with the era's authored cost zones (roads, danger zones); without
/// them unless the file says <c>"CostZones": "Apply"</c>.</param>
public sealed record ForeverRoute(string Name, IReadOnlyList<ForeverTask> Tasks, bool Repeat, bool ApplyCostZones = false);

/// <summary>
/// Route files: the agenda as data, kept in <c>Json/path/_forever</c>. A route
/// is a list of steps on one zone map, in the coordinates a player reads (0 to
/// 100), each step a <c>Travel</c> or a <c>Roam</c>, or one that names its place
/// by who or what is there - a <c>Visit</c> (the nearest vendor, an NPC, where a
/// quest is given) or a <c>Quest</c> (roam where a quest's creatures are), found
/// when the step begins; a step may name another map. The file loads into the
/// same tasks the <see cref="ForeverTasks"/> API builds, and a mistake is refused
/// with the step it is in. A field the format does not know is refused too, so a
/// misspelt name never silently does nothing. <c>"CostZones": "Apply"</c> has
/// the run plan with the authored cost zones (<see cref="ForeverRoute.ApplyCostZones"/>).
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
        if (!TryCostZones(parsed.CostZones, out bool applyCostZones))
        {
            error = $"{name}: CostZones is \"{ApplyCostZones}\" or \"{IgnoreCostZones}\", not {parsed.CostZones}";
            return false;
        }
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
        route = new(name, tasks, parsed.Repeat, applyCostZones);
        error = string.Empty;
        return true;
    }

    /// <summary>The <c>CostZones</c> that has a run plan with the authored cost zones; Contracts' <c>CostZoneUse</c> names it so too.</summary>
    public const string ApplyCostZones = "Apply";

    /// <summary>The <c>CostZones</c> that has a run plan without them, as one that gives none does.</summary>
    public const string IgnoreCostZones = "Ignore";

    // Whether a run plans with the cost zones: Apply or Ignore by name, in any case; none is Ignore.
    private static bool TryCostZones(string? given, out bool apply)
    {
        apply = string.Equals(given, ApplyCostZones, StringComparison.OrdinalIgnoreCase);
        return given is null || apply || string.Equals(given, IgnoreCostZones, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The roles a <c>Visit</c> names most, for the message that refuses another.</summary>
    public const string CommonRoles = "vendor, repair, innkeeper, flightmaster, trainer, classtrainer, professiontrainer, banker or stablemaster";

    private static bool TryStep(RouteStep step, int routeMap, WorldMapAreaDB maps, out ForeverTask task, out string why)
    {
        task = null!;
        int kinds = (step.Travel is null ? 0 : 1) + (step.Roam is null ? 0 : 1) + (step.Visit is null ? 0 : 1) + (step.Quest is null ? 0 : 1);
        if (kinds != 1)
        {
            why = "a step is one of a Travel, a Roam, a Visit or a Quest";
            return false;
        }
        if (step.Visit is { } visit)
            return TryVisit(visit, out task, out why);
        if (step.Quest is { } quest)
            return TryQuest(quest, out task, out why);
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
        if (!TryUntil(roam.Until, "a Roam", out ForeverUntil ends, out why))
            return false;
        task = ForeverTasks.RoamArea(shape, ends, roam.Pulls, roam.Seed);
        return true;
    }

    // Exactly one of who or what to visit; the place itself is found when the step begins.
    private static bool TryVisit(VisitStep visit, out ForeverTask task, out string why)
    {
        task = null!;
        int named = (visit.Role is null ? 0 : 1) + (visit.Npc is null ? 0 : 1) + (visit.QuestStart is null ? 0 : 1) +
                    (visit.QuestEnd is null ? 0 : 1);
        if (named != 1)
        {
            why = "a Visit names one of a Role, an Npc, a QuestStart or a QuestEnd";
            return false;
        }
        if (visit.Title is not null && (visit.Role is null || string.IsNullOrWhiteSpace(visit.Title)))
        {
            why = "a Visit's Title goes with a Role, and holds a word of the NPC's title";
            return false;
        }

        ForeverVisitTarget target;
        if (visit.Role is { } role)
        {
            string name = role.Trim().ToLowerInvariant();
            if (name == nameof(NpcFlags.None).ToLowerInvariant() || !NpcFlagsExtensions.ToDictionary().ContainsKey(name))
            {
                why = $"{role} is not an NPC role: {CommonRoles}";
                return false;
            }
            target = ForeverVisitTarget.Nearest(name, visit.Title?.Trim());
        }
        else if (visit.Npc is { } npc)
        {
            if (string.IsNullOrWhiteSpace(npc))
            {
                why = "a Visit's Npc needs the NPC's name";
                return false;
            }
            target = ForeverVisitTarget.Npc(npc.Trim());
        }
        else
        {
            int quest = visit.QuestStart ?? visit.QuestEnd!.Value;
            if (quest < 1)
            {
                why = "a quest id is 1 or more";
                return false;
            }
            target = visit.QuestStart is not null ? ForeverVisitTarget.QuestStart(quest) : ForeverVisitTarget.QuestEnd(quest);
        }

        task = ForeverTasks.Visit(target, visit.Pulls);
        why = string.Empty;
        return true;
    }

    private static bool TryQuest(QuestStep quest, out ForeverTask task, out string why)
    {
        task = null!;
        if (quest.Id < 1)
        {
            why = "a Quest needs its Id, 1 or more";
            return false;
        }
        if (!TryUntil(quest.Until, "a Quest", out ForeverUntil ends, out why))
            return false;
        task = ForeverTasks.QuestArea(quest.Id, ends, quest.Pulls, quest.Seed);
        return true;
    }

    // A step that hunts ends by kills, minutes or a level, or it would never end.
    private static bool TryUntil(UntilStep? until, string step, out ForeverUntil ends, out string why)
    {
        ends = ForeverUntil.Never;
        if (until is null || until.Kills is null && until.Minutes is null && until.Level is null)
        {
            why = $"{step} needs an Until with Kills, Minutes or Level, or it would never end";
            return false;
        }
        // NaN, Infinity and 1e400 read as numbers too, and none of them ends a step (known issue 69).
        if (until.Kills is < 1 || until.Minutes is { } minutes && !(double.IsFinite(minutes) && minutes > 0) || until.Level is < 1)
        {
            why = "Until needs Kills and Level of at least 1, and Minutes a number above 0";
            return false;
        }
        ends = new ForeverUntil(until.Kills, Minutes: until.Minutes, Level: until.Level);
        why = string.Empty;
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
        public string? CostZones { get; set; }
    }

    internal sealed class RouteStep
    {
        public TravelStep? Travel { get; set; }
        public RoamStep? Roam { get; set; }
        public VisitStep? Visit { get; set; }
        public QuestStep? Quest { get; set; }
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

    internal sealed class VisitStep
    {
        public string? Role { get; set; }
        public string? Title { get; set; }
        public string? Npc { get; set; }
        public int? QuestStart { get; set; }
        public int? QuestEnd { get; set; }
        public ForeverPulls Pulls { get; set; } = ForeverPulls.None;
    }

    internal sealed class QuestStep
    {
        public int Id { get; set; }
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
