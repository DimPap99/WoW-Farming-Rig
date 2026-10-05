using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;

using System;

using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ControlCenter.Hosting;

/// <summary>
/// What the browser asks the Pathing Service for directly, forwarded as it is (YARP), so the
/// map's script, the 3D view's hub connection and Swagger stay same-origin: world data, the API
/// and the watch hub, WebSockets included. A service that is down answers 502 here, which the
/// map reports as offline. The service's key goes on every forwarded request, so the browser
/// never holds it.
/// </summary>
public static class ServiceProxy
{
    /// <summary>Every path prefix this host forwards to the Pathing Service.</summary>
    public static readonly string[] PathingPrefixes =
        [PathingApi.Root, PathingApi.WatchHub, PathingApi.Swagger, .. WorldData.All];

    public static WebApplication MapServiceProxy(this WebApplication app)
    {
        PathingServiceOptions pathing = app.Services.GetRequiredService<PathingServiceOptions>();
        string destination = pathing.Url.GetLeftPart(UriPartial.Authority);

        foreach (string prefix in PathingPrefixes)
        {
            // A catch-all also matches nothing, so the prefix itself (/watchHub, /swagger) is forwarded too.
            app.MapForwarder($"{prefix}/{{**catch-all}}", destination, transforms => AddKey(transforms, pathing.ApiKey));
        }

        return app;
    }

    /// <summary>Sends <paramref name="key"/> with a forwarded request, replacing whatever the browser sent.</summary>
    public static void AddKey(TransformBuilderContext transforms, string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            transforms.AddRequestHeader(ServiceKey.Header, key, append: false);
        }
    }
}
