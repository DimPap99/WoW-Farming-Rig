using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

namespace PathingService.World;

public static class WorldEndpoints
{
    public const string Tag = "World";

    public static IEndpointRouteBuilder MapWorldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PathingApi.World, (WorldCatalog world) => world.Info())
            .WithTags(Tag)
            .WithSummary("The served client's continents, zone maps (with the world rectangle each spans) and NPC types")
            .Produces<WorldInfo>();

        app.MapGet(PathingApi.Npcs, (int uiMapId, string? flag, WorldCatalog world) => world.Npcs(uiMapId, flag).ToHttp())
            .WithTags(Tag)
            .WithSummary("NPCs of one type (flag: vendor, repair, innkeeper, ...) whose first spawn is in the zone, in world yards")
            .Produces<NpcInfo[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}
