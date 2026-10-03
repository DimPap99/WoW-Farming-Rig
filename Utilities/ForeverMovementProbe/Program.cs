using Core;
using Core.Forever;

using Game;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PPather.Navmesh;

using SharedLib;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

// An explicit, bounded input probe. The regular Forever bot remains guarded.
bool move = args.Contains("--move");
if (args.Any(arg => arg is not ("--move" or "--check" or "--pid") &&
    !int.TryParse(arg, out _)) || (move && args.Contains("--check")))
{
    Console.Error.WriteLine("Use --check or --move [--pid <WowB process ID>].");
    return 1;
}

int pidIndex = Array.IndexOf(args, "--pid");
int? requestedPid = null;
if (pidIndex >= 0)
{
    if (pidIndex + 1 >= args.Length || !int.TryParse(args[pidIndex + 1], out int pid) || pid <= 0)
    {
        Console.Error.WriteLine("Use --pid with a positive WowB process ID.");
        return 1;
    }
    requestedPid = pid;
}

Process? wow = Process.GetProcessesByName("WowB")
    .OrderByDescending(p => p.StartTime)
    .FirstOrDefault(p => requestedPid is null || p.Id == requestedPid);
if (wow is null)
{
    Console.Error.WriteLine("WowB is not running.");
    return 2;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

try
{
    WowProcess process = new(cts, Options.Create(new StartupConfigPid { Id = wow.Id }));
    using WowScreenWGC screen = new(NullLogger<WowScreenWGC>.Instance, process, []);
    ForeverVisualBarTracker tracker = new(screen, new StartupClientVersion(process.FileVersion));
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    WorldMapAreaDB maps = new(config);

    int freshSamples = 0;
    int positionPairs = 0;
    float lastMapError = 0;
    for (int attempt = 0; attempt < 100 && !cts.IsCancellationRequested; attempt++)
    {
        screen.Update();
        tracker.Update();
        if (!tracker.TryGetLatest(out VisualBarSample bars))
        {
            await Task.Delay(100, cts.Token);
            continue;
        }
        freshSamples++;
        if (bars.Position is not { } map || bars.WorldPosition is not { } world)
        {
            await Task.Delay(100, cts.Token);
            continue;
        }
        positionPairs++;

        if (!maps.TryGet(map.UIMapId, out WorldMapArea area) ||
            area.MapID != world.InstanceMapId ||
            area.Continent is not ("Azeroth" or "Kalimdor"))
        {
            Console.Error.WriteLine("The live map or instance is not a baked Forever continent.");
            return 2;
        }

        Vector3 mapWorld = map.ToWorld(area);
        Vector2 origin = new((float)world.X, (float)world.Y);
        lastMapError = Vector2.Distance(new(mapWorld.X, mapWorld.Y), origin);
        if (lastMapError > 1f)
        {
            await Task.Delay(100, cts.Token);
            continue;
        }

        if (bars.PlayerHealth < 0.95 || bars.TargetPresent != false)
        {
            Console.Error.WriteLine("Probe requires at least 95% health and no selected target.");
            return 2;
        }

        string cache = ForeverNavmesh.CacheDirectory(config, area.Continent);
        if (!Directory.Exists(cache))
        {
            Console.Error.WriteLine($"No baked Forever navmesh at {cache}.");
            return 2;
        }

        using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, cache);
        if (!nav.TryGetHeight(origin.X, origin.Y, out float startZ))
        {
            Console.Error.WriteLine("No walkable mesh covers the live player X/Y.");
            return 2;
        }

        Vector3 start = new(origin.X, origin.Y, startZ);
        Vector3 ahead = new(start.X + 3f * MathF.Cos((float)map.Direction),
            start.Y + 3f * MathF.Sin((float)map.Direction), 0);
        if (!nav.TryGetHeight(ahead.X, ahead.Y, out float aheadZ) ||
            MathF.Abs(aheadZ - startZ) > 1.5f)
        {
            Console.Error.WriteLine("Forward mesh segment has no nearby walkable height.");
            return 2;
        }
        ahead.Z = aheadZ;

        var route = nav.FindPath(start, ahead, null);
        if (route is null || route.locations.Count < 2 || nav.LastStats.TilesBaked != 0)
        {
            Console.Error.WriteLine("Forward mesh segment has no complete disk-only path.");
            return 2;
        }

        float pathLength = 0;
        for (int i = 1; i < route.locations.Count; i++)
            pathLength += Vector2.Distance(new(route.locations[i - 1].X, route.locations[i - 1].Y),
                new(route.locations[i].X, route.locations[i].Y));
        Vector3 routeStart = route.locations[0];
        Vector3 routeEnd = route.locations[^1];
        float startError = Vector2.Distance(new(routeStart.X, routeStart.Y), origin);
        float endError = Vector2.Distance(new(routeEnd.X, routeEnd.Y),
            new(ahead.X, ahead.Y));
        if (pathLength > 3.6f || startError > 0.5f || endError > 0.5f)
        {
            Console.Error.WriteLine("Forward path bends or misses the nearby target.");
            return 2;
        }

        Console.WriteLine($"Preflight passed in {area.AreaName}: start=({start.X:F2}, {start.Y:F2}, {start.Z:F2}), " +
            $"facing={map.Direction:F2}, path={pathLength:F2} yd, " +
            $"start/end error={startError:F2}/{endError:F2} yd, " +
            $"new bakes={nav.LastStats.TilesBaked}.");
        if (!move)
        {
            Console.WriteLine("No game input sent. Use --move for one 200 ms forward press.");
            return 0;
        }

        await Task.Delay(300, cts.Token);
        screen.Update();
        tracker.Update();
        if (!tracker.TryGetLatest(out VisualBarSample steady) ||
            steady.WorldPosition is not { } steadyWorld ||
            steady.Position is not { } steadyMap ||
            steadyMap.UIMapId != map.UIMapId ||
            steadyWorld.InstanceMapId != world.InstanceMapId ||
            steady.PlayerHealth < 0.95 || steady.TargetPresent != false ||
            Math.Abs(Math.Atan2(Math.Sin(steadyMap.Direction - map.Direction),
                Math.Cos(steadyMap.Direction - map.Direction))) > 0.1 ||
            Vector2.Distance(origin, new((float)steadyWorld.X, (float)steadyWorld.Y)) > 0.3f)
        {
            Console.Error.WriteLine("Player moved or combat state changed before the press; no input sent.");
            return 2;
        }

        InputWindowsNative input = new(process, cts, 0);
        try
        {
            input.PressFixed((int)ConsoleKey.UpArrow, 200, cts.Token);
        }
        finally
        {
            input.KeyUp((int)ConsoleKey.UpArrow);
        }

        await Task.Delay(300, cts.Token);
        for (int i = 0; i < 25 && !cts.IsCancellationRequested; i++)
        {
            screen.Update();
            tracker.Update();
            if (tracker.TryGetLatest(out VisualBarSample after) &&
                after.WorldPosition is { } next &&
                after.Position is { } nextMap &&
                nextMap.UIMapId == map.UIMapId &&
                next.InstanceMapId == world.InstanceMapId)
            {
                Vector2 delta = new((float)(next.X - steadyWorld.X),
                    (float)(next.Y - steadyWorld.Y));
                Vector2 forward = new(MathF.Cos((float)steadyMap.Direction),
                    MathF.Sin((float)steadyMap.Direction));
                float along = Vector2.Dot(delta, forward);
                float lateral = MathF.Abs(delta.X * forward.Y - delta.Y * forward.X);
                Console.WriteLine($"After press: displacement={delta.Length():F2} yd, " +
                    $"forward={along:F2} yd, lateral={lateral:F2} yd.");
                return along is > 0.2f and < 4f && lateral < 2f ? 0 : 3;
            }
            await Task.Delay(100, cts.Token);
        }

        Console.Error.WriteLine("No fresh position after the movement press.");
        return 3;
    }

    Console.Error.WriteLine($"No consistent Forever position captured: " +
        $"fresh={freshSamples}, positionPairs={positionPairs}, " +
        $"last map error={lastMapError:F2} yd.");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Movement probe cancelled.");
    return 3;
}
finally
{
    cts.Cancel();
    await Task.Delay(200);
}
