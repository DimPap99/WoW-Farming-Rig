using Core;
using Core.Database;

using Game;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PPather.Navmesh;

using SharedLib;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

// Read-only capture diagnostic for the Forever visual status-bar prototype.
// It deliberately does not construct BotController or send any game input.
int pidOption = Array.IndexOf(args, "--pid");
int? requestedPid = null;
if (pidOption >= 0)
{
    if (pidOption + 1 >= args.Length ||
        !int.TryParse(args[pidOption + 1], out int parsedPid) || parsedPid <= 0)
    {
        Console.Error.WriteLine("Use --pid with a positive WowB process ID.");
        return 1;
    }
    requestedPid = parsedPid;
    args = args.Where((_, index) => index != pidOption && index != pidOption + 1).ToArray();
}

if (args.Length == 1 && args[0] == "--self-test")
    return RunSelfTest();

bool readerSmoke = args.Length == 1 && args[0] == "--reader-smoke";
bool routeProbe = args.Length == 1 && args[0] == "--route-probe";

if (args.Length == 2 && args[0] == "--read")
{
    using Image<Bgra32> saved = Image.Load<Bgra32>(args[1]);
    Bgra32[] savedPixels = new Bgra32[saved.Width * saved.Height];
    saved.CopyPixelDataTo(savedPixels);
    PrintBars(savedPixels, saved.Width, saved.Height);
    return 0;
}

Process? wow = Process.GetProcessesByName("WowB")
    .OrderByDescending(p => p.StartTime)
    .FirstOrDefault(p => requestedPid is null || p.Id == requestedPid);

if (wow is null)
{
    Console.Error.WriteLine(requestedPid is { } pid
        ? $"WowB process {pid} is not running."
        : "WowB (WoW: Forever beta) is not running.");
    return 1;
}

string output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), $"forever-visual-{DateTime.Now:yyyyMMdd-HHmmss}.png");
bool monitor = args.Length > 0 && args[0] == "--monitor";
int monitorSeconds = monitor && args.Length > 1 && int.TryParse(args[1], out int requestedSeconds)
    ? Math.Clamp(requestedSeconds, 5, 60)
    : 20;

using CancellationTokenSource cts = new();
try
{
    WowProcess process = new(cts, Options.Create(new StartupConfigPid { Id = wow.Id }));
    using WowScreenWGC screen = new(NullLogger<WowScreenWGC>.Instance, process, []);
    ForeverVisualBarTracker tracker = new(screen, new StartupClientVersion(process.FileVersion));

    if (readerSmoke)
        return await RunReaderSmoke(screen, tracker);

    if (routeProbe)
        return await RunRouteProbe(screen, tracker);

    if (monitor)
    {
        Console.WriteLine($"Monitoring Forever visual bars for {monitorSeconds} seconds; no game input is sent.");
        VisualBarSample? previous = null;
        bool reportedMissing = false;
        for (int i = 0; i < monitorSeconds * 10; i++)
        {
            screen.Update();
            tracker.Update();
            if (i % 2 == 0)
            {
                if (tracker.TryGetLatest(out VisualBarSample current))
                {
                    if (previous is null || HasChanged(previous.Value, current))
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss}: " +
                            $"health={current.PlayerHealth:P0}, power={current.PlayerPower:P0}, " +
                            $"healthMax={current.PlayerHealthMax?.ToString() ?? "unavailable"}, " +
                            $"powerMax={current.PlayerPowerMax?.ToString() ?? "unavailable"}, " +
                            $"target={current.TargetHealth:P0}, {TargetText(current.TargetPresent)}, " +
                            $"calibration={current.Calibration:P0}, " +
                            PositionText(current.Position) + ", " +
                            WorldPositionText(current.WorldPosition) + ", " +
                            CooldownText(current.ActionCooldownFill) + ", " +
                            ActionText(current.WatchedAction) + ", " +
                            ActionBarText(current.ActionBar) + ", " +
                            SpellBookText(current.SpellBook) + ", " +
                            BindingsText(current.Bindings));
                        previous = current;
                    }
                }
                else if (!reportedMissing)
                {
                    Console.WriteLine("Visual bars not found yet.");
                    reportedMissing = true;
                }
            }
            await Task.Delay(100);
        }
        return previous is null ? 2 : 0;
    }

    // WGC delivers frames asynchronously. Give it time to receive a frame before
    // saving the client-area image.
    for (int i = 0; i < 40; i++)
    {
        screen.Update();
        await Task.Delay(100);
    }

    // WowScreenWGC keeps a JPEG-only ImageSharp configuration. Copy its pixels
    // into a default-config image so this diagnostic can save lossless PNG.
    using Image<Bgra32> image = new(screen.ScreenImage.Width, screen.ScreenImage.Height);
    Bgra32[] pixels = new Bgra32[image.Width * image.Height];
    screen.ScreenImage.CopyPixelDataTo(pixels);
    image.ProcessPixelRows(destination =>
    {
        for (int y = 0; y < image.Height; y++)
            pixels.AsSpan(y * image.Width, image.Width).CopyTo(destination.GetRowSpan(y));
    });
    image.SaveAsPng(output);
    string cropOutput = Path.Combine(
        Path.GetDirectoryName(output)!,
        Path.GetFileNameWithoutExtension(output) + "-bars.png");
    using Image<Bgra32> crop = image.Clone(context => context.Crop(
        new Rectangle(0, 0, Math.Min(650, image.Width), Math.Min(550, image.Height))));
    crop.SaveAsPng(cropOutput);
    Console.WriteLine($"Saved {image.Width}x{image.Height} capture to {output}");
    Console.WriteLine($"Saved upper-left bar region to {cropOutput}");
    PrintBars(pixels, image.Width, image.Height);
    return 0;
}
catch (System.Management.ManagementException ex)
{
    Console.Error.WriteLine("Cannot inspect the WoW process from this Windows session: " + ex.Message);
    Console.Error.WriteLine("The game is unaffected. Run this read-only tool with process-inspection access.");
    return 3;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine("Cannot start WoW screen capture: " + ex.Message);
    Console.Error.WriteLine("If the game is still launching, wait until its window is visible and retry.");
    return 4;
}
finally
{
    cts.Cancel();
    // WowProcess owns a polling thread; let it observe cancellation before CTS disposal.
    await Task.Delay(200);
}

static void PrintBars(Bgra32[] pixels, int width, int height)
{
    if (!VisualBarReader.TryRead(pixels, width, height, out VisualBarSample bars))
    {
        Console.WriteLine("Visual bars not found or calibration failed.");
        return;
    }

    Console.WriteLine($"Visual bars at x={bars.Left}, y={bars.Bottom}, width={bars.Width}");
    Console.WriteLine($"Calibration={bars.Calibration:P1}, player health={bars.PlayerHealth:P1}, " +
        $"player power={bars.PlayerPower:P1}, target health={bars.TargetHealth:P1}");
    Console.WriteLine($"Player health max={bars.PlayerHealthMax?.ToString() ?? "unavailable"}, " +
        $"power max={bars.PlayerPowerMax?.ToString() ?? "unavailable"}");
    Console.WriteLine(TargetText(bars.TargetPresent));
    Console.WriteLine(PositionText(bars.Position));
    Console.WriteLine(WorldPositionText(bars.WorldPosition));
    Console.WriteLine(CooldownText(bars.ActionCooldownFill));
    Console.WriteLine(ActionText(bars.WatchedAction));
    Console.WriteLine(ActionBarText(bars.ActionBar));
    Console.WriteLine(SpellBookText(bars.SpellBook));
    Console.WriteLine(BindingsText(bars.Bindings));
}

static string PositionText(VisualPositionSample? position) => position is { } value
    ? $"map={value.UIMapId}, x={value.MapX:F5}, y={value.MapY:F5}, facing={value.Direction:F2}"
    : "position=unavailable";

static string WorldPositionText(VisualWorldPositionSample? position) => position is { } value
    ? $"world=({value.X:F2}, {value.Y:F2}, {value.Z:F2}) instance={value.InstanceMapId}"
    : "world position=unavailable";

static string CooldownText(double? fill) => fill is { } value
    ? $"action cooldown fill={value:P0}"
    : "action cooldown=unavailable";

static string ActionText(VisualActionSample? action) => action is { } value
    ? $"watched slot {value.Slot}: {value.Kind} ID {value.Id}"
    : "watched action=unavailable";

static string ActionBarText(VisualActionBarSnapshot? bar)
{
    if (bar is null)
        return "action bar=unavailable";

    List<string> occupied = [];
    for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
    {
        if (bar.GetSlot(slot) is { Kind: not VisualActionKind.Empty } action)
            occupied.Add($"{slot}:{action.Kind}:{action.Id}");
    }

    return $"action bar {bar.ReadableCount}/120 readable, " +
        $"{bar.OccupiedCount} occupied [{string.Join(", ", occupied)}]";
}

static string SpellBookText(VisualSpellBookSnapshot? book)
{
    if (book is null)
        return "spellbook=unavailable";

    List<string> entries = [];
    for (int slot = 1; slot <= book.Count; slot++)
        if (book.GetSlot(slot) is { } entry)
            entries.Add($"{slot}:{entry.Source}:{entry.Id}");

    return $"spellbook {book.ReadableCount}/{book.Count} readable " +
        $"[{string.Join(", ", entries)}]";
}

static string BindingsText(VisualBindingSnapshot? snapshot)
{
    if (snapshot is null)
        return "bindings=unavailable";

    List<string> bound = [];
    for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
        if (snapshot.GetCode(index) is > 0 and int code &&
            KeyReader.DecodeBinding(code) is { } binding)
        {
            string primary = binding.key1 == ConsoleKey.NoName
                ? "" : $"{binding.mod1.ToPrefix()}{binding.key1}";
            string secondary = binding.key2 == ConsoleKey.NoName
                ? "" : $"/{binding.mod2.ToPrefix()}{binding.key2}";
            bound.Add($"{index}:{binding.bindingId}:{primary}{secondary}");
        }

    return $"bindings {snapshot.ReadableCount}/{VisualBindingSnapshot.Count} readable, " +
        $"{bound.Count} bound [{string.Join(", ", bound)}]";
}

static string TargetText(bool? present) => present switch
{
    true => "target presence=present",
    false => "target presence=absent",
    null => "target presence=unknown"
};

static async Task<int> RunReaderSmoke(WowScreenWGC screen, ForeverVisualBarTracker tracker)
{
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    WorldMapAreaDB maps = new(config);
    SpellBookReader spellBook = new(new SpellDB(config));
    KeyBindingsReader bindingReader = new(NullLogger<KeyBindingsReader>.Instance);
    KeyReader.RequireGameBindings = true;
    PlayerReader player = new(screen, maps, null!, new AddonBits(),
        new SpellInRange(), new Stance(), tracker);

    for (int i = 0; i < 50; i++)
    {
        screen.Update();
        tracker.Update();
        if (player.TryGetForeverVisualVitals(out VisualBarSample bars) &&
            bars.Position is { } mapPosition &&
            bars.WorldPosition is { } worldPosition)
        {
            player.Update(screen);
            Vector3 readerWorld = player.WorldPos;
            bool positionMatches = player.UIMapId.Value == mapPosition.UIMapId &&
                Math.Abs(player.MapX - mapPosition.MapX * 100) < 0.01 &&
                Math.Abs(player.MapY - mapPosition.MapY * 100) < 0.01 &&
                Math.Abs(player.Direction - mapPosition.Direction) < 0.0001 &&
                Vector2.Distance(new(readerWorld.X, readerWorld.Y),
                    new((float)worldPosition.X, (float)worldPosition.Y)) < 3;
            int health = player.HealthPercent();
            int power = player.PTPercentage();
            int? target = bars.TargetPresent == true
                ? player.TargetHealthPercent() : null;
            bool actionMatches = bars.WatchedAction is not { } watched ||
                (player.TryGetForeverWatchedAction(out VisualActionSample action) &&
                action == watched);
            bool actionBarMatches = bars.ActionBar is not { } liveBar ||
                (player.TryGetForeverActionBar(out VisualActionBarSnapshot actionBar) &&
                ReferenceEquals(actionBar, liveBar));
            bool maximaMatch =
                (bars.PlayerHealthMax is not { } healthMax || player.HealthMax() == healthMax) &&
                (bars.PlayerPowerMax is not { } powerMax || player.PTMax() == powerMax);
            bool worldPositionMatches = bars.WorldPosition is not { } world ||
                (player.TryGetForeverWorldPosition(out VisualWorldPositionSample current) &&
                 current == world);
            bool worldPositionAvailable = bars.WorldPosition is not null;
            VisualSpellBookSnapshot? visualBook = bars.SpellBook;
            spellBook.UpdateForever(visualBook);
            bool spellBookMatches = visualBook is { Count: > 0 } book &&
                book.ReadableCount == book.Count &&
                spellBook.IsInitialized && spellBook.AllRanksReceived &&
                spellBook.ExpectedCount == book.Count &&
                spellBook.ReceivedCount == book.Count;
            if (spellBookMatches)
            {
                HashSet<int> spellIds = [];
                for (int slot = 1; slot <= visualBook!.Count; slot++)
                {
                    VisualSpellBookEntry entry = visualBook.GetSlot(slot)!.Value;
                    if (entry.Source == VisualSpellIdSource.SpellId)
                    {
                        spellIds.Add(entry.Id);
                        if (!spellBook.HasExact(entry.Id))
                            spellBookMatches = false;
                    }
                }
                spellBookMatches &= spellBook.Count == spellIds.Count;
                for (int slot = 1; slot <= visualBook.Count; slot++)
                {
                    VisualSpellBookEntry entry = visualBook.GetSlot(slot)!.Value;
                    if (entry.Source == VisualSpellIdSource.ActionId &&
                        !spellIds.Contains(entry.Id) && spellBook.HasExact(entry.Id))
                        spellBookMatches = false;
                }
            }
            VisualBindingSnapshot? visualBindings = bars.Bindings;
            bindingReader.UpdateForever(visualBindings);
            int expectedBindings = 0;
            int expectedSecondaryBindings = 0;
            if (visualBindings is not null)
                for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
                    if (visualBindings.GetCode(index) is > 0 and int code &&
                        KeyReader.DecodeBinding(code) is { } decoded)
                    {
                        if (decoded.key1 != ConsoleKey.NoName)
                            expectedBindings++;
                        if (decoded.key2 != ConsoleKey.NoName)
                            expectedSecondaryBindings++;
                    }
            bool bindingMatches = visualBindings is
                    { ReadableCount: VisualBindingSnapshot.Count } &&
                bindingReader.IsInitialized &&
                bindingReader.ExpectedCount == VisualBindingSnapshot.Count &&
                bindingReader.ReceivedCount == VisualBindingSnapshot.Count &&
                bindingReader.Count == expectedBindings &&
                bindingReader.SecondaryBindings.Count == expectedSecondaryBindings;
            if (bindingMatches && visualBindings!.GetCode(21) is > 0)
            {
                KeyAction firstAction = new() { Slot = 1 };
                bindingMatches &= KeyReader.ResolveFromSlot(firstAction) &&
                    firstAction.ConsoleKey == ConsoleKey.D1;
            }
            if (bindingMatches && visualBindings!.GetCode(33) == 0)
                bindingMatches &= !KeyReader.ResolveFromSlot(new KeyAction { Slot = 49 });

            bool absoluteRejected = false;
            try { _ = player.HealthCurrent(); }
            catch (NotSupportedException) { absoluteRejected = true; }

            await Task.Delay(900);
            bool staleRejected = false;
            try { _ = player.HealthPercent(); }
            catch (InvalidOperationException) { staleRejected = true; }
            bool staleActionRejected = !player.TryGetForeverWatchedAction(out _);
            bool staleActionBarRejected = !player.TryGetForeverActionBar(out _);
            bool staleWorldPositionRejected = !player.TryGetForeverWorldPosition(out _);
            bool staleSpellBookRejected = !player.TryGetForeverSpellBook(out _);
            bool staleBindingsRejected = !player.TryGetForeverBindings(out _);
            spellBook.UpdateForever(null);
            bindingReader.UpdateForever(null);
            bool staleSpellBookReaderReset = !spellBook.IsInitialized &&
                !spellBook.AllRanksReceived && spellBook.Count == 0;
            bool staleBindingReaderReset = !bindingReader.IsInitialized &&
                bindingReader.Count == 0 &&
                bindingReader.SecondaryBindings.Count == 0;
            staleBindingReaderReset &=
                !KeyReader.ResolveFromSlot(new KeyAction { Slot = 1 });
            bool stalePositionRejected = false;
            try { _ = player.WorldPos; }
            catch (InvalidOperationException) { stalePositionRejected = true; }
            player.Update(screen);
            bool staleMapIdCleared = player.UIMapId.Value == 0;
            bool staleMaximumRejected = false;
            try { _ = player.HealthMax(); }
            catch (InvalidOperationException) { staleMaximumRejected = true; }

            Console.WriteLine($"PlayerReader: health={health}%, power={power}%, " +
                $"target={(target is { } value ? value + "%" : "unavailable")}, " +
                $"actionMatches={actionMatches}, actionBarMatches={actionBarMatches}, " +
                $"healthMax={bars.PlayerHealthMax?.ToString() ?? "unavailable"}, " +
                $"powerMax={bars.PlayerPowerMax?.ToString() ?? "unavailable"}, " +
                $"maximaMatch={maximaMatch}, staleMaximumRejected={staleMaximumRejected}, " +
                $"worldPositionAvailable={worldPositionAvailable}, " +
                $"worldPositionMatches={worldPositionMatches}, " +
                $"staleWorldPositionRejected={staleWorldPositionRejected}, " +
                $"spellBook={SpellBookText(visualBook)}, " +
                $"spellBookMatches={spellBookMatches}, " +
                $"staleSpellBookRejected={staleSpellBookRejected}, " +
                $"staleSpellBookReaderReset={staleSpellBookReaderReset}, " +
                $"bindings={BindingsText(visualBindings)}, " +
                $"bindingMatches={bindingMatches}, " +
                $"staleBindingsRejected={staleBindingsRejected}, " +
                $"staleBindingReaderReset={staleBindingReaderReset}, " +
                $"positionMatches={positionMatches}, " +
                $"stalePositionRejected={stalePositionRejected}, " +
                $"staleMapIdCleared={staleMapIdCleared}, " +
                $"absoluteRejected={absoluteRejected}, staleRejected={staleRejected}, " +
                $"staleActionRejected={staleActionRejected}, " +
                $"staleActionBarRejected={staleActionBarRejected}");
            return actionMatches && actionBarMatches && maximaMatch &&
                worldPositionMatches && staleWorldPositionRejected &&
                spellBookMatches && staleSpellBookRejected &&
                staleSpellBookReaderReset &&
                bindingMatches && staleBindingsRejected &&
                staleBindingReaderReset &&
                positionMatches && stalePositionRejected && staleMapIdCleared &&
                staleMaximumRejected && absoluteRejected && staleRejected &&
                staleActionRejected && staleActionBarRejected ? 0 : 2;
        }

        await Task.Delay(100);
    }

    Console.Error.WriteLine("No fresh Forever visual sample was captured.");
    return 2;
}

static async Task<int> RunRouteProbe(WowScreenWGC screen, ForeverVisualBarTracker tracker)
{
    // Read-only placement and connectivity check. The client reports Z=0 in
    // the observed old-world zones, so use the baked walkable height instead.
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    WorldMapAreaDB maps = new(config);
    NavmeshBakeOptions bake = new();
    string version = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
    string hash = NavmeshSettings.ComputeSettingsHash("forever", version,
        bake.AgentRadius, bake.AgentMaxClimb, bake.WalkableSlope);

    float lastMapError = 0;
    for (int i = 0; i < 50; i++)
    {
        screen.Update();
        tracker.Update();
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            bars.Position is not { } map || bars.WorldPosition is not { } world)
        {
            await Task.Delay(100);
            continue;
        }

        if (!maps.TryGet(map.UIMapId, out WorldMapArea area) ||
            area.MapID != world.InstanceMapId)
        {
            Console.Error.WriteLine("Live map or instance ID is missing or inconsistent.");
            return 2;
        }

        Vector3 fromMap = WorldMapAreaDB.ToWorld_FlipXY(
            new Vector3((float)(map.MapX * 100), (float)(map.MapY * 100), 0), area);
        Vector2 fromWorld = new((float)world.X, (float)world.Y);
        float mapError = Vector2.Distance(new(fromMap.X, fromMap.Y), fromWorld);
        if (mapError > 3f)
        {
            // The two Lua pixel rows can be captured across an OnUpdate while
            // the player moves. Try the next screen frame before failing.
            lastMapError = mapError;
            await Task.Delay(100);
            continue;
        }

        string cache = Path.Combine(config.Navmesh, area.Continent, hash);
        if (!Directory.Exists(cache))
        {
            Console.Error.WriteLine($"No baked Forever navmesh at {cache}.");
            return 2;
        }

        using NavmeshPathfinder nav = new(NullLogger.Instance, null, cache,
            bake, new NavmeshQueryOptions());
        if (!nav.TryGetHeight((float)world.X, (float)world.Y, out float startZ))
        {
            Console.Error.WriteLine("No walkable mesh covers the live player X/Y.");
            return 2;
        }

        Vector3 start = new((float)world.X, (float)world.Y, startZ);
        NavmeshCoords.GetTileIndex(start.X, start.Y, out int tileX, out int tileZ);
        Console.WriteLine($"Live {area.AreaName} map={map.UIMapId} instance={world.InstanceMapId}; " +
            $"world=({world.X:F2}, {world.Y:F2}, {world.Z:F2}); " +
            $"map/world error={mapError:F2} yd; mesh Z={startZ:F2}; " +
            $"tile={tileX},{tileZ}");

        foreach (float radius in new[] { 12f, 24f })
        {
            for (int step = 0; step < 8; step++)
            {
                float angle = step * MathF.Tau / 8;
                float targetX = start.X + radius * MathF.Cos(angle);
                float targetY = start.Y + radius * MathF.Sin(angle);
                if (!nav.TryGetHeight(targetX, targetY, out float targetZ))
                    continue;

                Vector3 target = new(targetX, targetY, targetZ);
                var route = nav.FindPath(start, target, null);
                if (route is null || route.locations.Count < 2 ||
                    nav.LastStats.TilesBaked != 0)
                    continue;

                Vector3 end = route.locations[^1];
                float endError = Vector2.Distance(new(end.X, end.Y),
                    new(target.X, target.Y));
                if (endError > 5f)
                    continue;

                Console.WriteLine($"Disk-only local route: {route.locations.Count} points, " +
                    $"{radius:F0} yd target, end=({end.X:F2}, {end.Y:F2}, {end.Z:F2}), " +
                    $"end error={endError:F2} yd, " +
                    $"resident tiles={nav.LastStats.ResidentTiles}, " +
                    $"new bakes={nav.LastStats.TilesBaked}");
                return 0;
            }
        }

        Console.Error.WriteLine("No complete short route from the live player position.");
        return 2;
    }

    Console.Error.WriteLine(lastMapError > 0
        ? $"No consistent live map/world position was captured; last error {lastMapError:F2} yd."
        : "No fresh checksummed Forever position was captured.");
    return 2;
}

static bool HasChanged(VisualBarSample previous, VisualBarSample current)
    => Math.Abs(previous.PlayerHealth - current.PlayerHealth) >= 0.02
    || Math.Abs(previous.PlayerPower - current.PlayerPower) >= 0.02
    || Math.Abs(previous.TargetHealth - current.TargetHealth) >= 0.02
    || previous.TargetPresent != current.TargetPresent
    || previous.PlayerHealthMax != current.PlayerHealthMax
    || previous.PlayerPowerMax != current.PlayerPowerMax
    || previous.WorldPosition != current.WorldPosition
    || previous.WatchedAction != current.WatchedAction
    || ActionBarChanged(previous.ActionBar, current.ActionBar)
    || SpellBookChanged(previous.SpellBook, current.SpellBook)
    || BindingsChanged(previous.Bindings, current.Bindings)
    || ((previous.ActionCooldownFill, current.ActionCooldownFill) switch
    {
        (null, null) => false,
        (null, _) or (_, null) => true,
        ({ } a, { } b) => Math.Abs(a - b) >= 0.02
    })
    || ((previous.Position, current.Position) switch
    {
        (null, null) => false,
        (null, _) or (_, null) => true,
        ({ } a, { } b) => a.UIMapId != b.UIMapId ||
            Math.Abs(a.MapX - b.MapX) >= 0.0001 ||
            Math.Abs(a.MapY - b.MapY) >= 0.0001 ||
            Math.Abs(a.Direction - b.Direction) >= 0.05
    });

static bool ActionBarChanged(VisualActionBarSnapshot? previous,
    VisualActionBarSnapshot? current)
{
    if (previous is null || current is null)
        return previous != current;

    if (previous.ReadableCount != current.ReadableCount ||
        previous.OccupiedCount != current.OccupiedCount)
        return true;

    for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
        if (previous.GetSlot(slot) != current.GetSlot(slot))
            return true;

    return false;
}

static bool SpellBookChanged(VisualSpellBookSnapshot? previous,
    VisualSpellBookSnapshot? current)
{
    if (previous is null || current is null)
        return previous != current;

    if (previous.Count != current.Count ||
        previous.ReadableCount != current.ReadableCount)
        return true;

    for (int slot = 1; slot <= current.Count; slot++)
        if (previous.GetSlot(slot) != current.GetSlot(slot))
            return true;

    return false;
}

static bool BindingsChanged(VisualBindingSnapshot? previous,
    VisualBindingSnapshot? current)
{
    if (previous is null || current is null)
        return previous != current;
    if (previous.ReadableCount != current.ReadableCount)
        return true;
    for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
        if (previous.GetCode(index) != current.GetCode(index))
            return true;
    return false;
}

static int RunSelfTest()
{
    KeyReader.RequireGameBindings = true;
    SpellBookReader spellBookReader = new(new SpellDB(new DataConfig
        { Root = Path.GetFullPath("Json"), Exp = "forever" }));
    KeyBindingsReader bindingReader = new(NullLogger<KeyBindingsReader>.Instance);
    foreach (double scale in new[] { 0.75, 1.0, 1.25 })
    {
        const int width = 700;
        const int height = 550;
        const int left = 16;
        const int top = 70;
        Bgra32[] pixels = new Bgra32[width * height];
        int barWidth = (int)Math.Round(320 * scale);
        int rowStep = (int)Math.Round(17 * scale);
        int barHeight = (int)Math.Round(10 * scale);

        Draw(0, 0.75, new Bgra32 { G = 255, A = 255 });
        Draw(1, 0.25, new Bgra32 { B = 255, A = 255 });
        Draw(2, 0.60, new Bgra32 { R = 255, A = 255 });
        Draw(3, 0.50, new Bgra32 { R = 255, G = 255, A = 255 });
        Draw(4, 1.00, new Bgra32 { R = 255, G = 255, B = 255, A = 255 });
        Draw(7, 0.40, new Bgra32 { G = 255, B = 255, A = 255 });
        DrawPositionPixel(0, 42);
        DrawPositionPixel(1, (int)Math.Floor(0.345678 * 16777215));
        DrawPositionPixel(2, (int)Math.Floor(0.789012 * 16777215));
        DrawPositionPixel(3, (int)Math.Floor(1.23456 * 100000));
        DrawPositionPixel(4, (0xA55AA5 - 42 -
            (int)Math.Floor(0.345678 * 16777215) -
            (int)Math.Floor(0.789012 * 16777215) -
            (int)Math.Floor(1.23456 * 100000)) & 0xFFFFFF);
        DrawPositionPixel(6, 0x00FFFF);
        DrawPositionPixel(7, 0x00FF00);
        DrawPositionPixel(8, 7744);
        DrawPositionPixel(9, 0xA50401);
        DrawPositionPixel(10, 0x5AA55A);
        DrawPositionPixel(11, 742);
        DrawPositionPixel(12, (0xA55A11 - 742) & 0xFFFFFF);
        DrawPositionPixel(13, 1000);
        DrawPositionPixel(14, (0xA55A22 - 1000) & 0xFFFFFF);
        DrawWorldPixel(0, 8388608 - 61834);
        DrawWorldPixel(1, 8388608 + 123456);
        DrawWorldPixel(2, 8388608 + 8765);
        DrawWorldPixel(3, 0);
        DrawWorldPixel(4, (0xA55A33 - (8388608 - 61834) -
            (8388608 + 123456) - (8388608 + 8765)) & 0xFFFFFF);
        DrawWorldPixel(5, 3);
        DrawWorldPixel(6, (0xA56622 - 3) & 0xFFFFFF);
        DrawWorldPixel(7, VisualBindingSnapshot.Count);
        DrawWorldPixel(8, (0xA57722 - VisualBindingSnapshot.Count) & 0xFFFFFF);
        DrawSpellGridSlot(1, VisualSpellIdSource.SpellId, 6603);
        DrawSpellGridSlot(2, VisualSpellIdSource.ActionId, 270);
        DrawSpellGridSlot(3, VisualSpellIdSource.SpellId, 7744);
        for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
            DrawBindingSlot(index, 0);
        DrawBindingSlot(21, (21 << 14) | (28 << 7));
        DrawBindingSlot(45, (45 << 14) | 52);
        for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
            DrawActionGridSlot(slot, 0, 0);
        DrawActionGridSlot(4, 1, 7744);
        DrawActionGridSlot(26, 4, 270);

        if (!VisualBarReader.TryRead(pixels, width, height, out VisualBarSample result)
            || Math.Abs(result.PlayerHealth - 0.75) > 0.02
            || Math.Abs(result.PlayerPower - 0.25) > 0.02
            || Math.Abs(result.TargetHealth - 0.60) > 0.02
            || Math.Abs(result.Calibration - 0.50) > 0.02
            || result.Position is not { } position
            || position.UIMapId != 42
            || Math.Abs(position.MapX - 0.345678) > 0.0001
            || Math.Abs(position.MapY - 0.789012) > 0.0001
            || Math.Abs(position.Direction - 1.23456) > 0.0001
            || result.WorldPosition is not { } worldPosition
            || Math.Abs(worldPosition.X + 618.34) > 0.001
            || Math.Abs(worldPosition.Y - 1234.56) > 0.001
            || Math.Abs(worldPosition.Z - 87.65) > 0.001
            || worldPosition.InstanceMapId != 0
            || result.TargetPresent != true
            || result.PlayerHealthMax != 742
            || result.PlayerPowerMax != 1000
            || result.WatchedAction is not { Slot: 4, Kind: VisualActionKind.Spell, Id: 7744 }
            || result.ActionBar is not { ReadableCount: 120, OccupiedCount: 2 } actionBar
            || actionBar.GetSlot(4) is not { Kind: VisualActionKind.Spell, Id: 7744 }
            || actionBar.GetSlot(26) is not { Kind: VisualActionKind.Flyout, Id: 270 }
            || result.SpellBook is not { Count: 3, ReadableCount: 3 } spellBook
            || spellBook.GetSlot(1) is not { Source: VisualSpellIdSource.SpellId, Id: 6603 }
            || spellBook.GetSlot(2) is not { Source: VisualSpellIdSource.ActionId, Id: 270 }
            || spellBook.GetSlot(3) is not { Source: VisualSpellIdSource.SpellId, Id: 7744 })
        {
            Console.Error.WriteLine($"Visual reader self-test failed at UI scale {scale}.");
            return 1;
        }

        if (result.Bindings is not
                { ReadableCount: VisualBindingSnapshot.Count } visualBindings ||
            visualBindings.GetCode(21) != ((21 << 14) | (28 << 7)) ||
            visualBindings.GetCode(22) != 0 ||
            visualBindings.GetCode(45) != ((45 << 14) | 52))
        {
            Console.Error.WriteLine($"Visual binding grid failed at UI scale {scale}.");
            return 1;
        }
        bindingReader.UpdateForever(visualBindings);
        if (!bindingReader.IsInitialized || bindingReader.Count != 1 ||
            bindingReader.SecondaryBindings.Count != 1 ||
            !bindingReader.TryGetBinding(BindingID.ACTIONBUTTON1,
                out ConsoleKey firstKey, out ModifierKey firstModifier) ||
            firstKey != ConsoleKey.D1 || firstModifier != ModifierKey.None ||
            !bindingReader.TryGetSecondaryBinding(BindingID.MULTIACTIONBAR1BUTTON1,
                out ConsoleKey secondKey, out ModifierKey secondModifier) ||
            secondKey != ConsoleKey.F1 || secondModifier != ModifierKey.None)
        {
            Console.Error.WriteLine($"Forever binding adapter failed at UI scale {scale}.");
            return 1;
        }
        KeyAction boundAction = new() { Slot = 1 };
        KeyAction unboundAction = new() { Slot = 2 };
        KeyAction secondaryAction = new() { Slot = 61 };
        KeyAction secondaryById = new()
            { BindingID = BindingID.MULTIACTIONBAR1BUTTON1 };
        if (!KeyReader.ResolveFromSlot(boundAction) ||
            boundAction.ConsoleKey != ConsoleKey.D1 ||
            KeyReader.ResolveFromSlot(unboundAction) ||
            !KeyReader.ResolveFromSlot(secondaryAction) ||
            secondaryAction.ConsoleKey != ConsoleKey.F1 ||
            !KeyReader.ReadKey(NullLogger.Instance, secondaryById) ||
            secondaryById.ConsoleKey != ConsoleKey.F1 ||
            KeyReader.ReadKey(NullLogger.Instance,
                new KeyAction { BindingID = BindingID.ACTIONBUTTON2 }))
        {
            Console.Error.WriteLine($"Forever key resolution used a default or missed a secondary binding at UI scale {scale}.");
            return 1;
        }

        spellBookReader.UpdateForever(result.SpellBook);
        if (!spellBookReader.IsInitialized ||
            !spellBookReader.AllRanksReceived ||
            spellBookReader.ExpectedCount != 3 ||
            spellBookReader.Count != 2 ||
            !spellBookReader.HasExact(6603) ||
            !spellBookReader.HasExact(7744) ||
            spellBookReader.HasExact(270))
        {
            Console.Error.WriteLine($"Forever spellbook adapter failed at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(12, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.PlayerHealthMax is not null || result.PlayerPowerMax != 1000)
        {
            Console.Error.WriteLine($"Visual reader accepted missing health-max marker at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(12, (0xA55A11 - 742) & 0xFFFFFF);
        DrawPositionPixel(14, 0xA55A11);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.PlayerPowerMax is not null || result.PlayerHealthMax != 742)
        {
            Console.Error.WriteLine($"Visual reader accepted mismatched power-max marker at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(14, (0xA55A22 - 1000) & 0xFFFFFF);

        // A red-channel shift from a transient screen tint must not turn a
        // small maximum into a plausible but wildly larger value.
        DrawPositionPixel(11, 742 + (4 << 16));
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.PlayerHealthMax is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted a corrupted maximum at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(11, 742);
        DrawPositionPixel(11, 742 + (4 << 16));
        DrawPositionPixel(12, ((0xA55A11 - 742) & 0xFFFFFF) + (4 << 16));
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.PlayerHealthMax is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted a uniformly tinted maximum at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(11, 742);
        DrawPositionPixel(12, (0xA55A11 - 742) & 0xFFFFFF);

        DrawWorldPixel(6, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.SpellBook is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing spellbook marker at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(6, (0xA56622 - 3) & 0xFFFFFF);
        DrawEncodedRect(left + (int)Math.Round(16 * scale),
            top + (int)Math.Round(205 * scale), (int)Math.Round(8 * scale),
            270 + (4 << 16));
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.SpellBook is not { Count: 3, ReadableCount: 2 })
        {
            Console.Error.WriteLine($"Visual reader accepted a corrupted spellbook ID at UI scale {scale}.");
            return 1;
        }
        spellBookReader.UpdateForever(result.SpellBook);
        if (spellBookReader.IsInitialized ||
            spellBookReader.AllRanksReceived || spellBookReader.Count != 0)
        {
            Console.Error.WriteLine($"Forever spellbook adapter kept a partial snapshot at UI scale {scale}.");
            return 1;
        }
        DrawSpellGridSlot(2, VisualSpellIdSource.ActionId, 270);

        DrawWorldPixel(8, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.Bindings is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing binding marker at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(8, (0xA57722 - VisualBindingSnapshot.Count) & 0xFFFFFF);
        DrawEncodedRect(left + (int)Math.Round(16 * scale),
            top + (int)Math.Round(300 * scale), (int)Math.Round(8 * scale),
            4 << 16);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.Bindings is not { ReadableCount: 61 })
        {
            Console.Error.WriteLine($"Visual reader accepted corrupt binding code at UI scale {scale}.");
            return 1;
        }
        bindingReader.UpdateForever(result.Bindings);
        if (bindingReader.IsInitialized || bindingReader.Count != 0)
        {
            Console.Error.WriteLine($"Forever binding adapter kept a partial snapshot at UI scale {scale}.");
            return 1;
        }
        if (KeyReader.ResolveFromSlot(new KeyAction { Slot = 1 }))
        {
            Console.Error.WriteLine($"Forever key resolution accepted stale bindings at UI scale {scale}.");
            return 1;
        }
        DrawBindingSlot(2, 0);

        DrawWorldPixel(5, 200);
        DrawWorldPixel(6, (0xA56622 - 200) & 0xFFFFFF);
        DrawSpellGridSlot(200, VisualSpellIdSource.SpellId, 12345);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.SpellBook is not { Count: 200, ReadableCount: 4 } longBook ||
            longBook.GetSlot(200) is not
                { Source: VisualSpellIdSource.SpellId, Id: 12345 })
        {
            Console.Error.WriteLine($"Visual reader missed the final spellbook row at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(5, 3);
        DrawWorldPixel(6, (0xA56622 - 3) & 0xFFFFFF);

        DrawWorldPixel(4, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.WorldPosition is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing world-position marker at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(4, (0xA55A33 - (8388608 - 61834) -
            (8388608 + 123456) - (8388608 + 8765)) & 0xFFFFFF);
        DrawWorldPixel(0, (8388608 - 61834) + (4 << 16));
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.WorldPosition is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted a corrupted world coordinate at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(0, 8388608 - 61834);

        DrawPositionPixel(1, (int)Math.Floor(0.345678 * 16777215) + (4 << 16));
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.Position is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted a corrupted map coordinate at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(1, (int)Math.Floor(0.345678 * 16777215));

        DrawPositionPixel(7, 0xFF0000);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.TargetPresent != false)
        {
            Console.Error.WriteLine($"Visual reader no-target test failed at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(7, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.TargetPresent is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing target marker at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(9, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.WatchedAction is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing action marker at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(8, 0);
        DrawPositionPixel(9, 0xA50400);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.WatchedAction is not { Slot: 4, Kind: VisualActionKind.Empty, Id: 0 })
        {
            Console.Error.WriteLine($"Visual reader empty-action test failed at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(8, 7744);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.WatchedAction is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted mismatched action pixels at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(10, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.ActionBar is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing action-grid marker at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(10, 0x5AA55A);
        DrawActionGridSlot(4, -1, 7744);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.ActionBar is not { ReadableCount: 119, OccupiedCount: 1 } partialBar ||
            partialBar.GetSlot(4) is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted unreadable action slot at UI scale {scale}.");
            return 1;
        }
        DrawActionGridSlot(4, 1, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.ActionBar?.GetSlot(4) is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted mismatched action slot at UI scale {scale}.");
            return 1;
        }

        if (result.ActionCooldownFill is not { } cooldown ||
            Math.Abs(cooldown - 0.40) > 0.02)
        {
            Console.Error.WriteLine($"Visual reader cooldown test failed at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(6, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.ActionCooldownFill is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing cooldown marker at UI scale {scale}.");
            return 1;
        }

        DrawPositionPixel(4, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.Position is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing position marker at UI scale {scale}.");
            return 1;
        }

        // A calibration mismatch or absent bars must never produce a sample.
        Draw(3, 0.65, new Bgra32 { R = 255, G = 255, A = 255 });
        if (VisualBarReader.TryRead(pixels, width, height, out _))
        {
            Console.Error.WriteLine($"Visual reader accepted bad calibration at UI scale {scale}.");
            return 1;
        }

        Array.Clear(pixels);
        Draw(4, 1.00, new Bgra32 { R = 255, G = 255, B = 255, A = 255 });
        if (VisualBarReader.TryRead(pixels, width, height, out _))
        {
            Console.Error.WriteLine($"Visual reader accepted missing bars at UI scale {scale}.");
            return 1;
        }

        void Draw(int row, double fraction, Bgra32 color)
        {
            int fill = (int)Math.Round(barWidth * fraction);
            int yStart = top + row * rowStep;
            for (int y = yStart; y < yStart + barHeight; y++)
                for (int x = left; x < left + fill; x++)
                    pixels[y * width + x] = color;
        }

        void DrawPositionPixel(int index, int encoded)
        {
            int xStart = left + (int)Math.Round(index * 20 * scale);
            int yStart = top + (int)Math.Round(90 * scale);
            int side = (int)Math.Round(10 * scale);
            DrawEncodedRect(xStart, yStart, side, encoded);
        }

        void DrawWorldPixel(int index, int encoded)
        {
            int xStart = left + (int)Math.Round(index * 20 * scale);
            int yStart = top + (int)Math.Round(105 * scale);
            int side = (int)Math.Round(10 * scale);
            DrawEncodedRect(xStart, yStart, side, encoded);
        }

        void DrawActionGridSlot(int slot, int kind, int id)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int yStart = top + (int)Math.Round((140 + row * 8) * scale);
            int side = (int)Math.Round(8 * scale);
            DrawEncodedRect(left + (int)Math.Round(column * 16 * scale),
                yStart, side, id);
            DrawEncodedRect(left + (int)Math.Round((column * 16 + 8) * scale),
                yStart, side, kind < 0 ? 0 : 0xA50000 + kind);
        }

        void DrawSpellGridSlot(int slot, VisualSpellIdSource source, int id)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int yStart = top + (int)Math.Round((205 + row * 8) * scale);
            int side = (int)Math.Round(8 * scale);
            int checksum = (0xA56611 - id - slot) & 0xFFFFFF;
            DrawEncodedRect(left + (int)Math.Round(column * 16 * scale),
                yStart, side, id);
            DrawEncodedRect(left + (int)Math.Round((column * 16 + 8) * scale),
                yStart, side, (checksum & 0xFFFFF0) | (int)source);
        }

        void DrawBindingSlot(int index, int code)
        {
            int column = (index - 1) % 20;
            int row = (index - 1) / 20;
            int yStart = top + (int)Math.Round((300 + row * 8) * scale);
            int side = (int)Math.Round(8 * scale);
            DrawEncodedRect(left + (int)Math.Round(column * 16 * scale),
                yStart, side, code);
            DrawEncodedRect(left + (int)Math.Round((column * 16 + 8) * scale),
                yStart, side, (0xA57711 - code - index) & 0xFFFFFF);
        }

        void DrawEncodedRect(int xStart, int yStart, int side, int encoded)
        {
            Bgra32 color = new()
            {
                R = (byte)(encoded >> 16),
                G = (byte)(encoded >> 8),
                B = (byte)encoded,
                A = 255
            };
            for (int y = yStart; y < yStart + side; y++)
                for (int x = xStart; x < xStart + side; x++)
                    pixels[y * width + x] = color;
        }
    }

    KeyReader.RequireGameBindings = false;
    Console.WriteLine("Visual reader self-test passed at 75%, 100%, and 125% UI scale.");
    return 0;
}
