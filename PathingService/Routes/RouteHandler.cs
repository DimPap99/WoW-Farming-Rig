using Contracts;
using Contracts.Pathing;

using PPather.Navmesh;
using PPather.Planning;

using System;
using System.Numerics;
using System.Threading;

namespace PathingService.Routes;

/// <summary>
/// Checks a <see cref="RouteRequest"/>, asks the <see cref="RoutePlanner"/> and turns the
/// outcome into a <see cref="RouteResponse"/>. The planner speaks engine types and the wire
/// speaks contract types; this is the one place they meet.
/// </summary>
public sealed class RouteHandler(RoutePlanner planner)
{
    public RouteResponse Find(RouteRequest request, CancellationToken token)
    {
        if (Problem(request) is string problem)
        {
            return RouteResponse.Failed(RouteStatus.BadRequest, problem);
        }

        RouteTuning? tuning = request.Tuning;
        RouteQuery query = new(request.UiMapId,
            request.Space == CoordinateSpace.Map ? RouteSpace.Map : RouteSpace.World,
            ToVector(request.From), ToVector(request.To),
            new NavmeshQueryTuning(tuning?.JitterYards ?? 0f, tuning?.JitterSeed, tuning?.EdgeMarginYards),
            request.Search == RouteSearch.Continent ? RouteReach.Continent : RouteReach.Band);

        RouteOutcome outcome = planner.Find(query, token);

        Point3[] points = new Point3[outcome.Points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 p = outcome.Points[i];
            points[i] = new Point3(p.X, p.Y, p.Z);
        }

        return new RouteResponse(ToStatus(outcome.Status), points,
            points.Length > 0 && RoutePlanner.PathsAreSmoothed, outcome.ElapsedMs, outcome.Detail);
    }

    private string? Problem(RouteRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Client) &&
            !request.Client.Equals(planner.Client, StringComparison.OrdinalIgnoreCase))
        {
            return $"this service serves '{planner.Client}', not '{request.Client}'";
        }

        if (!IsFinite(request.From) || !IsFinite(request.To))
        {
            return "points must be finite numbers";
        }

        return request.Tuning is RouteTuning tuning
            ? new NavmeshQueryTuning(tuning.JitterYards, tuning.JitterSeed, tuning.EdgeMarginYards).Problem()
            : null;
    }

    private static bool IsFinite(Point3 p)
    {
        return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    }

    private static Vector3 ToVector(Point3 p)
    {
        return new Vector3(p.X, p.Y, p.Z);
    }

    internal static RouteStatus ToStatus(RoutePlanStatus status)
    {
        return status switch
        {
            RoutePlanStatus.Found => RouteStatus.Found,
            RoutePlanStatus.Partial => RouteStatus.Partial,
            RoutePlanStatus.NoPath => RouteStatus.NoPath,
            RoutePlanStatus.NotBaked => RouteStatus.NotBaked,
            RoutePlanStatus.UnknownMap or RoutePlanStatus.Refused => RouteStatus.BadRequest,
            _ => RouteStatus.Unavailable,
        };
    }
}
