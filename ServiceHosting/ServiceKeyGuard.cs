using Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Security;

using System;
using System.Collections.Generic;
using System.Net;

namespace ServiceHosting;

/// <summary>
/// The service's key (<see cref="ServiceKey"/>): this machine is served when its request names it,
/// another machine only with the key, and without a key the service stops right after binding beyond
/// localhost. A write, or a WebSocket, that a page on another site made a browser send is refused too
/// (<see cref="ServiceKeyPolicy.CrossSiteRefusal"/>). The rules are <see cref="ServiceKeyPolicy"/>'s,
/// shared with the forwarders and the clients; this is their one ASP.NET guard, for every service.
/// </summary>
public static class ServiceKeyGuard
{
    public const string RefusedTitle = "Key required";

    public const string CrossSiteTitle = "Refused: sent from another site";

    public const string ForeignHostTitle = "Refused: not this machine's name";

    /// <summary>ASP.NET Core's switch for forwarded headers, under which a remote caller can claim to be 127.0.0.1.</summary>
    public const string ForwardedHeadersVariable = "ASPNETCORE_FORWARDEDHEADERS_ENABLED";

    public static WebApplication UseServiceKey(this WebApplication app)
    {
        string? key = app.Configuration[ServiceKey.ConfigKey]?.Trim();
        bool trustLoopback = app.Configuration.GetValue(ServiceKey.TrustLoopbackKey, true);

        app.Use(async (context, next) =>
        {
            HttpRequest request = context.Request;
            if (ServiceKeyPolicy.Refusal(key, request.Headers[ServiceKey.Header], context.Connection.RemoteIpAddress, request.Host.Value,
                    trustLoopback) is string refusal)
            {
                await Results.Problem(detail: refusal, statusCode: StatusCodes.Status401Unauthorized, title: RefusedTitle)
                    .ExecuteAsync(context);
                return;
            }

            if (ServiceKeyPolicy.CrossSiteRefusal(request.Method, request.Headers["Sec-Fetch-Site"], request.Headers.Origin,
                    request.Host.Value, IsWebSocket(request)) is string crossSite)
            {
                await Results.Problem(detail: crossSite, statusCode: StatusCodes.Status403Forbidden, title: CrossSiteTitle)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            ILogger logger = app.Logger;
            if (trustLoopback && string.Equals(Environment.GetEnvironmentVariable(ForwardedHeadersVariable), "true", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("{Variable} is on: a remote caller can claim this machine's address. Set {Key}=false", ForwardedHeadersVariable,
                    ServiceKey.TrustLoopbackKey);
            }

            ICollection<string>? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || ServiceKeyPolicy.BindingProblem(addresses, key) is not string problem)
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Critical))
            {
                logger.LogCritical("Not serving: {Problem}", problem);
            }
            Environment.ExitCode = 1;
            app.Lifetime.StopApplication();
        });

        return app;
    }

    /// <summary>
    /// For a UI that serves its pages without a key (the Control Center, BlazorServer): a request over
    /// loopback must name this machine. A page whose name a DNS rebinding pointed at 127.0.0.1 is
    /// another site's, and would otherwise drive the pages as the person at this machine (known issue 75).
    /// </summary>
    public static WebApplication UseHostCheck(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            IPAddress? remote = context.Connection.RemoteIpAddress;
            bool local = remote is null || IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);
            if (local && !ServiceKeyPolicy.NamesThisMachine(context.Request.Host.Value))
            {
                await Results.Problem(detail: $"a request to this machine must name it (localhost, its address or its name), not {context.Request.Host}",
                    statusCode: StatusCodes.Status400BadRequest, title: ForeignHostTitle).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        return app;
    }

    // A WebSocket's opening: HTTP/1.1's Upgrade, or HTTP/2's extended CONNECT.
    private static bool IsWebSocket(HttpRequest request)
    {
        return string.Equals(request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase) || HttpMethods.IsConnect(request.Method);
    }
}
