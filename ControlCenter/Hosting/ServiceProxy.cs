using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;

using ServiceHosting;

using System;

namespace ControlCenter.Hosting;

/// <summary>
/// What the browser asks the Pathing Service for directly, forwarded as it is (YARP), so the world
/// view's script and Swagger stay same-origin: the API, the map art and a recorded path's file. A
/// service that is down answers 502 here, which the pages report as offline. The service's key goes on
/// every forwarded request, so the browser never holds it.
/// <para>
/// This host is a door to the service, which serves its machine and its key whoever stands behind it,
/// so the door applies the rules to its own caller first (<see cref="ForwardGuard"/>): this machine is
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

        // Another machine reads only: nothing else of the service is open to it here.
        app.UseForwardGuard(IsForwarded, pathing.ApiKey);

        foreach (string prefix in PathingPrefixes)
        {
            // A catch-all also matches nothing, so the prefix itself (/swagger) is forwarded too.
            app.MapForwarder($"{prefix}/{{**catch-all}}", destination, transforms => ForwardGuard.Forwarding(transforms, pathing.ApiKey));
        }

        return app;
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
}
