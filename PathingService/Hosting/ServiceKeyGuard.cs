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

namespace PathingService.Hosting;

/// <summary>
/// The service's key (<see cref="ServiceKey"/>): this machine is served, another machine only with
/// the key, and without a key the service stops right after binding beyond localhost. The rules are
/// <see cref="ServiceKeyPolicy"/>'s, shared with the Bot Manager and the clients.
/// </summary>
public static class ServiceKeyGuard
{
    public const string RefusedTitle = "Key required";

    public static WebApplication UseServiceKey(this WebApplication app)
    {
        string? key = app.Configuration[ServiceKey.ConfigKey]?.Trim();

        app.Use(async (context, next) =>
        {
            string? refusal = ServiceKeyPolicy.Refusal(key, context.Request.Headers[ServiceKey.Header],
                context.Connection.RemoteIpAddress);
            if (refusal is null)
            {
                await next(context);
                return;
            }

            await Results.Problem(detail: refusal, statusCode: StatusCodes.Status401Unauthorized, title: RefusedTitle)
                .ExecuteAsync(context);
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
