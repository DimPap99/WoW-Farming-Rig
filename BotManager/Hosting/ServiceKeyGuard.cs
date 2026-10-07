using Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Security;

using System;
using System.Collections.Generic;

namespace BotManager.Hosting;

/// <summary>
/// The service's key (<see cref="ServiceKey"/>): this machine is served, another machine only with
/// the key, and without a key the service stops right after binding beyond localhost. A write that a
/// page on another site made a browser send is refused too (<see cref="ServiceKeyPolicy.CrossSiteRefusal"/>).
/// The rules are <see cref="ServiceKeyPolicy"/>'s, shared with the Pathing Service, the forwarders and the clients.
/// </summary>
public static class ServiceKeyGuard
{
    public const string RefusedTitle = "Key required";

    public const string CrossSiteTitle = "Refused: sent from another site";

    public static WebApplication UseServiceKey(this WebApplication app)
    {
        string? key = app.Configuration[ServiceKey.ConfigKey]?.Trim();

        app.Use(async (context, next) =>
        {
            HttpRequest request = context.Request;
            if (ServiceKeyPolicy.Refusal(key, request.Headers[ServiceKey.Header], context.Connection.RemoteIpAddress) is string refusal)
            {
                await Results.Problem(detail: refusal, statusCode: StatusCodes.Status401Unauthorized, title: RefusedTitle)
                    .ExecuteAsync(context);
                return;
            }

            if (ServiceKeyPolicy.CrossSiteRefusal(request.Method, request.Headers["Sec-Fetch-Site"], request.Headers.Origin,
                    request.Host.Value) is string crossSite)
            {
                await Results.Problem(detail: crossSite, statusCode: StatusCodes.Status403Forbidden, title: CrossSiteTitle)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            ICollection<string>? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || ServiceKeyPolicy.BindingProblem(addresses, key) is not string problem)
            {
                return;
            }

            ILogger logger = app.Logger;
            if (logger.IsEnabled(LogLevel.Critical))
            {
                logger.LogCritical("Not serving: {Problem}", problem);
            }
            Environment.ExitCode = 1;
            app.Lifetime.StopApplication();
        });

        return app;
    }
}
