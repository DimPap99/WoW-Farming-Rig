using Contracts;

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ControlCenter.Routes;

/// <summary>One step of a Forever route as its text writes it: the points it names, on its map.</summary>
/// <param name="Travel">A Travel (its points: the Via ones, then To) rather than a Roam (its area's corners).</param>
/// <param name="UiMapId">The step's own map, else the route's.</param>
/// <param name="Points">Zone map coordinates, 0 to 100.</param>
public sealed record ForeverStepSketch(bool Travel, int UiMapId, Point2[] Points);

/// <summary>
/// What a Forever route's text says where, for drawing it while it is written: the steps' points,
/// read leniently - comments, trailing commas, any order of fields. Whether a run would load it is
/// the Pathing Service's to say (<c>CheckForeverRouteAsync</c>), as it checks with the run's own
/// loader; this reads only enough to draw.
/// </summary>
public static class ForeverRouteSketch
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The steps' points, or false with where the text stops being readable.</summary>
    public static bool TryRead(string text, out List<ForeverStepSketch> steps, out string error)
    {
        steps = [];
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

            foreach (JsonElement step in list.EnumerateArray())
            {
                if (Property(step, "Travel") is { ValueKind: JsonValueKind.Object } travel)
                {
                    List<Point2> points = [.. Points(Property(travel, "Via"))];
                    if (Point(Property(travel, "To")) is Point2 to)
                    {
                        points.Add(to);
                    }

                    steps.Add(new ForeverStepSketch(true, Int(travel, "UIMapId") ?? routeMap, [.. points]));
                }
                else if (Property(step, "Roam") is { ValueKind: JsonValueKind.Object } roam)
                {
                    steps.Add(new ForeverStepSketch(false, Int(roam, "UIMapId") ?? routeMap, [.. Points(Property(roam, "Area"))]));
                }
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"not readable yet, line {(ex.LineNumber ?? 0) + 1}: {ex.Message.Split('.')[0]}";
            return false;
        }
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
