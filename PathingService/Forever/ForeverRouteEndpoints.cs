using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using PathingService.Hosting;

namespace PathingService.Forever;

public static class ForeverRouteEndpoints
{
    public const string Tag = "Forever routes";

    public static IEndpointRouteBuilder MapForeverRouteEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder routes = app.MapGroup(PathingApi.ForeverRoutes).WithTags(Tag);

        routes.MapGet("", (ForeverRouteLibrary library) => library.List())
            .WithSummary("Every Forever route file, each checked as a run would load it")
            .Produces<ForeverRouteInfo[]>();

        app.MapPost(PathingApi.ForeverRouteCheck, (ForeverRouteDraft draft, ForeverRouteLibrary library) => library.Check(draft).ToHttp())
            .WithTags(Tag)
            .WithSummary("What a run would make of a route file's text, without saving it")
            .Produces<ForeverRouteInfo>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet("{name}", (string name, ForeverRouteLibrary library) => library.Get(name).ToHttp())
            .WithSummary("One route file's text, comments and all, and what a run makes of it")
            .Produces<ForeverRouteDocument>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        routes.MapPut("{name}", (string name, ForeverRouteText body, ForeverRouteLibrary library) => library.Save(name, body.Text).ToHttp())
            .WithSummary("Create or replace a route file; it is written as given once a run would load it")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete("{name}", (string name, ForeverRouteLibrary library) => library.Delete(name).ToHttp())
            .WithSummary("Delete a route file")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
