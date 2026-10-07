using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ServiceClients.Routes;

/// <summary>The kinds of step a Forever route file has: each step is one of these fields.</summary>
public enum ForeverStepKind
{
    Travel,
    Roam,
    Visit,
    Quest
}

/// <summary>One step of a Forever route as its text writes it.</summary>
/// <param name="UiMapId">The step's own map, else the route's.</param>
/// <param name="Points">A travel's points (the Via ones, then To) or a roam's corners: zone map coordinates, 0 to 100.</param>
/// <param name="Kills">A roam's or a quest's Until kills; 0 when it gives none.</param>
/// <param name="Visit">Who or what a visit goes to; null when it names none.</param>
/// <param name="Settings">The step's other settings, as the file names them (<c>Pulls</c>, <c>Seed</c>, <c>Minutes</c>, ...).</param>
public sealed record ForeverStepSketch(ForeverStepKind Kind, int UiMapId, Point2[] Points, int Kills = 0, StopTarget? Visit = null,
    string[]? Settings = null)
{
    public bool Travel => Kind == ForeverStepKind.Travel;
}

/// <summary>What a Forever route's text says: its name, its steps, whether it repeats, and whether its run plans with the cost zones.</summary>
public sealed record ForeverSketch(string? Name, ForeverStepSketch[] Steps, bool Repeat, CostZoneUse CostZones);

/// <summary>
/// What a Forever route's text says where, read leniently - comments, trailing commas, any order of fields
/// - for drawing it while it is written and for opening it as a design. Whether a run would load it is the
/// Pathing Service's to say (<c>CheckForeverRouteAsync</c>), as it checks with the run's own loader; this
/// reads only enough to draw and to design.
/// </summary>
public static class ForeverRouteSketch
{
    /// <summary>The route file's field that says whether its run plans with the cost zones (SharedLib's loader reads it too).</summary>
    public const string CostZonesField = "CostZones";

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The route as its text writes it, or false with where the text stops being readable.</summary>
    public static bool TryRead(string text, out ForeverSketch sketch, out string error)
    {
        sketch = new ForeverSketch(null, [], false, CostZoneUse.Ignore);
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, Lenient);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the text is not a route: it should be one { ... }";
                return false;
            }

            int routeMap = Int(root, "UIMapId") ?? 0;
            if (Property(root, "Steps") is not { ValueKind: JsonValueKind.Array } list)
            {
                error = "the route has no Steps yet";
                return false;
            }

            List<ForeverStepSketch> steps = [];
            foreach (JsonElement step in list.EnumerateArray())
            {
                if (Step(step, routeMap) is ForeverStepSketch read)
                {
                    steps.Add(read);
                }
            }

            // A run plans with the cost zones only when the route says Apply.
            bool apply = Property(root, CostZonesField) is { ValueKind: JsonValueKind.String } zones &&
                         string.Equals(zones.GetString(), nameof(CostZoneUse.Apply), StringComparison.OrdinalIgnoreCase);
            sketch = new ForeverSketch(Property(root, "Name") is { ValueKind: JsonValueKind.String } name ? name.GetString() : null,
                [.. steps], Property(root, "Repeat") is { ValueKind: JsonValueKind.True }, apply ? CostZoneUse.Apply : CostZoneUse.Ignore);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"not readable yet, line {(ex.LineNumber ?? 0) + 1}: {ex.Message.Split('.')[0]}";
            return false;
        }
    }

    // A step by the one field it has; null for one this cannot read.
    private static ForeverStepSketch? Step(JsonElement step, int routeMap)
    {
        if (Property(step, nameof(ForeverStepKind.Travel)) is { ValueKind: JsonValueKind.Object } travel)
        {
            List<Point2> points = [.. Points(Property(travel, "Via"))];
            if (Point(Property(travel, "To")) is Point2 to)
            {
                points.Add(to);
            }

            return new ForeverStepSketch(ForeverStepKind.Travel, Int(travel, "UIMapId") ?? routeMap, [.. points],
                Settings: Settings(travel, "Pulls"));
        }

        if (Property(step, nameof(ForeverStepKind.Roam)) is { ValueKind: JsonValueKind.Object } roam)
        {
            return new ForeverStepSketch(ForeverStepKind.Roam, Int(roam, "UIMapId") ?? routeMap, [.. Points(Property(roam, "Area"))], Kills(roam),
                Settings: [.. Settings(roam, "Pulls", "Seed"), .. Settings(Property(roam, "Until") ?? default, "Minutes", "Level")]);
        }

        if (Property(step, nameof(ForeverStepKind.Visit)) is { ValueKind: JsonValueKind.Object } visit)
        {
            StopTarget? who = Text(visit, "Npc") is string npc ? new StopTarget(Npc: npc)
                : Text(visit, "Role") is string role ? new StopTarget(Role: role)
                : Int(visit, "QuestStart") is int start ? new StopTarget(QuestStart: start)
                : Int(visit, "QuestEnd") is int end ? new StopTarget(QuestEnd: end) : null;
            return new ForeverStepSketch(ForeverStepKind.Visit, routeMap, [], Visit: who, Settings: Settings(visit, "Pulls", "Title"));
        }

        return Property(step, nameof(ForeverStepKind.Quest)) is { ValueKind: JsonValueKind.Object } quest
            ? new ForeverStepSketch(ForeverStepKind.Quest, routeMap, [], Kills(quest))
            : null;
    }

    // Which of these settings a step gives, by their names in the file.
    private static string[] Settings(JsonElement element, params string[] names)
    {
        return [.. names.Where(n => Property(element, n) is not null)];
    }

    private static int Kills(JsonElement step)
    {
        return Property(step, "Until") is { } until ? Int(until, "Kills") ?? 0 : 0;
    }

    // A name the way the run's loader takes it: case does not matter.
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static int? Int(JsonElement element, string name)
    {
        return Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int number) ? number : null;
    }

    private static string? Text(JsonElement element, string name)
    {
        return Property(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;
    }

    private static Point2? Point(JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.Array } xy && xy.GetArrayLength() == 2 &&
               xy[0].ValueKind == JsonValueKind.Number && xy[1].ValueKind == JsonValueKind.Number
            ? new Point2(xy[0].GetSingle(), xy[1].GetSingle())
            : null;
    }

    private static IEnumerable<Point2> Points(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } list)
        {
            yield break;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (Point(item) is Point2 point)
            {
                yield return point;
            }
        }
    }
}
