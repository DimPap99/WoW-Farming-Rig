using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

using ServiceHosting;

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

        app.MapGet(PathingApi.NpcSearch, (int mapId, string? name, WorldCatalog world) => world.Search(mapId, name).ToHttp())
            .WithTags(Tag)
            .WithSummary("Creatures on a continent (mapId) whose name holds the text, where each first spawns; at most 50")
            .Produces<NpcInfo[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet(PathingApi.Spawns, (int uiMapId, string? layer, WorldCatalog world) => world.Spawns(uiMapId, layer).ToHttp())
            .WithTags(Tag)
            .WithSummary("Everything of one layer in a zone (elite, herb, vein, skinnable, mailbox), every spawn or node, in world yards")
            .Produces<SpawnPoint[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet(PathingApi.Quests, (int uiMapId, QuestCatalog quests) => quests.InZone(uiMapId).ToHttp())
            .WithTags(Tag)
            .WithSummary("The quests given in a zone, by level, each with where it is given, in world yards")
            .Produces<QuestSummary[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet(PathingApi.QuestSearch, (string? name, QuestCatalog quests) => quests.Search(name).ToHttp())
            .WithTags(Tag)
            .WithSummary("Quests whose name holds the text, at most 50, each with where it is given")
            .Produces<QuestSummary[]>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet($"{PathingApi.Quests}/{{id:int}}", (int id, QuestCatalog quests) => quests.Get(id).ToHttp())
            .WithTags(Tag)
            .WithSummary("One quest: where it is given and handed in, what it asks, and every place for each, in world yards")
            .WithDescription("An item to collect comes with every creature that drops it, object that holds it and vendor that sells it.")
            .Produces<QuestDetail>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
