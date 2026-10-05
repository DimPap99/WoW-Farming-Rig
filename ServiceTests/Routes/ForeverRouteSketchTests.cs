using Contracts;

using ControlCenter.Routes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>A Forever route's text read for drawing while it is written: comments and all.</summary>
public sealed class ForeverRouteSketchTests
{
    [Fact]
    public void The_shipped_example_reads_as_travel_roam_travel_on_durotar()
    {
        string example = File.ReadAllText(Path.Combine(TestData.RepoJson, "path", "_forever", "durotar_example.json"));

        Assert.True(ForeverRouteSketch.TryRead(example, out List<ForeverStepSketch> steps, out string error), error);

        Assert.Collection(steps,
            s => Assert.Equal(new ForeverStepSketch(true, 1411, [new Point2(52.5f, 44.5f)]), s, Same),
            s => Assert.Equal(new ForeverStepSketch(false, 1411, [new Point2(51.5f, 44.0f), new Point2(53.5f, 46.0f)]), s, Same),
            s => Assert.Equal(new ForeverStepSketch(true, 1411, [new Point2(52.5f, 43.0f)]), s, Same));
    }

    [Fact]
    public void Via_points_come_before_To_and_a_step_may_name_its_own_map()
    {
        const string text = """
            { "uimapid": 1411, "steps": [ { "travel": { "uimapid": 1413, "to": [5, 6], "via": [[1, 2], [3, 4]], }, }, ], }
            """;

        Assert.True(ForeverRouteSketch.TryRead(text, out List<ForeverStepSketch> steps, out string error), error);

        ForeverStepSketch travel = Assert.Single(steps);
        Assert.Equal(1413, travel.UiMapId);
        Assert.Equal([new Point2(1, 2), new Point2(3, 4), new Point2(5, 6)], travel.Points);
    }

    [Fact]
    public void Text_half_written_says_where_it_stops()
    {
        Assert.False(ForeverRouteSketch.TryRead("{ \"Steps\": [ { \"Travel\": ", out _, out string broken));
        Assert.False(ForeverRouteSketch.TryRead("{ \"Name\": \"x\" }", out _, out string noSteps));

        Assert.Contains("line 1", broken);
        Assert.Contains("no Steps", noSteps);
    }

    private static bool Same(ForeverStepSketch a, ForeverStepSketch b)
    {
        return a.Travel == b.Travel && a.UiMapId == b.UiMapId && a.Points.SequenceEqual(b.Points);
    }
}
