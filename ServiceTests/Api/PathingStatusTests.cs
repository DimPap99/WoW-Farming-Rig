using ControlCenter.Components.Layout;
using ControlCenter.Pathing;

using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients.Pathing;

using ServiceTests.Clients;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// The Control Center's poller of the Pathing Service: only that service's own health counts (known
/// issue 50), and pages read again when what it serves changed while it stayed up (known issue 51).
/// </summary>
public sealed class PathingStatusTests
{
    private static readonly Uri ServiceUrl = new("http://localhost:5010");

    private static readonly PathingCapabilities Serves =
        new(PathingApi.Version, "som", "precata", true, ["Azeroth"], GameFiles: false);

    private static PathingHealth Healthy(string instance = "run-1", int kalimdorTiles = 0, int azerothTiles = 10)
    {
        return new PathingHealth("som", "precata", [new("Azeroth", azerothTiles), new("Kalimdor", kalimdorTiles)], [],
            BakeStatus.Idle, instance);
    }

    [Fact]
    public async Task The_bot_managers_health_is_the_pathing_service_offline_with_why()
    {
        // The Bot Manager answers the same route, and this machine needs no key there.
        PathingStatus status = Status(_ => """{"apiVersion":1,"agentProtocol":1,"bots":0,"online":0}""");

        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Online);
        Assert.Contains("not the Pathing Service", status.Error);
        Assert.Contains(PathingServiceOptions.UrlKey, status.Error);
        Assert.Null(status.Health);
    }

    [Fact]
    public async Task A_health_answer_from_an_older_build_is_offline_with_why()
    {
        // As a Phase 1 service answered: no bake, no instance.
        PathingStatus status = Status(_ => """{"client":"som","era":"precata","coverage":[{"continent":"Azeroth","tiles":10}],"lanes":[]}""");

        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Online);
        Assert.Contains(nameof(PathingHealth.Bake), status.Error);
        Assert.Null(status.Health);
    }

    [Fact]
    public async Task Another_version_of_the_api_is_offline_with_why()
    {
        PathingStatus status = Status(_ => Json(Healthy()), Serves with { ApiVersion = PathingApi.Version + 1 });

        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Online);
        Assert.Contains("version", status.Error);
    }

    [Fact]
    public async Task Pages_read_again_when_the_service_restarts_or_its_baked_continents_change()
    {
        PathingHealth health = Healthy();
        PathingStatus status = Status(_ => Json(health));
        int reconnected = 0;
        status.Reconnected += () => reconnected++;

        await status.RefreshAsync(TestContext.Current.CancellationToken);
        await status.RefreshAsync(TestContext.Current.CancellationToken);
        int first = reconnected;

        // A restart quicker than a poll: only the instance says so.
        health = Healthy(instance: "run-2");
        await status.RefreshAsync(TestContext.Current.CancellationToken);
        int restarted = reconnected;

        // A bake on Kalimdor: a continent with tiles now.
        health = Healthy(instance: "run-2", kalimdorTiles: 5);
        await status.RefreshAsync(TestContext.Current.CancellationToken);
        int baked = reconnected;

        // More tiles where there were tiles: nothing a page picks from changed.
        health = Healthy(instance: "run-2", kalimdorTiles: 5, azerothTiles: 11);
        await status.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Online);
        Assert.Equal(1, first);
        Assert.Equal(2, restarted);
        Assert.Equal(3, baked);
        Assert.Equal(3, reconnected);
        Assert.Equal(11, status.Health!.Coverage[0].Tiles);
    }

    [Fact]
    public async Task The_status_bar_shows_a_wrong_health_answer_as_offline()
    {
        PathingClient client = new(new HttpClient(Answers(_ => """{"apiVersion":1,"agentProtocol":1,"bots":0,"online":0}""", Serves))
            { BaseAddress = ServiceUrl });
        using ControlCenterFactory factory = new(pathing: client);
        await factory.Services.GetRequiredService<PathingStatus>().RefreshAsync(TestContext.Current.CancellationToken);

        string bar = await factory.RenderAsync<StatusBar>();

        Assert.Contains("Pathing Service offline", bar);
        Assert.Contains("not the Pathing Service", bar);
    }

    private static PathingStatus Status(Func<HttpRequestMessage, string> health, PathingCapabilities? capabilities = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IPathingClient>(new PathingClient(new HttpClient(Answers(health, capabilities ?? Serves)) { BaseAddress = ServiceUrl }));
        return new PathingStatus(services.BuildServiceProvider(), new PathingServiceOptions(ServiceUrl), NullLogger<PathingStatus>.Instance);
    }

    private static StubService Answers(Func<HttpRequestMessage, string> health, PathingCapabilities capabilities)
    {
        return new StubService(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == PathingApi.Capabilities ? Json(capabilities) : health(request),
                Encoding.UTF8, "application/json"),
        });
    }

    private static string Json<T>(T value)
    {
        return JsonSerializer.Serialize(value, ContractJson.Options);
    }
}
