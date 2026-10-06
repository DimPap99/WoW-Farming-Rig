using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PPather.Planning;

using SharedLib;

using System.Numerics;

namespace PathingService.Watch;

public static class WatchEndpoints
{
    public const string Tag = "Watch";

    public static IEndpointRouteBuilder MapWatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHub<WatchHub>(PathingApi.WatchHub);

        app.MapPost(PathingApi.WatchPaths, (WatchPath path, WorldMapAreaDB maps, RoutePlanner planner, WatchBroadcaster watch) =>
            {
                if (WatchDrawing.Problem(path.Name, path.UiMapId, path.Points ?? [], path.Space, path.Color, maps, out WorldMapArea zone) is string problem)
                {
                    return Results.Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest, title: "Request refused");
                }

                watch.Draw(path.Name, WatchDrawing.ToWorld(path.Points, path.Space, zone, planner), path.Color);
                return Results.Accepted();
            })
            .WithTags(Tag)
            .WithSummary("Show points as a path in every viewer of " + PathingApi.WatchHub + ", without planning")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost(PathingApi.WatchMarkers, (WatchMarker marker, WorldMapAreaDB maps, RoutePlanner planner, WatchBroadcaster watch) =>
            {
                if (WatchDrawing.Problem(marker.Name, marker.UiMapId, [marker.At], marker.Space, marker.Color, maps, out WorldMapArea zone) is string problem)
                {
                    return Results.Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest, title: "Request refused");
                }

                Vector3[] at = WatchDrawing.ToWorld([marker.At], marker.Space, zone, planner);
                watch.Mark(marker.Name, at[0], marker.Color);
                return Results.Accepted();
            })
            .WithTags(Tag)
            .WithSummary("Mark a point in every viewer")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost(PathingApi.WatchClear, (WatchBroadcaster watch) =>
            {
                watch.Clear();
                return Results.Accepted();
            })
            .WithTags(Tag)
            .WithSummary("Wipe every viewer's scene")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}
