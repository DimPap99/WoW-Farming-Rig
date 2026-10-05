using System;
using System.Net;
using System.Net.Sockets;

namespace ServiceTests.Api;

/// <summary>
/// A service that is down: an address on this machine where nothing listens, so a connection is
/// refused. A fixed port is not safe for this. Windows' Simple TCP/IP Services listens on the
/// discard port (9) and never answers, which turns "refused" into a hang until the client's timeout.
/// </summary>
internal static class ClosedPort
{
    /// <summary>A loopback URL on a port the system has just handed out and nothing kept.</summary>
    public static Uri NewUrl()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return new Uri($"http://127.0.0.1:{port}/");
    }
}
