using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System;
using System.Numerics;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// The planner's own guards against work that never ends (known issue 32), for a caller that does
/// not come through the service's checks. Each call runs against a deadline, so a regression fails
/// the test instead of wedging the run.
/// </summary>
public sealed class RoutePlannerBoundsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_height_beyond_the_edge_of_the_world_is_zero_and_never_asked_for()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        using RoutePlanner planner = TestData.NewPlanner();

        // Azeroth's lane is open, so the lookup would reach Detour's tile loop.
        (int AreaId, float Z) baked = await Run(() => planner.GetAreaAndHeight(0, -8898.32f, -117.35608f));
        (int AreaId, float Z) beyond = await Run(() => planner.GetAreaAndHeight(0, 0, 3e11f));

        Assert.NotEqual(0f, baked.Z);
        Assert.Equal((0, 0f), beyond);
    }

    [Theory]
    [InlineData(0f, 1e12f)]
    [InlineData(1e5f, null)]
    [InlineData(float.NaN, null)]
    public async Task Tuning_past_its_caps_is_refused(float jitterYards, float? edgeMarginYards)
    {
        // The refusal comes before any lane, so no tiles are needed.
        using TempDataRoot data = new(withMapData: true);
        using RoutePlanner planner = new(NullLogger.Instance, data.Config(), new WorldMapAreaDB(data.Config()),
            new NavmeshBakeOptions(), new NavmeshQueryOptions());

        RouteOutcome outcome = await Run(() => planner.Find(new RouteQuery(1429, RouteSpace.World,
            new Vector3(-8898.32f, -117.35608f, 81.840546f), new Vector3(-8779.58f, -106.12012f, 0),
            new NavmeshQueryTuning(jitterYards, 7, edgeMarginYards))));

        Assert.Equal(RoutePlanStatus.Refused, outcome.Status);
        Assert.Empty(outcome.Points);
    }

    private static Task<T> Run<T>(Func<T> work)
    {
        return Task.Run(work, TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
    }
}
