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
        app.MapPost(PathingApi.CostZonesReload, (RoutePlanner planner) => planner.ReloadCostZones(null, out string? failure)
                ? Results.NoContent()
                : Results.Problem(
                    detail: $"{failure}; that continent keeps its previous zones",
                    statusCode: StatusCodes.Status409Conflict, title: "Not reloaded"))
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
                    Saved(store.SaveRoads(continent, uiMapId, roads), continent, planner))
            .WithSummary("Replace one map's roads; an empty list deletes them")
            .WithDescription(SavedDescription)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        zones.MapPut($"{{continent}}/{PathingApi.Danger}/{{uiMapId:int}}",
                (string continent, int uiMapId, DangerZones danger, CostZoneStore store, RoutePlanner planner) =>
                    Saved(store.SaveDanger(continent, uiMapId, danger), continent, planner))
            .WithSummary("Replace one map's danger zones; empty lists delete them")
            .WithDescription(SavedDescription)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private const string SavedDescription = "204 when saved and applied. 409 when saved, but another file of the continent " +
        "fails to load, so the planner keeps the zones it had: the detail names that file and why.";

    // A save shapes the very next route, so its continent reloads at once; the file watcher
    // would reload as well, half a second later, which is harmless. A reload is all or nothing,
    // so one bad file elsewhere in the continent keeps this save from applying: say so.
    private static IResult Saved(StoreResult result, string continent, RoutePlanner planner)
    {
        if (result.Outcome == StoreOutcome.Done && !planner.ReloadCostZones(continent, out string? failure))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Saved, not applied",
                detail: $"saved, but routes keep {continent}'s previous zones, because a file there does not load: {failure}");
        }

        return result.ToHttp();
    }
}
