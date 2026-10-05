using Core;
using Core.Forever;

using Game;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // Route files: the shipped example loads into the tasks the API builds,
    // each mistake is refused with its step, a repeating agenda starts over,
    // and the start-up check finds the example's points on the baked mesh.
    private static void RouteChecks(Action<string, bool, string> check)
    {
        DataConfig data = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
        WorldMapAreaDB maps = new(data);

        string example = ForeverRouteFile.Resolve(data, "durotar_example.json");
        bool loaded = ForeverRouteFile.TryLoad(example, maps, out ForeverRoute route, out string error);
        check("route: the example loads as travel, a roam until 3 kills or 10 minutes, and travel back",
            loaded && !route.Repeat && route.Tasks.Count == 3 &&
            route.Tasks[0] is ForeverTravelTask { To: { UIMapId: ForeverVerifiedSetup.Durotar, X: 52.5, Y: 44.5 }, Pulls: ForeverPulls.None } &&
            route.Tasks[1] is ForeverRoamAreaTask { Pulls: ForeverPulls.Any, Until: { Kills: 3, Minutes: 10 }, Area.Corners.Count: 4 } &&
            route.Tasks[2] is ForeverTravelTask,
            loaded ? string.Join(" / ", route.Tasks.Select(task => task.Name)) : error);

        // Each mistake, in a file of its own.
        string folder = Path.Combine(Path.GetTempPath(), $"forever-routes-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        string Refusal(string json)
        {
            string path = Path.Combine(folder, "route.json");
            File.WriteAllText(path, json);
            return ForeverRouteFile.TryLoad(path, maps, out _, out string why) ? "loaded" : why;
        }
        try
        {
            const string head = "\"Name\": \"Test\", \"UIMapId\": 1411";
            (string Json, string Expected)[] mistakes =
            [
                ($"{{ {head}, \"Steps\": [] }}", "has no steps"),
                ($"{{ {head}, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }}, \"Roam\": {{ \"Area\": [[51, 44], [53, 46]] }} }} ] }}",
                    "step 1: a step is either a Travel or a Roam"),
                ($"{{ {head}, \"Steps\": [ {{ \"Travel\": {{ \"Via\": [[52, 44]] }} }} ] }}", "step 1: To needs a point"),
                ($"{{ {head}, \"Steps\": [ {{ \"Travel\": {{ \"To\": [52, 44] }} }}, {{ \"Travel\": {{ \"To\": [152, 44] }} }} ] }}",
                    "step 2: the map point"),
                ($"{{ {head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44], [53, 46]] }} }} ] }}", "step 1: a Roam needs an Until"),
                ($"{{ {head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44]], \"Until\": {{ \"Kills\": 3 }} }} }} ] }}", "step 1: a Roam needs an Area"),
                ($"{{ {head}, \"Steps\": [ {{ \"Roam\": {{ \"Area\": [[51, 44], [53, 46]], \"Until\": {{ \"Kills\": 0 }} }} }} ] }}",
                    "step 1: Until needs Kills"),
                ("{ \"UIMapId\": 9999, \"Steps\": [ { \"Travel\": { \"To\": [50, 50] } } ] }", "not in the map data"),
                ($"{{ {head}, \"Steps\": [ {{ \"Travel\": {{ \"Too\": [52, 44] }} }} ] }}", "Too"),
            ];
            List<string> wrong = [];
            foreach ((string json, string expected) in mistakes)
            {
                string refusal = Refusal(json);
                if (!refusal.Contains(expected, StringComparison.Ordinal))
                    wrong.Add($"expected '{expected}', got '{refusal}'");
            }
            check("route: each mistake is refused, naming the step it is in, and a misspelt field too",
                wrong.Count == 0, string.Join("; ", wrong));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        ForeverTask first = ForeverTasks.TravelTo(new(ForeverVerifiedSetup.Durotar, 52, 44));
        ForeverTask second = ForeverTasks.TravelTo(new(ForeverVerifiedSetup.Durotar, 53, 45));
        ForeverAgenda loop = new([first, second], repeat: true);
        loop.Advance();
        loop.Advance();
        check("agenda: a repeating agenda starts over after its last task, a lap on",
            loop.Current == first && loop.Laps == 1 && !loop.Finished && loop.Describe().Contains("lap 2"), loop.Describe());

        // The start-up check against the baked mesh, from a player standing
        // in Razor Hill.
        maps.TryGet(ForeverVerifiedSetup.Durotar, out WorldMapArea durotar);
        if (!loaded || !Directory.Exists(ForeverNavmesh.CacheDirectory(data, durotar.Continent)))
        {
            Console.WriteLine("SKIP route: no baked Forever mesh or no example route");
            return;
        }
        new ForeverMapPoint(ForeverVerifiedSetup.Durotar, 52.5, 43.5).TryToWorld(maps, out Vector2 standing, out _, out _);
        VisualBarSample player = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            Position = new VisualPositionSample(ForeverVerifiedSetup.Durotar, 0.525, 0.435, 0),
            WorldPosition = new VisualWorldPositionSample(standing.X, standing.Y, 0, 1)
        };
        ForeverNavigator navigator = new(NullLogger<ForeverNavigator>.Instance, null!, new MouseCameraInput(null!), maps, data);
        bool Preflight(IEnumerable<ForeverTask> tasks, out string reason) =>
            new ForeverTaskActivity(NullLogger<ForeverTaskActivity>.Instance, new ForeverAgenda(tasks), null!,
                navigator, null!, new ClassConfiguration(), maps, new ForeverMobMemory()).TryPreflight(player, out reason);
        bool examplePasses = Preflight(route.Tasks, out string exampleReason);
        bool elsewhereRefused = !Preflight([ForeverTasks.TravelTo(new(ForeverVerifiedSetup.TirisfalGlades, 60, 50))],
            out string elsewhere);
        check("route: the start-up check finds the example's points and area on the baked mesh, and refuses a point on another continent",
            examplePasses && elsewhereRefused && elsewhere.Contains("another continent"),
            $"example: {examplePasses} '{exampleReason}'; elsewhere: '{elsewhere}'");
        // Another zone of the same continent: the Crossroads in The Barrens,
        // where the player stood on 2026-10-04.
        const int barrens = 1413;
        bool nextZonePasses = Preflight([ForeverTasks.TravelTo(new(barrens, 51.8, 32.2))], out string nextZone);
        check("route: a point in the next zone over passes the start-up check from Razor Hill",
            nextZonePasses, nextZone);
    }
}
