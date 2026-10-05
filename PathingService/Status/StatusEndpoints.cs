using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Navmesh;

using PPather;
using PPather.Planning;

using System.Collections.Generic;
using System.Linq;

namespace PathingService.Status;

public static class StatusEndpoints
{
    public const string Tag = "Status";

    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PathingApi.Capabilities, (RoutePlanner planner, CoverageCache coverage, BakeAdmin admin) =>
                new PathingCapabilities(PathingApi.Version, planner.Client, planner.Era, RoutePlanner.PathsAreSmoothed,
                    [.. coverage.Read().Where(c => c.Tiles > 0).Select(c => c.Continent)], admin.HasGameFiles))
            .WithTags(Tag)
            .WithSummary("What this instance serves: client, era, continents with baked tiles, and whether game files are here");

        app.MapGet(PathingApi.Health, (RoutePlanner planner, CoverageCache coverage, BakeAdmin admin) =>
            {
                PPatherService.NavmeshCoverage[] counted = coverage.Read();
                IReadOnlyList<RoutePlanner.LaneStatus> lanes = planner.Lanes();

                return new PathingHealth(planner.Client, planner.Era,
                    [.. counted.Select(c => new ContinentCoverage(c.Continent, c.Tiles))],
                    [.. lanes.Select(l => new PlannerLane(l.Continent, l.ResidentTiles, l.Queries, l.LastMs))],
                    admin.Status);
            })
            .WithTags(Tag)
            .WithSummary("Baked tiles per continent, the continents the planner has open, and the bake");

        return app;
    }
}
