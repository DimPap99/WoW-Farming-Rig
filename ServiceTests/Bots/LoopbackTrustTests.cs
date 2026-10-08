using Contracts;
using Contracts.Bots;

using ServiceClients.Security;

using ServiceTests.Api;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// How far this machine is trusted (known issue 75): it was served whatever the request named, so a page
/// whose name a DNS rebinding pointed at 127.0.0.1 was "this machine"; a WebSocket's opening, a GET, was
/// let through the cross-site rule, so any page on the machine could open the hubs; and a proxy in front
/// of a service made every remote caller 127.0.0.1.
/// </summary>
public sealed class LoopbackTrustTests
{
    private const string Key = "a-shared-secret";

    [Theory]
    [InlineData(null, true)]
    [InlineData("localhost:5030", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("app.localhost:5030", true)]
    [InlineData("127.0.0.1:5030", true)]
    [InlineData("[::1]:5030", true)]
    [InlineData("attacker.example:5030", false)]
    [InlineData("192.0.2.10:5030", false)]
    public void A_request_names_this_machine_by_localhost_a_loopback_address_or_its_own_name(string? host, bool local)
    {
        Assert.Equal(local, ServiceKeyPolicy.NamesThisMachine(host));
        Assert.True(ServiceKeyPolicy.NamesThisMachine(Environment.MachineName + ":5030"));
    }

    [Fact]
    public void A_page_rebound_to_this_machine_needs_the_key()
    {
        Assert.NotNull(ServiceKeyPolicy.Refusal(null, null, IPAddress.Loopback, "attacker.example:5030"));
        Assert.NotNull(ServiceKeyPolicy.Refusal(Key, null, IPAddress.Loopback, "attacker.example:5030"));
        Assert.Null(ServiceKeyPolicy.Refusal(Key, Key, IPAddress.Loopback, "attacker.example:5030"));
        Assert.Null(ServiceKeyPolicy.Refusal(null, null, IPAddress.Loopback, "localhost:5030"));
    }

    [Fact]
    public void Without_loopback_trust_this_machine_needs_the_key_too()
    {
        Assert.NotNull(ServiceKeyPolicy.Refusal(Key, null, IPAddress.Loopback, "localhost:5030", trustLoopback: false));
        Assert.NotNull(ServiceKeyPolicy.Refusal(Key, null, null, null, trustLoopback: false));
        Assert.Null(ServiceKeyPolicy.Refusal(Key, Key, IPAddress.Loopback, "localhost:5030", trustLoopback: false));
    }

    [Theory]
    [InlineData("cross-site", null, true)]
    [InlineData("same-site", null, true)]
    [InlineData(null, "http://evil.example", true)]
    [InlineData("same-origin", null, false)]
    [InlineData(null, "http://localhost:5030", false)]
    [InlineData(null, null, false)]
    public void A_WebSocket_opened_from_another_site_is_refused_as_a_write_is(string? fetchSite, string? origin, bool refused)
    {
        Assert.Equal(refused, ServiceKeyPolicy.CrossSiteRefusal("GET", fetchSite, origin, "localhost:5030", webSocket: true) is not null);
        Assert.Null(ServiceKeyPolicy.CrossSiteRefusal("GET", fetchSite, origin, "localhost:5030"));
    }

    [Fact]
    public async Task The_manager_refuses_a_rebound_page_and_a_hub_opened_from_another_site()
    {
        using TempDataRoot data = new();
        using BotManagerFactory manager = new(data.Root);
        HttpClient client = manager.CreateClient();

        HttpStatusCode rebound = await SendAsync(client, BotManagerApi.Bots, ("Host", "attacker.example:5030"));
        HttpStatusCode named = await SendAsync(client, BotManagerApi.Bots, ("Host", "localhost:5030"));
        HttpStatusCode socket = await SendAsync(client, BotManagerApi.FleetHub, ("Upgrade", "websocket"), ("Connection", "Upgrade"),
            ("Origin", "http://evil.example"));

        Assert.Equal(HttpStatusCode.Unauthorized, rebound);
        Assert.Equal(HttpStatusCode.OK, named);
        Assert.Equal(HttpStatusCode.Forbidden, socket);
    }

    [Fact]
    public async Task A_manager_told_not_to_trust_loopback_serves_this_machine_only_with_the_key()
    {
        using TempDataRoot data = new();
        using BotManagerFactory manager = new(data.Root, apiKey: Key, trustLoopback: false);
        HttpClient client = manager.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, BotManagerApi.Health));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, BotManagerApi.Health, (ServiceKey.Header, Key)));
    }

    [Fact]
    public async Task The_Control_Center_refuses_a_rebound_page()
    {
        using ControlCenterFactory controlCenter = new();
        HttpClient client = controlCenter.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, "/", ("Host", "attacker.example:5020")));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, "/", ("Host", "localhost:5020")));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string path, params (string Name, string Value)[] headers)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        foreach ((string name, string value) in headers)
        {
            if (name == "Host")
            {
                request.Headers.Host = value;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
