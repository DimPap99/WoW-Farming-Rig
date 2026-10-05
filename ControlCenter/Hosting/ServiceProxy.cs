using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;

using System;

namespace ControlCenter.Hosting;

/// <summary>
/// What the browser asks the Pathing Service for directly, forwarded as it is (YARP), so the
/// map's script, the 3D view's hub connection and Swagger stay same-origin: world data, the API
/// and the watch hub, WebSockets included. A service that is down answers 502 here, which the
/// map reports as offline.
/// </summary>
public static class ServiceProxy
{
    /// <summary>Every path prefix this host forwards to the Pathing Service.</summary>
    public static readonly string[] PathingPrefixes =
        [PathingApi.Root, PathingApi.WatchHub, PathingApi.Swagger, .. WorldData.All];

    public static WebApplication MapServiceProxy(this WebApplication app)
    {
        Uri pathing = app.Services.GetRequiredService<PathingServiceOptions>().Url;
        string destination = pathing.GetLeftPart(UriPartial.Authority);

        foreach (string prefix in PathingPrefixes)
        {
            // A catch-all also matches nothing, so the prefix itself (/watchHub, /swagger) is forwarded too.
            app.MapForwarder($"{prefix}/{{**catch-all}}", destination);
        }

        return app;
    }
}
