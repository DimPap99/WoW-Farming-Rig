using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

using System;
using System.Net;

namespace ServiceTests.Api;

/// <summary>
/// Makes a request to an in-memory server look as if it came from another machine: the test server
/// has no remote address, which the services read as this machine. A request carrying
/// <see cref="Header"/> gets that address instead.
/// </summary>
internal sealed class RemoteAddress : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    /// <summary>An address of the documentation range: another machine, never this one.</summary>
    public const string OtherMachine = "192.0.2.10";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var address) && IPAddress.TryParse(address, out IPAddress? remote))
                {
                    context.Connection.RemoteIpAddress = remote;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
