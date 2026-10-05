using Core;
using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // Roaming: areas in map coordinates and world yards, the conditions a
    // roam ends on, and the spot picker, first on plain stand-ins for the
    // mesh, then on the baked Durotar mesh when it is on disk.
    private static void RoamChecks(Action<string, bool, string> check)
    {
        DataConfig data = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
        WorldMapAreaDB maps = new(data);
        const int durotar = ForeverVerifiedSetup.Durotar;

        ForeverArea rectangle = ForeverArea.FromCorners(durotar, [(51, 42), (54, 45)]);
        bool resolved = rectangle.TryToWorld(maps, out ForeverWorldArea razorHill, out int instance, out string reason);
        new ForeverMapPoint(durotar, 52.5, 43.5).TryToWorld(maps, out Vector2 middle, out _, out _);
        new ForeverMapPoint(durotar, 56, 43.5).TryToWorld(maps, out Vector2 east, out _, out _);
        check("roam: two corners make a rectangle, and its world polygon holds what lies inside it",
            rectangle.Corners.Count == 4 && resolved && instance == 1 &&
            razorHill.Contains(middle) && !razorHill.Contains(east),
            $"corners={rectangle.Corners.Count}, resolved={resolved} '{reason}', instance={instance}");

        ForeverWorldArea triangle = new([new(0, 0), new(100, 0), new(0, 100)]);
        bool tooFewRefused = !new ForeverArea(durotar, [(51, 42)]).TryToWorld(maps, out _, out _, out string tooFew);
        check("roam: a polygon holds points by the even-odd rule, and an area needs three corners or two",
            triangle.Contains(new(20, 20)) && !triangle.Contains(new(70, 70)) && !triangle.Contains(new(-1, 5)) &&
            tooFewRefused && tooFew.Contains("three corners"), tooFew);

        ForeverTaskProgress started = new();
        bool beforeStart = ForeverUntil.AfterMinutes(0).IsMet(started, default);
        started.Start(Stopwatch.GetTimestamp() - (long)(61 * Stopwatch.Frequency));
        VisualBarSample level7 = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with { PlayerIdentity = new(3, 8, 7) };
        check("until: minutes count from when the task began, and a level holds once the player has it",
            !beforeStart && ForeverUntil.AfterMinutes(1).IsMet(started, default) &&
            !ForeverUntil.AfterMinutes(2).IsMet(started, default) &&
            ForeverUntil.AtLevel(7).IsMet(new(), level7) && !ForeverUntil.AtLevel(8).IsMet(new(), level7), "");

        // A plain square where every point is walkable and reachable.
        ForeverWorldArea square = new([new(0, 0), new(100, 0), new(100, 100), new(0, 100)]);
        static Vector2? Anywhere(Vector2 hint) => hint;
        static bool Reachable(Vector2 spot) => true;
        ForeverSpotPicker picker = new(square, seed: 7);
        List<Vector2> spots = [];
        for (int i = 0; i < 8; i++)
        {
            if (picker.Next(Anywhere, Reachable) is { } spot)
                spots.Add(spot);
        }
        ForeverSpotPicker again = new(square, seed: 7);
        bool inside = spots.All(square.Contains);
        bool apart = spots.Zip(spots.Skip(1)).All(pair => Vector2.Distance(pair.First, pair.Second) >= picker.Spacing);
        bool same = spots.SequenceEqual(spots.Select(_ => again.Next(Anywhere, Reachable) ?? default));
        check("roam: spots fall inside the area, each away from the last, and the same seed draws the same spots",
            spots.Count == 8 && inside && apart && same && picker.Spacing == ForeverSpotPicker.MaxSpacingYards,
            $"{spots.Count} spots, inside={inside}, apart={apart}, same={same}, spacing={picker.Spacing}");
        check("roam: in a small area the spots are closer together",
            new ForeverSpotPicker(new([new(0, 0), new(30, 0), new(30, 20), new(0, 20)]), 1).Spacing == 8, "");

        Vector2 outside = new(-50, 50);
        List<float> asked = [];
        int calls = 0;
        Vector2? entry = new ForeverSpotPicker(square, seed: 3).Nearest(outside, Anywhere, spot =>
        {
            asked.Add(Vector2.Distance(outside, spot));
            return ++calls > 2;
        });
        check("roam: from outside, the nearest spots inside are asked first, and the first reachable is taken",
            entry is { } door && asked.Count == 3 && asked.Zip(asked.Skip(1)).All(pair => pair.First <= pair.Second) &&
            Math.Abs(Vector2.Distance(outside, door) - asked[2]) < 0.001f,
            $"asked {string.Join(", ", asked.Select(d => d.ToString("0.0")))}, entry {entry}");

        // Razor Hill on the baked mesh: walkable ground under each draw,
        // reachable from the middle of the area.
        maps.TryGet(durotar, out WorldMapArea area);
        string cache = ForeverNavmesh.CacheDirectory(data, area.Continent);
        if (!Directory.Exists(cache))
        {
            Console.WriteLine($"SKIP roam: no baked Forever mesh at {cache}");
            return;
        }
        using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, cache);
        nav.TryGetHeight(middle.X, middle.Y, out float middleZ);
        Vector2? Ground(Vector2 hint) =>
            nav.TryGetHeight(hint.X, hint.Y, out float z) &&
            nav.TrySnapWalkable(new(hint.X, hint.Y, z), out Vector3 snapped, out _)
                ? new Vector2(snapped.X, snapped.Y) : null;
        bool FromMiddle(Vector2 spot)
        {
            if (!nav.TryGetHeight(spot.X, spot.Y, out float z))
                return false;
            var path = nav.FindPath(new(middle.X, middle.Y, middleZ), new(spot.X, spot.Y, z), null);
            return path is { locations.Count: >= 2 } &&
                Vector2.Distance(new(path.locations[^1].X, path.locations[^1].Y), spot) <= 1;
        }
        ForeverSpotPicker onMesh = new(razorHill, seed: 11);
        List<Vector2> meshSpots = [];
        for (int i = 0; i < 10; i++)
        {
            if (onMesh.Next(Ground, FromMiddle) is { } spot)
                meshSpots.Add(spot);
        }
        check("roam: around Razor Hill every spot is inside the area, on the mesh and reachable from its middle",
            meshSpots.Count == 10 && meshSpots.All(razorHill.Contains) &&
            meshSpots.All(spot => nav.TryGetHeight(spot.X, spot.Y, out _)) && meshSpots.All(FromMiddle),
            $"{meshSpots.Count} spots, area {razorHill.Width:0} x {razorHill.Height:0} yards, spacing {onMesh.Spacing:0}");
    }
}
