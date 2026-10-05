using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        app.MapGet(PathingApi.Capabilities, (RoutePlanner planner) =>
                new PathingCapabilities(PathingApi.Version, planner.Client, planner.Era, RoutePlanner.PathsAreSmoothed,
                    [.. planner.Coverage().Where(c => c.Tiles > 0).Select(c => c.Continent)]))
            .WithTags(Tag)
            .WithSummary("What this instance serves: client, era and continents with baked tiles");

        app.MapGet(PathingApi.Health, (RoutePlanner planner) =>
            {
                PPatherService.NavmeshCoverage[] coverage = planner.Coverage();
                IReadOnlyList<RoutePlanner.LaneStatus> lanes = planner.Lanes();

                return new PathingHealth(planner.Client, planner.Era,
                    [.. coverage.Select(c => new ContinentCoverage(c.Continent, c.Tiles))],
                    [.. lanes.Select(l => new PlannerLane(l.Continent, l.ResidentTiles, l.Queries, l.LastMs))]);
            })
            .WithTags(Tag)
            .WithSummary("Baked tiles per continent and the continents the planner has open");

        return app;
    }
}
