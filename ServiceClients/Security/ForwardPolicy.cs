using Contracts;
using Contracts.Pathing;

using System;
using System.Net;

namespace ServiceClients.Security;

/// <summary>
/// What a forwarder lets through: a host that relays a browser's calls to a service and adds the
/// service's key on the way (BlazorServer's <c>/api/v1</c>, the Control Center's proxy). The service
/// serves the forwarder's machine and the key, whoever stands behind the forwarder, so the forwarder is
/// the door and applies the rules to its own caller before it adds the key: the key rule
/// (<see cref="ServiceKeyPolicy"/>), no writes from another site (<see cref="ServiceKeyPolicy.CrossSiteRefusal"/>),
/// and no request it forwarded itself. Pure rules, with no ASP.NET types, like the key's.
/// <para>
/// This machine, and a caller with the key, are forwarded anything. Another machine gets reads and
/// what the host's own pages author (<c>openToOthers</c>), and nothing that changes what every bot
/// uses - tiles, bakes, area grids - which the service itself would refuse it without the key.
/// </para>
/// </summary>
public static class ForwardPolicy
{
    /// <summary>
    /// Set on every request a forwarder forwards. A forwarder that receives one refuses it: its
    /// destination is itself, or another forwarder, and the chain would run until the ports ran out.
    /// </summary>
    public const string HopHeader = "X-Rig-Forwarded";

    /// <summary>
    /// What a browser sends that says where a request comes from: a forwarder checks it and takes it
    /// off, so the service judges the forwarder's request, not the page's.
    /// </summary>
    public static readonly string[] BrowserOriginHeaders = ["Origin", "Referer", "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest", "Sec-Fetch-User"];

    /// <summary>
    /// Why a request a forwarder received is not forwarded, or null to forward it.
    /// </summary>
    /// <param name="method">The request's method.</param>
    /// <param name="path">Its path, for the refusal and for <paramref name="openToOthers"/>.</param>
    /// <param name="remote">Where it comes from; null over a local transport (this machine).</param>
    /// <param name="configuredKey">The service's key the forwarder adds; a caller presenting it is trusted as the service would trust it.</param>
    /// <param name="presented">The key the caller sent, if any.</param>
    /// <param name="openToOthers">What another machine may have without the key, besides reads.</param>
    /// <param name="forwarded">Whether it carries <see cref="HopHeader"/>.</param>
    public static string? Refusal(string method, string path, IPAddress? remote, string? configuredKey, string? presented,
        Func<string, string, bool> openToOthers, bool forwarded)
    {
        if (forwarded)
        {
            return "this request was forwarded here by a forwarder already: the service's address names a forwarder, " +
                   "maybe this host itself, not the service";
        }

        if (ServiceKeyPolicy.Refusal(configuredKey, presented, remote) is null || IsRead(method) || openToOthers(method, path))
        {
            return null;
        }

        return $"{method} {path} changes what every bot uses: it is served to the machine this runs on, " +
               $"or with the service's key in the {ServiceKey.Header} header";
    }

    /// <summary>
    /// What the bot's Leaflet map writes, open to another machine as BlazorServer's dashboard is: a
    /// recorded path saved (<c>PUT {PathingApi.Paths}/{name}</c>), and a zone map's roads or danger
    /// zones saved (<c>PUT {PathingApi.CostZones}/{continent}/roads|danger/{uiMapId}</c>).
    /// </summary>
    public static bool LeafletMapAuthors(string method, string path)
    {
        if (!method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.StartsWith(PathingApi.Paths + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] zones = PathingApi.CostZones.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == zones.Length + 3 &&
               segments.AsSpan(0, zones.Length).SequenceEqual(zones) &&
               segments[zones.Length + 1] is PathingApi.Roads or PathingApi.Danger;
    }

    /// <summary>A read: GET or HEAD, which change nothing.</summary>
    public static bool IsRead(string method)
    {
        return method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
    }
}
