using Contracts;
using Contracts.Pathing;

using ControlCenter.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients;
using ServiceClients.Pathing;

using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// A point's zone as the Control Center names it: the zone maps' guess until the service's area
/// grid (checked into the repository) has been asked, then the zone the grid puts the point in.
/// </summary>
public sealed class ZoneLookupTests
{
    private const int Kalimdor = 1;

    [Fact]
    public async Task The_area_grid_names_the_zone_where_the_zone_maps_overlap()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());
        using ServiceProvider services = new ServiceCollection().AddSingleton<IPathingClient>(client).BuildServiceProvider();
        ZoneLookup zones = new(services);

        ServiceResult<WorldInfo> info = await client.GetWorldAsync(TestContext.Current.CancellationToken);
        WorldSnapshot world = new(info.Value!, [], []);
        ZoneInfo durotar = world.Zone(1411)!;
        ZoneInfo barrens = world.Zone(1413)!;
        // South Durotar lies further inside the Barrens' map than inside Durotar's; the east of the
        // Barrens inside Durotar's map.
        Point3 southDurotar = ZoneMath.ToWorld(durotar, new Point3(48, 85, 0));
        Point3 eastBarrens = ZoneMath.ToWorld(barrens, new Point3(55, 35, 0));

        Assert.Equal("The Barrens", zones.Known(world, Kalimdor, southDurotar)?.Name);

        Assert.True(await zones.LearnAsync(Kalimdor, [southDurotar, eastBarrens], TestContext.Current.CancellationToken));
        Assert.False(await zones.LearnAsync(Kalimdor, [southDurotar], TestContext.Current.CancellationToken));

        Assert.Equal("Durotar", zones.Known(world, Kalimdor, southDurotar)?.Name);
        Assert.Equal("The Barrens", zones.Known(world, Kalimdor, eastBarrens)?.Name);

        ServiceResult<AreaAtPoint> area = await client.GetAreaAsync(Kalimdor, southDurotar.X, southDurotar.Y, TestContext.Current.CancellationToken);
        Assert.Equal(1411, area.Value?.UiMapId);
    }
}
