using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using System.Threading;

namespace PathingService.Routes;

public static class RouteEndpoints
{
    public const string Tag = "Routes";

    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(PathingApi.FindRoute, (RouteRequest request, RouteHandler handler, CancellationToken token) =>
            {
                RouteResponse response = handler.Find(request, token);
                return Results.Json(response, statusCode: StatusCodeFor(response.Status));
            })
            .WithTags(Tag)
            .WithSummary("Route between two points of one zone's continent")
            .WithDescription("Found, NoPath and NotBaked are answers (200); BadRequest is 400 and Unavailable 503. " +
                "Every answer carries a RouteResponse, so the status is always in the body.")
            .Produces<RouteResponse>(StatusCodes.Status200OK)
            .Produces<RouteResponse>(StatusCodes.Status400BadRequest)
            .Produces<RouteResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>A route that does not exist is still an answer; only a bad request or an unavailable service is an HTTP error.</summary>
    public static int StatusCodeFor(RouteStatus status)
    {
        return status switch
        {
            RouteStatus.BadRequest => StatusCodes.Status400BadRequest,
            RouteStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status200OK,
        };
    }
}
