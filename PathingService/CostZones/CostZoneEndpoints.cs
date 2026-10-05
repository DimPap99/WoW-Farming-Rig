using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

using PPather.Planning;

namespace PathingService.CostZones;

public static class CostZoneEndpoints
{
    public const string Tag = "Cost zones";

    public static IEndpointRouteBuilder MapCostZoneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(PathingApi.CostZonesReload, (RoutePlanner planner) => planner.ReloadCostZones()
                ? Results.NoContent()
                : Results.Problem(
                    detail: "at least one zone file is unreadable or invalid; the previous zones stay in effect",
                    statusCode: StatusCodes.Status409Conflict))
            .WithTags(Tag)
            .WithSummary("Re-read every authored zone file now")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        RouteGroupBuilder zones = app.MapGroup(PathingApi.CostZones).WithTags(Tag);

        zones.MapGet("{continent}", (string continent, CostZoneStore store) => store.Get(continent).ToHttp())
            .WithSummary("Every road and danger zone of a continent, by uiMap")
            .Produces<CostZoneSet>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        zones.MapPut($"{{continent}}/{PathingApi.Roads}/{{uiMapId:int}}",
                (string continent, int uiMapId, RoadZones roads, CostZoneStore store, RoutePlanner planner) =>
                    Saved(store.SaveRoads(continent, uiMapId, roads), planner))
            .WithSummary("Replace one map's roads; an empty list deletes them")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        zones.MapPut($"{{continent}}/{PathingApi.Danger}/{{uiMapId:int}}",
                (string continent, int uiMapId, DangerZones danger, CostZoneStore store, RoutePlanner planner) =>
                    Saved(store.SaveDanger(continent, uiMapId, danger), planner))
            .WithSummary("Replace one map's danger zones; empty lists delete them")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    // A save shapes the very next route. The file watcher would reload as well, half a
    // second later; that second reload is harmless.
    private static IResult Saved(StoreResult result, RoutePlanner planner)
    {
        if (result.Outcome == StoreOutcome.Done)
        {
            planner.ReloadCostZones();
        }

        return result.ToHttp();
    }
}
