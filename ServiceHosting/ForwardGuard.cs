using Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ServiceClients.Security;

using System;

using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ServiceHosting;

/// <summary>
/// A host that forwards a browser's calls to a service and adds the service's key on the way (the
/// Control Center's proxy, BlazorServer's <c>/api/v1</c>) is a door to that service, which serves its
/// machine and its key whoever stands behind the door. So the door applies the rules to its own caller
/// first (<see cref="ForwardPolicy"/>): this machine gets anything, another machine reads and what
/// <c>openToOthers</c> lets it, a write sent from another site nothing, and a request it forwarded
/// itself comes back no further. One guard for both doors, which each had a copy.
/// </summary>
public static class ForwardGuard
{
    public const string RefusedTitle = "Not forwarded";

    /// <summary>
    /// Refuses, before it is forwarded, a request <paramref name="forwarded"/> says goes to the service and the
    /// rules refuse. <paramref name="openToOthers"/> is what another machine may have besides reads.
    /// </summary>
    public static WebApplication UseForwardGuard(this WebApplication app, Func<PathString, bool> forwarded, string? key,
        Func<string, string, bool>? openToOthers = null)
    {
        app.Use(async (context, next) =>
        {
            if (forwarded(context.Request.Path) && Refusal(context, key, openToOthers) is (int status, string refusal))
            {
                await Results.Problem(detail: refusal, statusCode: status, title: RefusedTitle).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        return app;
    }

    /// <summary>Why the request is not forwarded, and the status to answer, or null to forward it.</summary>
    public static (int Status, string Refusal)? Refusal(HttpContext context, string? key, Func<string, string, bool>? openToOthers = null)
    {
        HttpRequest request = context.Request;
        bool hop = request.Headers.ContainsKey(ForwardPolicy.HopHeader);
        if (ServiceKeyPolicy.CrossSiteRefusal(request.Method, request.Headers["Sec-Fetch-Site"], request.Headers.Origin,
                request.Host.Value) is string crossSite)
        {
            return (StatusCodes.Status403Forbidden, crossSite);
        }

        return ForwardPolicy.Refusal(request.Method, request.Path.Value ?? string.Empty, context.Connection.RemoteIpAddress, key,
                request.Headers[ServiceKey.Header], openToOthers ?? Nothing, hop, request.Host.Value) is string refusal
            ? (hop ? StatusCodes.Status508LoopDetected : StatusCodes.Status403Forbidden, refusal)
            : null;
    }

    /// <summary>
    /// A forwarded request as it leaves: with the service's <paramref name="key"/>, replacing whatever the
    /// browser sent, and the hop mark; without the browser's word on where it comes from, which the door
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

    private static bool Nothing(string method, string path)
    {
        return false;
    }
}
