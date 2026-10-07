using Contracts;

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ServiceClients.Security;

/// <summary>
/// Who a service serves (<see cref="ServiceKey"/>): this machine always; another machine only with
/// the configured key, and nobody else without one. A service also refuses to start bound beyond
/// this machine without a key. Pure rules, with no ASP.NET types, so every service applies the same
/// ones in a few lines and clients and services share one definition.
/// </summary>
public static class ServiceKeyPolicy
{
    /// <summary>
    /// Why a request from <paramref name="remote"/> carrying <paramref name="presented"/> is refused,
    /// or null to serve it. A request with no remote address came over a local transport (a pipe, a
    /// socket file, an in-memory test server) and is this machine's.
    /// </summary>
    public static string? Refusal(string? configuredKey, string? presented, IPAddress? remote)
    {
        if (remote is null || IsLoopback(remote))
        {
            return null;
        }

        if (string.IsNullOrEmpty(configuredKey))
        {
            return $"this service serves only its own machine until it is given a key ({ServiceKey.ConfigKey})";
        }

        if (string.IsNullOrEmpty(presented))
        {
            return $"a request from another machine needs the service's key in the {ServiceKey.Header} header";
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configuredKey), Encoding.UTF8.GetBytes(presented))
            ? null
            : $"the {ServiceKey.Header} header does not carry this service's key";
    }

    /// <summary>
    /// Why a browser's write is refused as sent by a page on another site, or null. Any page a person
    /// opens can make their browser send a body-less POST, or a form, to a local address without asking
    /// first (no CORS preflight), and a service that serves this machine without a key would carry it out:
    /// cancel a bake, start an area grid, start or stop a bot. A browser says where a request comes from
    /// (<c>Sec-Fetch-Site</c>, else <c>Origin</c>); a client that is not a browser says neither and is not
    /// refused here. Reads are left alone: they change nothing, and such a page cannot read the answer.
    /// </summary>
    /// <param name="method">The request's method.</param>
    /// <param name="fetchSite">The <c>Sec-Fetch-Site</c> header: same-origin, same-site, cross-site or none.</param>
    /// <param name="origin">The <c>Origin</c> header, for a browser that sends no <c>Sec-Fetch-Site</c>.</param>
    /// <param name="host">The request's own host and port, as the <c>Host</c> header names them.</param>
    public static string? CrossSiteRefusal(string method, string? fetchSite, string? origin, string? host)
    {
        if (ForwardPolicy.IsRead(method) || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(fetchSite))
        {
            // "none": typed, bookmarked or opened by the person themselves.
            return fetchSite is "same-origin" or "none"
                ? null
                : $"a {method} that a page on another site made the browser send ({fetchSite}) is refused";
        }

        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) && from.Authority.Equals(host, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"a {method} that a page from {origin} made the browser send is refused";
    }

    /// <summary>
    /// Why a service listening on <paramref name="addresses"/> must not start, or null: an address
    /// another machine can reach, with no key configured.
    /// </summary>
    public static string? BindingProblem(IEnumerable<string> addresses, string? configuredKey)
    {
        if (!string.IsNullOrEmpty(configuredKey))
        {
            return null;
        }

        foreach (string address in addresses)
        {
            if (!IsLocalAddress(address))
            {
                return $"{address} can be reached from other machines, and no key is configured: " +
                       $"set {ServiceKey.ConfigKey} (the same on every service and client), or listen on localhost";
            }
        }

        return null;
    }

    /// <summary>Whether a listening address (<c>http://host:port</c>) is reachable from this machine only.</summary>
    public static bool IsLocalAddress(string address)
    {
        // Wildcards (*, +) do not parse as a URI host and listen everywhere.
        if (!Uri.TryCreate(address.Replace("://*", "://wildcard.invalid", StringComparison.Ordinal)
                .Replace("://+", "://wildcard.invalid", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip) && IsLoopback(ip);
    }

    private static bool IsLoopback(IPAddress address)
    {
        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}
