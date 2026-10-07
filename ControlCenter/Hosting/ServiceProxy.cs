using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;
using ServiceClients.Security;

using System;

using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ControlCenter.Hosting;

/// <summary>
/// What the browser asks the Pathing Service for directly, forwarded as it is (YARP), so the world
/// view's script and Swagger stay same-origin: the API, the map art and a recorded path's file. A
/// service that is down answers 502 here, which the pages report as offline. The service's key goes on
/// every forwarded request, so the browser never holds it.
/// <para>
/// This host is a door to the service, which serves its machine and its key whoever stands behind it,
/// so the door applies the rules to its own caller first (<see cref="ForwardPolicy"/>): this machine is
/// forwarded anything, another machine reads only, a write sent from another site nothing.
/// </para>
/// </summary>
public static class ServiceProxy
{
    /// <summary>Every path prefix this host forwards to the Pathing Service.</summary>
    public static readonly string[] PathingPrefixes = [PathingApi.Root, PathingApi.Swagger, WorldData.Tiles, WorldData.RecordedPaths];

    public static WebApplication MapServiceProxy(this WebApplication app)
    {
        PathingServiceOptions pathing = app.Services.GetRequiredService<PathingServiceOptions>();
        string destination = pathing.Url.GetLeftPart(UriPartial.Authority);

        app.Use(async (context, next) =>
        {
            if (IsForwarded(context.Request.Path) && Refusal(context, pathing.ApiKey) is (int status, string refusal))
            {
                await Results.Problem(detail: refusal, statusCode: status, title: "Not forwarded").ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        foreach (string prefix in PathingPrefixes)
        {
            // A catch-all also matches nothing, so the prefix itself (/swagger) is forwarded too.
            app.MapForwarder($"{prefix}/{{**catch-all}}", destination, transforms => Forwarding(transforms, pathing.ApiKey));
        }

        return app;
    }

    /// <summary>
    /// Why a request for the service is not forwarded, and the status to answer, or null to forward it
    /// (<see cref="ForwardPolicy"/>): the caller of this host is judged, as the service would judge it.
    /// <paramref name="openToOthers"/> is what another machine may have besides reads; none here.
    /// </summary>
    public static (int Status, string Refusal)? Refusal(HttpContext context, string? key, Func<string, string, bool>? openToOthers = null)
    {
        HttpRequest request = context.Request;
        bool forwarded = request.Headers.ContainsKey(ForwardPolicy.HopHeader);
        if (ServiceKeyPolicy.CrossSiteRefusal(request.Method, request.Headers["Sec-Fetch-Site"], request.Headers.Origin,
                request.Host.Value) is string crossSite)
        {
            return (StatusCodes.Status403Forbidden, crossSite);
        }

        return ForwardPolicy.Refusal(request.Method, request.Path.Value ?? string.Empty, context.Connection.RemoteIpAddress, key,
                request.Headers[ServiceKey.Header], openToOthers ?? Nothing, forwarded) is string refusal
            ? (forwarded ? StatusCodes.Status508LoopDetected : StatusCodes.Status403Forbidden, refusal)
            : null;
    }

    /// <summary>
    /// A forwarded request as it leaves: with the service's <paramref name="key"/>, replacing whatever the
    /// browser sent, and the hop mark; without the browser's word on where it comes from, which this host
    /// has checked (the service would read a forwarded page as another site's).
    /// </summary>
    public static void Forwarding(TransformBuilderContext transforms, string? key)
    {
        transforms.AddRequestHeader(ForwardPolicy.HopHeader, "1", append: false);
        foreach (string header in ForwardPolicy.BrowserOriginHeaders)
        {
            transforms.AddRequestHeaderRemove(header);
        }

        if (!string.IsNullOrEmpty(key))
        {
            transforms.AddRequestHeader(ServiceKey.Header, key, append: false);
        }
    }

    private static bool IsForwarded(PathString path)
    {
        foreach (string prefix in PathingPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Nothing(string method, string path)
    {
        return false;
    }
}
