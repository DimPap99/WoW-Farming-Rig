using ControlCenter.Components.Pages;
using ControlCenter.Components.Shared;
using ControlCenter.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;

using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The layers' panel on the world pages, against a Forever Pathing Service in memory.</summary>
public sealed class WorldLayersTests
{
    private const string Forever = "forever";
    private const int KoboldCampCleanup = 7;

    [Fact]
    public async Task A_quest_named_by_the_page_lists_what_it_asks_and_where()
    {
        using PathingServiceFactory service = new(TestData.RepoJson, Forever);
        using ControlCenterFactory factory = new(pathing: new PathingClient(service.CreateClient()));
        WorldSnapshot world = await WorldOf(factory);

        string panel = await factory.RenderAsync<WorldLayers>(new Dictionary<string, object?>
        {
            [nameof(WorldLayers.World)] = world,
            [nameof(WorldLayers.QuestId)] = KoboldCampCleanup,
        });

        Assert.Contains("Kobold Camp Cleanup", panel);
        Assert.Contains("Get it from Marshal McBride", panel);
        Assert.Contains("Kill Kobold Vermin", panel);
        Assert.Contains("Hand it in to Marshal McBride", panel);
        Assert.Contains("Elwynn Forest", panel);
        // No page asked for waypoints: no place offers to be one.
        Assert.DoesNotContain("as a waypoint", panel);
    }

    [Fact]
    public async Task The_map_opens_the_quest_its_address_names()
    {
        using PathingServiceFactory service = new(TestData.RepoJson, Forever);
        using ControlCenterFactory factory = new(pathing: new PathingClient(service.CreateClient()));
        await WorldOf(factory);

        string map = await factory.RenderAsync<WorldMap>(uri: "/map?quest=7");

        Assert.Contains("Kill Kobold Vermin", map);
    }

    [Fact]
    public async Task The_world_pages_offer_the_layers_and_quests()
    {
        using PathingServiceFactory service = new(TestData.RepoJson, Forever);
        using ControlCenterFactory factory = new(pathing: new PathingClient(service.CreateClient()));
        await WorldOf(factory);

        string map = await factory.RenderAsync<WorldMap>();
        string designer = await factory.RenderAsync<RouteMap>();

        Assert.All(new[] { map, designer }, page =>
        {
            Assert.Contains("Elwynn Forest", page);
            Assert.Contains("Find a quest by name", page);
            Assert.Contains("Skinnable", page);
        });
        Assert.Contains("Find a creature on", map);
    }

    private static async Task<WorldSnapshot> WorldOf(ControlCenterFactory factory)
    {
        await factory.Services.GetRequiredService<PathingStatus>().RefreshAsync(TestContext.Current.CancellationToken);
        WorldSnapshot? world = await factory.Services.GetRequiredService<WorldCache>().GetAsync();
        Assert.NotNull(world);
        return world;
    }
}
