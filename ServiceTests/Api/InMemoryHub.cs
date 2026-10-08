using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

using ServiceClients;

using System;
using System.Net.Http;

namespace ServiceTests.Api;

/// <summary>
/// A hub connection as the rig's clients build it (<see cref="ServiceHubs.Build"/>), over a test's
/// in-memory server: its handler, and long polling, as it has no WebSockets.
/// </summary>
internal static class InMemoryHub
{
    /// <summary>Through <paramref name="handler"/>, from <paramref name="remote"/> when given (<see cref="RemoteAddress"/>).</summary>
    public static Action<HttpConnectionOptions> Over(Func<HttpMessageHandler> handler, string? remote = null)
    {
        return options =>
        {
            options.HttpMessageHandlerFactory = _ => handler();
            options.Transports = HttpTransportType.LongPolling;
            if (remote is not null)
            {
                options.Headers[RemoteAddress.Header] = remote;
            }
        };
    }

    /// <summary>A connection to the hub at <paramref name="path"/> on <paramref name="server"/>, not started.</summary>
    public static HubConnection Connect(TestServer server, string path, string? key = null, string? remote = null)
    {
        return ServiceHubs.Build(server.BaseAddress, path, key, Over(server.CreateHandler, remote));
    }
}
