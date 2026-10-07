using ControlCenter.Components.Pages;
using ControlCenter.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Pathing;

using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// The World workspace: every world page as a mode over one world view, the one its address names shown, and
/// the old addresses coming to their modes. The route designer starts planned as the client's bot plans.
/// </summary>
public sealed class WorldWorkspaceTests
{
    [Theory]
    [InlineData("/map", "?quest=7", "world?mode=explore&quest=7")]
    [InlineData("/routes", "", "world?mode=route")]
    [InlineData("/npc-routes", "", "world?mode=npcs")]
    [InlineData("/cost-zones", "", "world?mode=costs")]
    [InlineData("/navmesh", "", "world?mode=navmesh")]
    [InlineData("/live", "", "world?mode=live")]
    public void Each_old_address_comes_to_its_mode_with_its_query(string path, string query, string mode)
    {
        Assert.Equal(mode, WorldWorkspace.AddressOf(path, query));
    }

    [Fact]
    public async Task The_workspace_offers_every_mode_and_shows_the_one_its_address_names()
    {
        using PathingServiceFactory service = new(TestData.RepoJson, TestData.Client);
        using ControlCenterFactory factory = new(pathing: new PathingClient(service.CreateClient()));
        await LoadAsync(factory);

        string route = await factory.RenderAsync<WorldWorkspace>(uri: "/world?mode=route");
        string home = await factory.RenderAsync<WorldWorkspace>(uri: "/");

        foreach (WorldWorkspace.WorldMode mode in WorldWorkspace.Modes)
        {
            Assert.Contains($"href=\"world?mode={mode.Id}\"", route);
        }

        // The designer, and no other mode yet: a mode is made the first time it is shown.
        Assert.Contains("New legs", route);
        Assert.Contains("Undo", route);
        Assert.DoesNotContain("Live view", route);
        Assert.Contains("Live view", home);
    }

    [Theory]
    [InlineData("forever", "Ignore")]
    [InlineData(TestData.Client, "Apply")]
    public async Task A_new_route_is_planned_as_the_clients_bot_plans_it(string client, string costZones)
    {
        using PathingServiceFactory service = new(TestData.RepoJson, client);
        using ControlCenterFactory factory = new(pathing: new PathingClient(service.CreateClient()));
        await LoadAsync(factory);

        string designer = await factory.RenderAsync<RouteMap>();

        Assert.Matches(new Regex($"aria-pressed=\"true\"[^>]*>\\s*{costZones}\\s*<"), designer);
    }

    private static async Task LoadAsync(ControlCenterFactory factory)
    {
        await factory.Services.GetRequiredService<PathingStatus>().RefreshAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(await factory.Services.GetRequiredService<WorldCache>().GetAsync());
    }
}
