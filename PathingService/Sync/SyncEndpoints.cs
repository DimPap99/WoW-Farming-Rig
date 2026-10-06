using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

namespace PathingService.Sync;

public static class SyncEndpoints
{
    public const string Tag = "Sync";

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder sync = app.MapGroup(PathingApi.Sync).WithTags(Tag);

        sync.MapGet("{set}", (string set, string? era, SyncLibrary library) => library.Manifest(set, era).ToHttp())
            .WithSummary("Every file of a sync set with its size and hash, for an agent to compare its copies with")
            .Produces<SyncManifest>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        sync.MapGet($"{{set}}/{PathingApi.SyncFiles}/{{**name}}", (string set, string name, string? era, SyncLibrary library) =>
            {
                StoreResult<string> file = library.Find(set, name, era);
                return file.Outcome == StoreOutcome.Done
                    ? (IResult)TypedResults.PhysicalFile(file.Value!, SyncLibrary.ContentType)
                    : file.ToHttp();
            })
            .WithSummary("One file of a sync set, its bytes exactly as stored")
            .Produces(StatusCodes.Status200OK, contentType: SyncLibrary.ContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
