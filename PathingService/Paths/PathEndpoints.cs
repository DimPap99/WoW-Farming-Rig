using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System.Globalization;

namespace PathingService.Paths;

public static class PathEndpoints
{
    public const string Tag = "Paths";

    public static IEndpointRouteBuilder MapPathEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder paths = app.MapGroup(PathingApi.Paths).WithTags(Tag);

        paths.MapGet("", (string? filter, RecordedPathStore store) => store.List(filter))
            .WithSummary("Recorded paths whose name contains the filter")
            .Produces<RecordedPathInfo[]>();

        paths.MapGet("{**name}", (string name, RecordedPathStore store) => store.Get(name).ToHttp())
            .WithSummary("One recorded path: zone map coordinates")
            .Produces<RecordedPath>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        paths.MapPut("{**name}", (string name, Point3[] points, RecordedPathStore store) => store.Save(name, points).ToHttp())
            .WithSummary("Create or replace a recorded path, in the format the bot reads; 409 for a name another path has in another case")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        paths.MapDelete("{**name}", (string name, RecordedPathStore store) => store.Delete(name).ToHttp())
            .WithSummary("Delete a recorded path; its screenshot is kept")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet(PathingApi.Area, (int mapId, float x, float y, RoutePlanner planner, WorldMapAreaDB maps) =>
            {
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    return Results.Problem(detail: "x and y must be finite numbers", statusCode: StatusCodes.Status400BadRequest);
                }

                if (!NavmeshCoords.InWorld(x, y))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: string.Create(CultureInfo.InvariantCulture,
                        $"x and y must be within {NavmeshCoords.WorldEdge:F2} yards of 0, the edge of the world"));
                }

                (int areaId, float z) = planner.GetAreaAndHeight(mapId, x, y);
                int uiMapId = maps.TryGetZoneOfArea(areaId, out WorldMapArea zone) ? zone.UIMapId : 0;
                return Results.Ok(new AreaAtPoint(areaId, z, uiMapId));
            })
            .WithTags(Tag)
            .WithSummary("Area id, its zone map and the walkable height at a world point of a continent (mapId), from baked data only")
            .Produces<AreaAtPoint>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}
