using Contracts;
using Contracts.Pathing;

using ServiceClients.Routes;

using ServiceClients.Pathing;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>The path library's files as the route designer opens and saves them.</summary>
public sealed class RecordedPathsTests
{
    // Elwynn Forest, as the service publishes it.
    private static readonly ZoneInfo Elwynn = new(1429, 12, "Elwynn Forest", "Azeroth", 0, 1535.4166f, -1935.4166f, -7939.583f, -10254.166f);

    [Fact]
    public void Map_points_open_as_world_waypoints_and_save_back_as_the_same_map_points()
    {
        Point3[] file = [new(48.5f, 59.1f, 0), new(50f, 60f, 0)];

        Point3[] waypoints = RecordedPaths.ToWaypoints(file, Elwynn);
        Point3[] saved = RecordedPaths.ToFile(waypoints, Elwynn);

        Assert.Equal(ZoneMath.ToWorld(Elwynn, file[0]), waypoints[0]);
        Assert.Equal(file[0].X, saved[0].X, 3);
        Assert.Equal(file[1].Y, saved[1].Y, 3);
        Assert.All(saved, p => Assert.Equal(0, p.Z));
    }

    [Fact]
    public void A_recorded_path_holds_a_design_of_shape_nodes_on_replanned_legs_only()
    {
        Point3 a = ZoneMath.ToWorld(Elwynn, new Point3(48, 59, 0));
        Point3 b = ZoneMath.ToWorld(Elwynn, new Point3(50, 60, 0));
        RouteDesign plain = new([new RouteNode(a), new RouteNode(b, Leg: LegKind.Exact)]);

        Assert.True(RecordedPaths.Holds(plain));
        Assert.False(RecordedPaths.Holds(plain with { Loop = true }));
        Assert.False(RecordedPaths.Holds(new RouteDesign([new RouteNode(a, Leg: LegKind.Exact), new RouteNode(b)])));
        Assert.False(RecordedPaths.Holds(new RouteDesign([new RouteNode(a, NodeKind.Required), new RouteNode(b)])));
    }

    [Fact]
    public void Points_recorded_with_a_height_are_world_yards_and_open_as_they_are()
    {
        Point3[] file = [new(-9449f, 64f, 56.6f), new(-9400f, 80f, 0)];

        Assert.False(RecordedPaths.AreMapPoints(file));
        Assert.Equal(file, RecordedPaths.ToWaypoints(file, Elwynn));
    }
}
