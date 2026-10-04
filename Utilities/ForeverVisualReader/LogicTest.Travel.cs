using Core;
using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // Travelling: map points to world yards, following a leg's path, the
    // unstick ladder and the progress rule, decided without input; then a
    // walk along a real Durotar path when the baked mesh is on disk.
    private static void TravelChecks(Action<string, bool, string> check)
    {
        DataConfig data = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
        WorldMapAreaDB maps = new(data);

        // The conversion the live preflight checks against the client's own
        // world position (PlayerMapAgrees), reached from map percent.
        maps.TryGet(ForeverVerifiedSetup.Durotar, out WorldMapArea durotar);
        Vector3 checkedWorld = new VisualMapPointSample(ForeverVerifiedSetup.Durotar, 0.5, 0.5).ToWorld(durotar);
        bool converted = new ForeverMapPoint(ForeverVerifiedSetup.Durotar, 50, 50)
            .TryToWorld(maps, out Vector2 world, out int instance, out string reason);
        check("travel: a map point in percent lands where the live-checked conversion puts it",
            converted && Vector2.Distance(world, new(checkedWorld.X, checkedWorld.Y)) < 0.01f && instance == 1,
            $"{world} vs {checkedWorld}, instance {instance}, {reason}");
        bool offMapRefused = !new ForeverMapPoint(ForeverVerifiedSetup.Durotar, 101, 50)
            .TryToWorld(maps, out _, out _, out string offMap);
        bool unknownRefused = !new ForeverMapPoint(9999, 50, 50)
            .TryToWorld(maps, out _, out _, out string unknown);
        // Elwynn Forest: any zone in the map data converts, on its own continent.
        bool otherZoneConverts = new ForeverMapPoint(1429, 50, 50)
            .TryToWorld(maps, out _, out int elwynnInstance, out _);
        check("travel: points off the map or on a map the data lacks are refused, with why; any other zone converts",
            offMapRefused && offMap.Contains("outside") && unknownRefused && unknown.Contains("not in the map data") &&
            otherZoneConverts && elwynnInstance == 0,
            $"'{offMap}' / '{unknown}' / Elwynn on instance {elwynnInstance}");

        // A straight leg with a point every 2 yards.
        List<Vector2> straight = [.. Enumerable.Range(0, 51).Select(i => new Vector2(i * 2, 0))];
        int next = 0;
        Vector2 carrot = ForeverWalker.Follow(straight, ref next, new(0, 0), out float remaining);
        check("travel: the walk heads 20 yards along the path, with the whole leg left",
            Vector2.Distance(carrot, new(20, 0)) < 0.01f && Math.Abs(remaining - 100) < 0.01f, $"{carrot}, {remaining:0.0}");
        bool ahead = true, shrinking = true, forward = true;
        float before = remaining;
        int lastNext = next;
        for (float x = 1; x <= 90; x += 1.5f)
        {
            Vector2 here = new(x, 0.8f);
            carrot = ForeverWalker.Follow(straight, ref next, here, out remaining);
            ahead &= carrot.X > here.X;
            shrinking &= remaining < before;
            forward &= next >= lastNext;
            before = remaining;
            lastNext = next;
        }
        carrot = ForeverWalker.Follow(straight, ref next, new(99.5f, 0), out remaining);
        check("travel: walking the leg, the point headed for stays ahead, what is left shrinks, and the end is the last point",
            ahead && shrinking && forward && carrot == straight[^1] && remaining < 1, $"ahead={ahead}, shrinking={shrinking}, forward={forward}, {carrot}, {remaining:0.0}");

        // An L-shaped leg whose corner the walk cut.
        List<Vector2> corner = [.. Enumerable.Range(0, 21).Select(i => new Vector2(i * 2, 0)),
            .. Enumerable.Range(1, 20).Select(i => new Vector2(40, i * 2))];
        next = 15;
        ForeverWalker.Follow(corner, ref next, new(38.5f, 4), out remaining);
        check("travel: a walk that cut a corner is past the points before it",
            next >= 21 && remaining < 40, $"next={next}, remaining={remaining:0.0}");

        List<Vector2> segment = [new(0, 0), new(10, 0)];
        check("travel: off the path is the distance to the leg toward the next point",
            Math.Abs(ForeverWalker.OffPath(segment, 1, new(5, 3)) - 3) < 0.01f &&
            ForeverWalker.OffPath(segment, 1, new(5, 0)) < 0.01f &&
            Math.Abs(ForeverWalker.OffPath(segment, 0, new(0, 4)) - 4) < 0.01f, "");

        ForeverUnstick[] ladder = [.. Enumerable.Range(1, 6).Select(rung => ForeverWalker.RungFor(rung, canBackOut: true, canSkipVia: true))];
        check("travel: the unstick ladder steps aside, backs out, skips the point on the way, then steps and backs out in turn",
            ladder.SequenceEqual([ForeverUnstick.Sidestep, ForeverUnstick.BackOut, ForeverUnstick.SkipVia,
                ForeverUnstick.BackOut, ForeverUnstick.Sidestep, ForeverUnstick.BackOut]), string.Join(", ", ladder));
        check("travel: without a move-backward key it steps aside, and with no point on the way left it does not skip",
            Enumerable.Range(1, 6).All(rung => ForeverWalker.RungFor(rung, canBackOut: false, canSkipVia: false) == ForeverUnstick.Sidestep) &&
            ForeverWalker.RungFor(3, canBackOut: true, canSkipVia: false) == ForeverUnstick.Sidestep, "");

        long t0 = Stopwatch.GetTimestamp();
        long At(double seconds) => t0 + (long)(seconds * Stopwatch.Frequency);
        ForeverProgress walk = new();
        walk.Restart(At(0));
        bool first = walk.Observe(100, At(0));
        bool shuffle = walk.Observe(99.5f, At(1));
        bool closer = walk.Observe(98, At(2));
        check("travel: only a new closest approach by a yard counts as progress, and 20 seconds without one gives up",
            first && !shuffle && closer && !walk.GaveUp(At(21)) && walk.GaveUp(At(22.5)),
            $"first={first}, shuffle={shuffle}, closer={closer}");

        ForeverTask there = ForeverTasks.TravelTo(new(ForeverVerifiedSetup.Durotar, 52, 43));
        check("agenda: tasks the run options name are the agenda",
            ForeverAgenda.For(new ForeverRunOptions { Tasks = [there], MaxCompletedCycles = 1 }).Current == there &&
            there is ForeverTravelTask { Pulls: ForeverPulls.None, Via.Count: 0 }, $"{there}");

        // A walker that heads straight for the point the leg says, 1.5 yards
        // a step, along a real path from Razor Hill: it arrives, on the mesh.
        string cache = ForeverNavmesh.CacheDirectory(data, durotar.Continent);
        if (!Directory.Exists(cache))
        {
            Console.WriteLine($"SKIP travel: no baked Forever mesh at {cache}");
            return;
        }
        using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, cache);
        new ForeverMapPoint(ForeverVerifiedSetup.Durotar, 52, 43).TryToWorld(maps, out Vector2 start, out _, out _);
        new ForeverMapPoint(ForeverVerifiedSetup.Durotar, 55, 45.5).TryToWorld(maps, out Vector2 end, out _, out _);
        nav.TryGetHeight(start.X, start.Y, out float startZ);
        nav.TryGetHeight(end.X, end.Y, out float endZ);
        List<Vector2> real = [.. (nav.FindPath(new(start.X, start.Y, startZ), new(end.X, end.Y, endZ), null)?.locations ?? [])
            .Select(location => new Vector2(location.X, location.Y))];
        Vector2 walker = start;
        next = 0;
        int steps = 0;
        bool onMesh = true;
        while (steps < 400 && Vector2.Distance(walker, end) > ForeverTravel.ArriveYards && real.Count >= 2)
        {
            Vector2 toward = ForeverWalker.Follow(real, ref next, walker, out _);
            onMesh &= nav.TryGetHeight(toward.X, toward.Y, out _);
            Vector2 step = toward - walker;
            walker = step.Length() <= 1.5f ? toward : walker + Vector2.Normalize(step) * 1.5f;
            steps++;
        }
        check("travel: following a real 190-yard Durotar path from Razor Hill arrives, heading only for points on the mesh",
            real.Count >= 2 && Vector2.Distance(walker, end) <= ForeverTravel.ArriveYards && onMesh,
            $"path points {real.Count}, steps {steps}, left {Vector2.Distance(walker, end):0.0} yd, onMesh={onMesh}");
    }
}
