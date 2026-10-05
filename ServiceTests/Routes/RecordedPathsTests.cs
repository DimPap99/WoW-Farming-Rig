using Contracts;
using Contracts.Pathing;

using ControlCenter.Routes;

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
    public void Points_recorded_with_a_height_are_world_yards_and_open_as_they_are()
    {
        Point3[] file = [new(-9449f, 64f, 56.6f), new(-9400f, 80f, 0)];

        Assert.False(RecordedPaths.AreMapPoints(file));
        Assert.Equal(file, RecordedPaths.ToWaypoints(file, Elwynn));
    }
}
