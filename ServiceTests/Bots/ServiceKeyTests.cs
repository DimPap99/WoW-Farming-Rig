using Contracts;
using Contracts.Bots;
using Contracts.Pathing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Security;

using ServiceTests.Api;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// The key (<see cref="ServiceKey"/>): this machine is always served; another machine only with the
/// key, and nobody else without one; and a service will not stay up bound beyond localhost
/// without a key. Both services apply the same rules.
/// </summary>
public sealed class ServiceKeyTests
{
    private const string Key = "a-shared-secret";

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(null, null, "127.0.0.1", true)]
    [InlineData(null, null, "::1", true)]
    [InlineData(null, null, "::ffff:127.0.0.1", true)]
    [InlineData(null, null, "192.0.2.10", false)]
    [InlineData(Key, null, "192.0.2.10", false)]
    [InlineData(Key, "wrong", "192.0.2.10", false)]
    [InlineData(Key, Key, "192.0.2.10", true)]
    [InlineData(Key, null, "127.0.0.1", true)]
    public void This_machine_is_served_and_another_only_with_the_key(string? configured, string? presented, string? remote, bool served)
    {
        IPAddress? address = remote is null ? null : IPAddress.Parse(remote);

        Assert.Equal(served, ServiceKeyPolicy.Refusal(configured, presented, address) is null);
    }

    [Theory]
    [InlineData("http://localhost:5030", true)]
    [InlineData("http://127.0.0.1:5030", true)]
    [InlineData("http://[::1]:5030", true)]
    [InlineData("http://0.0.0.0:5030", false)]
    [InlineData("http://[::]:5030", false)]
    [InlineData("http://*:5030", false)]
    [InlineData("http://+:5030", false)]
    [InlineData("http://botbox:5030", false)]
    [InlineData("http://192.168.1.20:5030", false)]
    public void An_address_other_machines_reach_needs_a_key(string address, bool local)
    {
        Assert.Equal(local, ServiceKeyPolicy.IsLocalAddress(address));
        Assert.Equal(local, ServiceKeyPolicy.BindingProblem([address], null) is null);
        Assert.Null(ServiceKeyPolicy.BindingProblem([address], Key));
    }

    [Fact]
    public async Task The_manager_serves_another_machine_only_with_its_key()
    {
        using TempDataRoot data = new();
        using BotManagerFactory keyed = new(data.Root, apiKey: Key);
        using BotManagerFactory open = new(data.Root);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(keyed, BotManagerApi.Health, remote: null, key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(keyed, BotManagerApi.Health, RemoteAddress.OtherMachine, key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(keyed, BotManagerApi.Health, RemoteAddress.OtherMachine, key: "wrong"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(keyed, BotManagerApi.Health, RemoteAddress.OtherMachine, Key));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(open, BotManagerApi.Health, RemoteAddress.OtherMachine, key: null));
    }

    [Fact]
    public async Task An_agent_on_another_machine_connects_only_with_the_key()
    {
        using TempDataRoot data = new();
        using BotManagerFactory keyed = new(data.Root, apiKey: Key);

        await using FakeAgent withKey = await FakeAgent.ConnectAsync(keyed, "agent1", Key, RemoteAddress.OtherMachine);
        AgentWelcome welcome = await withKey.RegisterAsync();
        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(
            () => FakeAgent.ConnectAsync(keyed, "agent2", key: null, RemoteAddress.OtherMachine));

        Assert.True(welcome.Accepted);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HubConnectionState.Connected, withKey.Connection.State);
    }

    [Fact]
    public async Task The_pathing_service_serves_another_machine_only_with_its_key()
    {
        using TempDataRoot data = new(withMapData: true);
        using KeyedPathingService keyed = new(data.Root, Key);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(keyed, PathingApi.Capabilities, remote: null, key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(keyed, PathingApi.Capabilities, RemoteAddress.OtherMachine, key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(keyed, "/dbc/WorldMapArea.json", RemoteAddress.OtherMachine, key: null));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(keyed, PathingApi.Capabilities, RemoteAddress.OtherMachine, Key));
    }

    private static async Task<HttpStatusCode> StatusAsync<TEntry>(WebApplicationFactory<TEntry> factory, string path, string? remote, string? key)
        where TEntry : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (remote is not null)
        {
            request.Headers.Add(RemoteAddress.Header, remote);
        }
        if (key is not null)
        {
            request.Headers.Add(ServiceKey.Header, key);
        }

        using HttpResponseMessage response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private sealed class KeyedPathingService(string dataRoot, string key) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(PathingService.Hosting.PathingLogging.FileKey, string.Empty);
            builder.UseSetting(ServiceKey.ConfigKey, key);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, RemoteAddress>());
            builder.ConfigureTestServices(services =>
                services.AddSingleton(new DataConfig { Root = dataRoot, Exp = TestData.Client }));
        }
    }
}
