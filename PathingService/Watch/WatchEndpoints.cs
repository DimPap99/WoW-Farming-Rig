using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PathingService.Watch;

public static class WatchEndpoints
{
    public const string Tag = "Watch";

    public static IEndpointRouteBuilder MapWatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHub<WatchHub>(PathingApi.WatchHub);

        app.MapPost(PathingApi.WatchClear, (WatchBroadcaster watch) =>
            {
                watch.Clear();
                return Results.Accepted();
            })
            .WithTags(Tag)
            .WithSummary("Forget the remembered routes, and tell every route watcher to")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}
