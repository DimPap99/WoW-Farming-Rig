using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using PathingService.Hosting;
using PathingService.Status;

using PPather;
using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System.Linq;

namespace PathingService.Navmesh;

public static class NavmeshEndpoints
{
    public const string Tag = "Navmesh";

    public static IEndpointRouteBuilder MapNavmeshEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder navmesh = app.MapGroup(PathingApi.Navmesh).WithTags(Tag);

        navmesh.MapGet($"{{continent}}/{PathingApi.Tiles}",
                (string continent, DataConfig data, IOptions<NavmeshBakeOptions> bake, RoutePlanner planner, CoverageCache coverage) =>
                {
                    if (!IsContinent(continent, coverage))
                    {
                        return NotAContinent(continent, data);
                    }

                    PPatherService.NavmeshTileInfo[] tiles = PPatherService.GetNavmeshTiles(data, bake.Value, continent,
                        planner.ResidentTileSet(continent));
                    return Results.Ok(tiles.Select(t => new NavmeshTile(t.X, t.Z, t.MinX, t.MinY, t.MaxX, t.MaxY, t.Resident)).ToArray());
                })
            .WithSummary("Every baked tile of a continent with its world bounds; Resident marks those the planner holds")
            .Produces<NavmeshTile[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        navmesh.MapGet($"{{continent}}/{PathingApi.Overview}", (string continent, DataConfig data, RoutePlanner planner, CoverageCache coverage) =>
                IsContinent(continent, coverage)
                    ? Results.Ok(NavmeshViews.Overview(planner, continent))
                    : NotAContinent(continent, data))
            .WithSummary("One walkable height per baked tile of a continent: a map's base at any zoom")
            .WithDescription("Read from the continent's tile graph, which the first request after a bake builds (about 10 s for Kalimdor).")
            .Produces<NavmeshOverview>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        navmesh.MapGet($"{{continent}}/{PathingApi.Tiles}/{{x:int}}/{{z:int}}",
                (string continent, int x, int z, DataConfig data, IOptions<NavmeshBakeOptions> bake, CoverageCache coverage) =>
                {
                    if (!IsContinent(continent, coverage))
                    {
                        return NotAContinent(continent, data);
                    }

                    return NavmeshViews.Tile(NavmeshSettings.CacheDirectory(data, bake.Value, continent), x, z) is NavmeshTileGeometry tile
                        ? Results.Ok(tile)
                        : Results.NotFound();
                })
            .WithSummary("One baked tile's walkable polygons, for drawing; 404 when it is not baked")
            .Produces<NavmeshTileGeometry>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        navmesh.MapDelete("{continent}", (string continent, BakeAdmin admin) => admin.Clear(continent).ToHttp())
            .WithSummary("Delete every baked tile of a continent, stale settings hashes included; refused while a bake runs")
            .Produces<CacheCleared>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet(PathingApi.Bake, (BakeAdmin admin) => admin.Status)
            .WithTags(Tag)
            .WithSummary("The running, or last, navmesh bake")
            .Produces<BakeStatus>();

        app.MapPost(PathingApi.Bake, (BakeRequest request, BakeAdmin admin) =>
            {
                StoreResult<BakeStatus> started = admin.Start(request);
                return started.Outcome == StoreOutcome.Done
                    ? Results.Accepted(PathingApi.Bake, started.Value)
                    : started.ToHttp();
            })
            .WithTags(Tag)
            .WithSummary("Start a background bake: one ADT, a continent, or every continent. Needs the game files")
            .WithDescription("Only ADTs the continent has terrain for are visited, so the open ocean costs nothing. " +
                "One ADT takes seconds, a continent tens of minutes. Progress is on GET " + PathingApi.Bake + ".")
            .Produces<BakeStatus>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost(PathingApi.BakeCancel, (BakeAdmin admin) => admin.Cancel().ToHttp())
            .WithTags(Tag)
            .WithSummary("Cancel the running bake; tiles already written stay")
            .Produces<BakeStatus>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost(PathingApi.AreaGrids, (string? continent, BakeAdmin admin) =>
            {
                StoreResult started = admin.StartAreaGrid(string.IsNullOrWhiteSpace(continent) ? null : continent);
                return started.Outcome == StoreOutcome.Done ? Results.Accepted() : started.ToHttp();
            })
            .WithTags(Tag)
            .WithSummary("Extract the area-id grid of a continent (or all) from the game files, in the background")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    // Continents are folder names: only the client's own are taken.
    private static bool IsContinent(string continent, CoverageCache coverage)
    {
        return coverage.Read().Any(c => c.Continent == continent);
    }

    private static IResult NotAContinent(string continent, DataConfig data)
    {
        return Results.Problem(detail: $"'{continent}' is not a continent of {data.Exp}",
            statusCode: StatusCodes.Status400BadRequest, title: "Request refused");
    }
}
