using Contracts;
using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// The Control Center's proxy as the door to the Pathing Service (known issue 47): it adds the key,
/// marks the hop and takes off the browser's word on where a request comes from, and it applies the
/// rules to its own caller first. The service here is a listener that answers with the headers it got.
/// </summary>
public sealed class ControlCenterForwardTests
{
    private const string Key = "the-key";

    [Fact]
    public async Task A_forwarded_request_carries_the_key_and_the_hop_and_not_the_browsers_origin()
    {
        await using HeaderEcho service = await HeaderEcho.StartAsync();
        using ControlCenterFactory factory = new(pathingUrl: service.Url, pathingKey: Key);
        using HttpRequestMessage request = new(HttpMethod.Get, PathingApi.World);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add(ServiceKey.Header, "what-the-browser-sent");

        using HttpResponseMessage response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        Dictionary<string, string>? seen = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Key, seen![ServiceKey.Header]);
        Assert.Equal("1", seen[ForwardPolicy.HopHeader]);
        Assert.DoesNotContain("Origin", seen.Keys);
        Assert.DoesNotContain("Sec-Fetch-Site", seen.Keys);
    }

    [Fact]
    public async Task Another_machine_reads_through_it_and_writes_nothing()
    {
        await using HeaderEcho service = await HeaderEcho.StartAsync();
        using ControlCenterFactory factory = new(pathingUrl: service.Url, pathingKey: Key);
        HttpClient http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(http, HttpMethod.Get, PathingApi.World, RemoteAddress.OtherMachine));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(http, HttpMethod.Post, PathingApi.Bake, RemoteAddress.OtherMachine));
        Assert.Equal(HttpStatusCode.Forbidden,
            await SendAsync(http, HttpMethod.Put, $"{PathingApi.CostZones}/Azeroth/{PathingApi.Roads}/1429", RemoteAddress.OtherMachine));
        // With the service's key, another machine is trusted as the service would trust it.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(http, HttpMethod.Post, PathingApi.Bake, RemoteAddress.OtherMachine,
            (ServiceKey.Header, Key)));
        // This machine gets anything, but no page on another site writes through it.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(http, HttpMethod.Post, PathingApi.Bake, remote: null));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(http, HttpMethod.Post, PathingApi.BakeCancel, remote: null,
            ("Sec-Fetch-Site", "cross-site")));
        // A request it forwarded itself comes back no further: the service's address names a forwarder.
        Assert.Equal(HttpStatusCode.LoopDetected, await SendAsync(http, HttpMethod.Get, PathingApi.World, remote: null,
            (ForwardPolicy.HopHeader, "1")));
        // Only what its pages fetch is forwarded.
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(http, HttpMethod.Get, PathingApi.WatchHub, remote: null));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(http, HttpMethod.Get, WorldData.Dbc + "/WorldMapArea.json", remote: null));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient http, HttpMethod method, string path, string? remote,
        params (string Name, string Value)[] headers)
    {
        using HttpRequestMessage request = new(method, path);
        if (remote is not null)
        {
            request.Headers.Add(RemoteAddress.Header, remote);
        }

        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>A service on a real port, as YARP forwards over sockets, that answers with the headers it got.</summary>
    private sealed class HeaderEcho(WebApplication app, Uri url) : IAsyncDisposable
    {
        public Uri Url { get; } = url;

        public static async Task<HeaderEcho> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            WebApplication app = builder.Build();
            app.Run(context => context.Response.WriteAsJsonAsync(
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await app.StartAsync(TestContext.Current.CancellationToken);

            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new HeaderEcho(app, new Uri(address));
        }

        public ValueTask DisposeAsync()
        {
            return app.DisposeAsync();
        }
    }
}
