using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PathingService.Catalog;

public static class CatalogEndpoints
{
    public const string Tag = "Catalog";

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PathingApi.Locations, (LocationCatalog catalog) => catalog.All)
            .WithTags(Tag)
            .WithSummary("Named places in world yards: hand-picked tuning spots and every teleport target of the client's maps")
            .Produces<NamedLocation[]>();

        app.MapGet(PathingApi.TestRoutes, (TestRoutes routes) => routes.All)
            .WithTags(Tag)
            .WithSummary("Known-trouble routes, as route requests ready to send to " + PathingApi.FindRoute)
            .Produces<TestRoute[]>();

        return app;
    }
}
