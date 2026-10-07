using SharedLib;
using SharedLib.Forever;

using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

namespace ServiceTests.Forever;

/// <summary>
/// The route file format as a run reads it, now that <see cref="ForeverRouteFile"/> lives in
/// SharedLib for the Pathing Service too: the shipped example loads into the tasks the API builds,
/// each mistake is refused with the step it is in, and text reads exactly as the file does. The
/// Forever reader's logic checks (<c>Utilities/ForeverVisualReader</c>, LogicTest.Route.cs) cover
/// the same ground on Windows, together with the start-up check on the baked mesh.
/// </summary>
public sealed class ForeverRouteFileTests
{
    private const int Durotar = 1411;

    private const string Head = "\"Name\": \"Test\", \"UIMapId\": 1411";

    private static readonly WorldMapAreaDB Maps = new(new DataConfig { Root = TestData.RepoJson, Exp = "forever" });

    public static TheoryData<string, string> Mistakes => new()
    {
        { $"{{ {Head}, \"Steps\": [] }}", "has no steps" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }}, \"Roam\": {{ \"Area\": [[51, 44], [53, 46]] }} }} ] }}",
            "step 1: a step is one of a Travel, a Roam, a Visit or a Quest" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Travel\": {{ \"Via\": [[52, 44]] }} }} ] }}", "step 1: To needs a point" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }} }}, {{ \"Travel\": {{ \"To\": [152, 44] }} }} ] }}",
            "step 2: the map point" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44], [53, 46]] }} }} ] }}", "step 1: a Roam needs an Until" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44]], \"Until\": {{ \"Kills\": 3 }} }} }} ] }}", "step 1: a Roam needs an Area" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44], [53, 46]], \"Until\": {{ \"Kills\": 0 }} }} }} ] }}",
            "step 1: Until needs Kills" },
        { "{ \"UIMapId\": 9999, \"Steps\": [ { \"Travel\": { \"To\": [50, 50] } } ] }", "not in the map data" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Travel\": {{ \"Too\": [52, 44] }} }} ] }}", "Too" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Visit\": {{ \"Role\": \"vendor\", \"Npc\": \"Gornek\" }} }} ] }}",
            "step 1: a Visit names one of a Role, an Npc, a QuestStart or a QuestEnd" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Visit\": {{ \"Role\": \"dentist\" }} }} ] }}", "step 1: dentist is not an NPC role" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Visit\": {{ \"Npc\": \"Gornek\", \"Title\": \"Trainer\" }} }} ] }}",
            "step 1: a Visit's Title goes with a Role" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Visit\": {{ \"QuestStart\": 0 }} }} ] }}", "step 1: a quest id is 1 or more" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Quest\": {{ \"Id\": 5441 }} }} ] }}", "step 1: a Quest needs an Until" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Quest\": {{ \"Until\": {{ \"Kills\": 5 }} }} }} ] }}", "step 1: a Quest needs its Id" },
        { $"{{ {Head}, \"Steps\": [ {{ \"Visit\": {{ \"Roll\": \"vendor\" }} }} ] }}", "Roll" },
        { $"{{ {Head}, \"CostZones\": \"Sometimes\", \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }} }} ] }}", "not Sometimes" },
        // A number would mean one thing to the run and another to the Control Center: names only.
        { $"{{ {Head}, \"CostZones\": 1, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }} }} ] }}", "CostZones is \"Apply\" or \"Ignore\"" },
    };

    [Fact]
    public void The_example_loads_as_travel_a_roam_until_kills_or_minutes_and_travel_back()
    {
        string example = ForeverRouteFile.Resolve(new DataConfig { Root = TestData.RepoJson, Exp = "forever" }, "durotar_example.json");

        bool loaded = ForeverRouteFile.TryLoad(example, Maps, out ForeverRoute route, out string error);

        Assert.True(loaded, error);
        Assert.Equal("Durotar example: south of Razor Hill", route.Name);
        Assert.False(route.Repeat);
        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverTravelTask { To: { UIMapId: Durotar, X: 52.5, Y: 44.5 }, Pulls: ForeverPulls.None }),
            task => Assert.True(task is ForeverRoamAreaTask { Pulls: ForeverPulls.Any, Until: { Kills: 3, Minutes: 10 }, Area.Corners.Count: 4 }),
            task => Assert.IsType<ForeverTravelTask>(task));
    }

    // Places named by who or what is there: found when the step begins, so the file names no point.
    [Fact]
    public void Visit_and_quest_steps_load_into_the_tasks_that_find_their_place()
    {
        const string json = """
            {
              "UIMapId": 1411,
              "Steps": [
                { "Visit": { "Role": "Innkeeper" } },
                { "Visit": { "Role": "classtrainer", "Title": "Warrior Trainer", "Pulls": "OnTheWay" } },
                { "Visit": { "Npc": "Innkeeper Grosk" } },
                { "Visit": { "QuestStart": 5441 } },
                { "Quest": { "Id": 5441, "Until": { "Kills": 5, "Minutes": 20 }, "Seed": 3 } },
                { "Visit": { "QuestEnd": 5441 } }
              ]
            }
            """;

        Assert.True(ForeverRouteFile.TryParse(json, "peons.json", Maps, out ForeverRoute route, out string error), error);

        Assert.Collection(route.Tasks,
            task => Assert.True(task is ForeverVisitTask { Target: { Kind: ForeverVisitKind.Role, Name: "innkeeper", Title: null }, Pulls: ForeverPulls.None }),
            task => Assert.True(task is ForeverVisitTask { Target: { Name: "classtrainer", Title: "Warrior Trainer" }, Pulls: ForeverPulls.OnTheWay }),
            task => Assert.True(task is ForeverVisitTask { Target: { Kind: ForeverVisitKind.Npc, Name: "Innkeeper Grosk" } }),
            task => Assert.True(task is ForeverVisitTask { Target: { Kind: ForeverVisitKind.QuestStart, QuestId: 5441 } }),
            task => Assert.True(task is ForeverQuestAreaTask { QuestId: 5441, Pulls: ForeverPulls.Any, Until: { Kills: 5, Minutes: 20 }, Seed: 3 }),
            task => Assert.True(task is ForeverVisitTask { Target: { Kind: ForeverVisitKind.QuestEnd, QuestId: 5441 } }));
        Assert.Equal(
            ["Visit the nearest innkeeper", "Visit the nearest classtrainer titled Warrior Trainer", "Visit Innkeeper Grosk",
             "Visit where quest 5441 is given", "Roam where quest 5441 sends", "Visit where quest 5441 is handed in"],
            Names(route.Tasks));
    }

    [Theory]
    [MemberData(nameof(Mistakes))]
    public void Each_mistake_is_refused_naming_its_step(string json, string expected)
    {
        using TempDataRoot data = new();
        string file = Path.Combine(data.Root, "route.json");
        File.WriteAllText(file, json);

        bool fromFile = ForeverRouteFile.TryLoad(file, Maps, out _, out string fileError);
        bool fromText = ForeverRouteFile.TryParse(json, "route.json", Maps, out _, out string textError);

        Assert.False(fromFile);
        Assert.False(fromText);
        Assert.Contains(expected, fileError);
        Assert.Equal(fileError, textError);
    }

    [Fact]
    public void Text_reads_as_the_file_does()
    {
        string example = Path.Combine(TestData.RepoJson, "path", ForeverRouteFile.Folder, "crossroads_trip.json");

        Assert.True(ForeverRouteFile.TryLoad(example, Maps, out ForeverRoute fromFile, out string fileError), fileError);
        Assert.True(ForeverRouteFile.TryParse(File.ReadAllText(example), "crossroads_trip.json", Maps,
            out ForeverRoute fromText, out string textError), textError);

        Assert.Equal(fromFile.Name, fromText.Name);
        Assert.Equal(fromFile.Repeat, fromText.Repeat);
        Assert.Equal(Names(fromFile.Tasks), Names(fromText.Tasks));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(", \"CostZones\": \"Ignore\"", false)]
    [InlineData(", \"CostZones\": \"Apply\"", true)]
    [InlineData(", \"costZones\": \"apply\"", true)]
    public void A_route_plans_with_the_cost_zones_only_when_it_says_so(string field, bool applied)
    {
        string json = $"{{ {Head}{field}, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }} }} ] }}";

        Assert.True(ForeverRouteFile.TryParse(json, "zones.json", Maps, out ForeverRoute route, out string error), error);

        Assert.Equal(applied, route.ApplyCostZones);
    }

    [Fact]
    public void A_route_without_a_name_is_named_after_its_file()
    {
        const string json = "{ \"UIMapId\": 1411, \"Steps\": [ { \"Travel\": { \"To\": [52, 44] } } ] }";

        Assert.True(ForeverRouteFile.TryParse(json, "my_trip.json", Maps, out ForeverRoute route, out string error), error);

        Assert.Equal("my_trip", route.Name);
    }

    private static IEnumerable<string> Names(IReadOnlyList<ForeverTask> tasks)
    {
        return tasks.Select(task => task.Name);
    }
}
