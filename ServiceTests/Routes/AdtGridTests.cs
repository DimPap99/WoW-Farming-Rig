using Contracts;

using ControlCenter.World;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>The bake API's ADT grid as the navmesh page counts it: x down from the world's edge along world x.</summary>
public sealed class AdtGridTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(32, 32)]
    [InlineData(28, 26)]
    [InlineData(63, 63)]
    public void An_adts_bounds_hold_the_points_that_name_it(int x, int y)
    {
        (Point3 min, Point3 max) = AdtGrid.Bounds(x, y);

        Assert.Equal(AdtGrid.AdtYd, max.X - min.X, 2);
        Assert.Equal((x, y), AdtGrid.Of((min.X + max.X) / 2, (min.Y + max.Y) / 2));
        Assert.Equal((x, y), AdtGrid.Of(max.X - 1, max.Y - 1));
    }

    [Fact]
    public void The_worlds_middle_is_where_the_grid_turns_to_its_second_half()
    {
        Assert.Equal((32, 32), AdtGrid.Of(-1, -1));
        Assert.Equal((31, 31), AdtGrid.Of(1, 1));
    }
}
