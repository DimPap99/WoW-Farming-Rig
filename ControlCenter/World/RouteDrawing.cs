using Contracts;
using Contracts.Pathing;

using ControlCenter.Components.Shared;
using ServiceClients.Routes;

using System.Collections.Generic;

namespace ControlCenter.World;

/// <summary>A route as the world view draws it, by how it ended.</summary>
public static class RouteDrawing
{
    /// <summary>
    /// A route's lines: found, in its colour; part of the way, as far as it goes, then a dashed gap
    /// to where it was going, with a stop marker added to <paramref name="markers"/>; none at all,
    /// a dashed straight line; still being planned (no status yet), a faint dashed one.
    /// </summary>
    /// <param name="name">What the tooltips call it: "Leg 2 to 3".</param>
    public static List<WorldRoute> Of(string id, string name, RouteStatus? status, IReadOnlyList<Point3> points,
        Point3 from, Point3 to, string? detail, List<WorldMarker> markers)
    {
        if (status == RouteStatus.Found && points.Count > 0)
        {
            return [WorldRoute.Of(id, WorldKinds.Found, points, $"{name}: {Words.Yards(RouteGeometry.Length(points))}")];
        }

        if (status == RouteStatus.Partial && points.Count > 0)
        {
            Point3 last = points[^1];
            markers.Add(WorldMarker.At($"{id}-stop", WorldKinds.Stop, last, $"{name} stops"));
            return
            [
                WorldRoute.Of(id, WorldKinds.Partial, points, $"{name}: stops {Words.Yards(RouteGeometry.Distance(last, to))} short"),
                WorldRoute.Of($"{id}-gap", WorldKinds.Gap, [last, to with { Z = to.Z == 0 ? last.Z : to.Z }], $"{name}: no way found here"),
            ];
        }

        return status is null
            ? [WorldRoute.Of(id, WorldKinds.Pending, [from, to], $"{name}: planning")]
            : [WorldRoute.Of(id, WorldKinds.Failed, [from, to], $"{name}: {detail ?? status.ToString()}")];
    }
}
