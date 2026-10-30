using Contracts;
using Contracts.Pathing;

using ServiceClients.Routes;

using System;
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

        Assert.True(ForeverRouteSketch.TryRead(example, out ForeverSketch sketch, out string error), error);

        Assert.Collection(sketch.Steps,
            s => Assert.Equal(new ForeverStepSketch(ForeverStepKind.Travel, 1411, [new Point2(52.5f, 44.5f)]), s, Same),
            s => Assert.Equal(new ForeverStepSketch(ForeverStepKind.Roam, 1411, [new Point2(51.5f, 44.0f), new Point2(53.5f, 46.0f)], 3), s, Same),
            s => Assert.Equal(new ForeverStepSketch(ForeverStepKind.Travel, 1411, [new Point2(52.5f, 43.0f)]), s, Same));
        Assert.Equal(CostZoneUse.Ignore, sketch.CostZones);
        Assert.False(sketch.Repeat);
    }

    [Fact]
    public void Visits_quests_the_loop_and_the_cost_zones_are_read_too()
    {
        const string text = """
            { "Name": "Loop", "UIMapId": 1411, "CostZones": "apply", "Repeat": true, "Steps": [
              { "Visit": { "Npc": "Innkeeper Grosk" } }, { "Visit": { "Role": "vendor" } }, { "Visit": { "QuestStart": 5441 } },
              { "Quest": { "Id": 5441, "Until": { "Kills": 8 } } } ] }
            """;

        Assert.True(ForeverRouteSketch.TryRead(text, out ForeverSketch sketch, out string error), error);

        Assert.Equal("Loop", sketch.Name);
        Assert.True(sketch.Repeat);
        Assert.Equal(CostZoneUse.Apply, sketch.CostZones);
        Assert.Equal([new StopTarget(Npc: "Innkeeper Grosk"), new StopTarget(Role: "vendor"), new StopTarget(QuestStart: 5441), null],
            sketch.Steps.Select(s => s.Visit));
        Assert.Equal(ForeverStepKind.Quest, sketch.Steps[3].Kind);
        Assert.Equal(8, sketch.Steps[3].Kills);
    }

    [Fact]
    public void Via_points_come_before_To_and_a_step_may_name_its_own_map()
    {
        const string text = """
            { "uimapid": 1411, "steps": [ { "travel": { "uimapid": 1413, "to": [5, 6], "via": [[1, 2], [3, 4]], }, }, ], }
            """;

        Assert.True(ForeverRouteSketch.TryRead(text, out ForeverSketch sketch, out string error), error);

        ForeverStepSketch travel = Assert.Single(sketch.Steps);
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
        return a.Kind == b.Kind && a.UiMapId == b.UiMapId && a.Kills == b.Kills && a.Points.SequenceEqual(b.Points);
    }
}
