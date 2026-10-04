using Core;
using Core.Database;
using Core.Forever;
using Core.Goals;
using Core.GOAP;

using ForeverVisualReader;

using Game;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

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

// Read-only capture diagnostics plus explicitly bounded trial inputs.
// It never constructs BotController or starts autonomous gameplay.
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
if (args.Length == 1 && args[0] == "--logic-test")
    return LogicTest.Run();

bool readerSmoke = args.Length == 1 && args[0] == "--reader-smoke";
bool targetAudit = args.Length == 1 && args[0] == "--target-audit";
bool targetPositionAudit = args.Length == 1 && args[0] == "--target-position-audit";
bool watchNameplates = args.Length == 1 && args[0] == "--watch-nameplates";
bool trialInteractApproach = args.Length == 1 && args[0] == "--trial-interact-approach";
bool routeProbe = args.Length == 1 && args[0] == "--route-probe";
bool trialCheck = args.Length == 1 && args[0] == "--trial-check";
bool trialMove = args.Length == 1 && args[0] == "--trial-move";
bool trialHeal = args.Length == 1 && args[0] == "--trial-heal";
bool trialJudgement = args.Length == 1 && args[0] == "--trial-judgement";
bool trialJudgementArmed = args.Length == 1 && args[0] == "--trial-judgement-armed";
bool bagsView = args.Length == 1 && args[0] == "--bags";
bool whereView = args.Length is 1 or 2 && args[0] == "--where";

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

    if (targetAudit)
        return await RunTargetAudit(screen, tracker);

    if (targetPositionAudit)
        return await RunTargetAudit(screen, tracker, requirePosition: true);

    if (watchNameplates)
        return await NameplateWatch.Run(screen, tracker, process.MainWindowHandle);

    if (trialInteractApproach)
        return await RunTrialInteractApproach(screen, tracker, process, cts);

    if (routeProbe)
        return await RunRouteProbe(screen, tracker);

    if (trialCheck)
        return await RunTrialCheck(screen, tracker);

    if (trialMove)
        return await RunTrialMove(screen, tracker, process, cts);

    if (trialHeal)
        return await RunTrialHeal(screen, tracker, process, cts);

    if (trialJudgement)
        return await RunTrialJudgement(screen, tracker, process, cts);

    if (trialJudgementArmed)
        return await RunArmedJudgement(screen, tracker, process, cts);

    // Read-only: where the player stands, in the map coordinates route files
    // use, printed whenever it changes; walk to a stop or a corner and copy it.
    if (whereView)
    {
        int whereSeconds = args.Length == 2 && int.TryParse(args[1], out int askedSeconds)
            ? Math.Clamp(askedSeconds, 5, 600)
            : 60;
        WorldMapAreaDB maps = new(new DataConfig { Root = Path.GetFullPath("Json"), Exp = "forever" });
        Console.WriteLine($"Printing the player's map position for {whereSeconds} seconds; no game input is sent.");
        (int Map, double X, double Y)? lastPrinted = null;
        for (int i = 0; i < whereSeconds * 10; i++)
        {
            screen.Update();
            tracker.Update();
            if (i % 5 == 0 && tracker.TryGetLatest(out VisualBarSample frame) && frame.Position is { } map)
            {
                (int Map, double X, double Y) here = (map.UIMapId, Math.Round(map.MapX * 100, 2), Math.Round(map.MapY * 100, 2));
                if (here != lastPrinted)
                {
                    string zone = maps.TryGet(map.UIMapId, out WorldMapArea area) ? area.AreaName : "an unknown zone";
                    Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{DateTime.Now:HH:mm:ss}  {zone}, \"UIMapId\": {here.Map}, at [{here.X:0.00}, {here.Y:0.00}], " +
                        $"facing {map.Direction * 180 / Math.PI:0} degrees"));
                    lastPrinted = here;
                }
            }
            await Task.Delay(100);
        }
        return 0;
    }

    // Read-only: every occupied bag slot's item and count, and each main-bar
    // action's usable state, from one fresh complete frame.
    if (bagsView)
    {
        for (int i = 0; i < 50; i++)
        {
            screen.Update();
            tracker.Update();
            if (i >= 20 && tracker.TryGetLatest(out VisualBarSample frame) && frame.Bags is { } bags &&
                frame.ActionBar is { } bar && frame.ActionStates is { } states)
            {
                Console.WriteLine($"bag grid: {bags.ReadableHeaders}/5 headers, {bags.ReadableSlots}/200 slots readable");
                for (int bag = 0; bag < VisualBagSnapshot.BagCount; bag++)
                {
                    if (bags.GetBag(bag) is not { } header)
                    {
                        Console.WriteLine($"bag {bag}: unreadable");
                        continue;
                    }
                    Console.WriteLine($"bag {bag}: {header.SlotCount} slots, {header.FreeSlots} free, family {header.Family}");
                    for (int slot = 1; slot <= header.SlotCount; slot++)
                    {
                        if (bags.GetSlot(bag, slot) is not { } item)
                            Console.WriteLine($"  slot {slot}: unreadable");
                        else if (item.ItemId > 0)
                            Console.WriteLine($"  slot {slot}: item {item.ItemId} x{item.Count}");
                    }
                }
                for (int slot = 1; slot <= 12; slot++)
                {
                    if (bar.GetSlot(slot) is { Kind: not VisualActionKind.Empty } action)
                        Console.WriteLine($"action {slot}: {action.Kind} {action.Id} {states.GetSlot(slot)}");
                }
                // The client's keys for the commands the runtime may press, by binding-grid index.
                for (int index = 1; index <= 20; index++)
                {
                    if (frame.Bindings?.GetCode(index) is > 0 and int code && KeyReader.DecodeBinding(code) is { } bound)
                        Console.WriteLine($"binding {index}: {bound.bindingId} = {bound.mod1} {bound.key1}" +
                            (bound.key2 != ConsoleKey.NoName ? $" / {bound.mod2} {bound.key2}" : ""));
                }
                return 0;
            }
            await Task.Delay(100);
        }
        Console.Error.WriteLine("No complete bag grid in a fresh frame.");
        return 2;
    }

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
                            ActionBarText(current.ActionBar) + ", " +
                            SpellBookText(current.SpellBook) + ", " +
                            BindingsText(current.Bindings) + ", " +
                            $"cast={current.LastCastEvent?.ToString() ?? "missing"}");
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
    Console.WriteLine($"target screen={bars.TargetScreenPosition?.ToString() ?? "unavailable"}, " +
        $"slot auras={bars.SlotAuras?.ToString() ?? "unavailable"}");
    Console.WriteLine(NameplateWatch.Describe(bars.Nameplates));
    Console.WriteLine(ActionBarText(bars.ActionBar));
    Console.WriteLine(SpellBookText(bars.SpellBook));
    Console.WriteLine(BindingsText(bars.Bindings));
    Console.WriteLine($"last successful player cast={bars.LastCastEvent?.ToString() ?? "unavailable"}");
    Console.WriteLine($"last target death={bars.LastDeathEvent?.ToString() ?? "unavailable"}; loot={bars.Loot?.ToString() ?? "unavailable"}");
}

static string PositionText(VisualPositionSample? position) => position is { } value
    ? $"map={value.UIMapId}, x={value.MapX:F5}, y={value.MapY:F5}, facing={value.Direction:F2}"
    : "position=unavailable";

static string WorldPositionText(VisualWorldPositionSample? position) => position is { } value
    ? $"world=({value.X:F2}, {value.Y:F2}, {value.Z:F2}) instance={value.InstanceMapId}"
    : "world position=unavailable";

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
        if (tracker.TryGetLatest(out VisualBarSample bars) &&
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
            int? mana = bars.PlayerPowerType == ForeverFrame.ManaPowerType
                ? player.ManaPercent() : null;
            int? target = bars.TargetPresent == true
                ? player.TargetHealthPercent() : null;
            VisualActionStateSnapshot? liveStates = bars.ActionStates;
            bool actionStatesMatch = liveStates is
                { ReadableCount: VisualActionBarSnapshot.SlotCount };
            bool cooldownGridMatches = bars.Cooldowns is
                { ReadableCount: VisualCooldownGridSnapshot.SlotCount };
            VisualBagSnapshot? liveBags = bars.Bags;
            bool bagGridMatches = liveBags is { IsComplete: true };
            int bagItemCount = 0;
            int regularFreeSlots = 0;
            if (bagGridMatches)
            {
                for (int bag = 0; bag < VisualBagSnapshot.BagCount; bag++)
                {
                    VisualBagHeader header = liveBags!.GetBag(bag)!.Value;
                    if (header.Family == 0)
                        regularFreeSlots += header.FreeSlots;
                    for (int slot = 1; slot <= header.SlotCount; slot++)
                        if (liveBags.GetSlot(bag, slot) is { ItemId: > 0 })
                            bagItemCount++;
                }
            }
            int usableActionCount = 0;
            int inRangeActionCount = 0;
            int outOfRangeActionCount = 0;
            int unknownRangeActionCount = 0;
            if (actionStatesMatch)
                for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
                {
                    var state = liveStates!.GetSlot(slot)!.Value;
                    if (state.Usable)
                        usableActionCount++;
                    if (state.InRange == true)
                        inRangeActionCount++;
                    else if (state.InRange == false)
                        outOfRangeActionCount++;
                    else if (bars.ActionBar?.GetSlot(slot)?.Kind !=
                        VisualActionKind.Empty)
                        unknownRangeActionCount++;
                    if (bars.ActionBar?.GetSlot(slot)?.Kind ==
                        VisualActionKind.Empty && state.Usable)
                        actionStatesMatch = false;
                }
            bool maximaMatch =
                (bars.PlayerHealthMax is not { } healthMax || player.HealthMax() == healthMax) &&
                (bars.PlayerPowerMax is not { } powerMax || player.PTMax() == powerMax);
            bool powerTypeReadable = bars.PlayerPowerType is >= 0 and <= 255;
            bool identityReadable = bars.PlayerIdentity is
                { ClassId: > 0, RaceId: > 0, Level: > 0 };
            bool identityMatches = identityReadable &&
                player.Class == (UnitClass)bars.PlayerIdentity!.Value.ClassId &&
                player.Race == (UnitRace)bars.PlayerIdentity!.Value.RaceId &&
                player.Level.Value == bars.PlayerIdentity!.Value.Level &&
                player.Version == ClientVersion.Forever;
            bool trialFactionMatches = bars.PlayerIdentity is not
                { RaceId: (int)UnitRace.Undead } ||
                player.Faction == PlayerFaction.Horde;
            bool controllerFrameReady = ForeverFrame.HasControllerInputs(bars);
            bool targetFlagsMatch = bars.TargetFlags is { } flags &&
                flags.Present == bars.TargetPresent;
            bool targetIdentityReady = ForeverFrame.HasTargetIdentity(bars);
            bool castEventMatches = bars.LastCastEvent is not null;
            bool manaMatches = powerTypeReadable &&
                (bars.PlayerPowerType != ForeverFrame.ManaPowerType || mana == power);
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

            KeyReader.ForeverActionBarProvider =
                () => tracker.TryGetLatest(out var live) ? live.ActionBar : null;
            bool spellActionResolved = false;
            string? resolvedSpellName = null;
            KeyAction? resolvedSpellAction = null;
            if (spellBookMatches && bindingMatches && bars.ActionBar is { } liveActions)
                for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
                {
                    if (liveActions.GetSlot(slot) is not
                            { Kind: VisualActionKind.Spell, Id: > 0 } barSpell ||
                        !spellBook.HasExact(barSpell.Id) ||
                        !spellBook.TryGetValue(barSpell.Id, out var spell))
                        continue;

                    KeyAction candidate = new() { Name = spell.Name };
                    if (!KeyReader.ReadKey(NullLogger.Instance, candidate))
                        continue;

                    spellActionResolved = candidate.Slot > 0 &&
                        liveActions.GetSlot(candidate.Slot) is
                            { Kind: VisualActionKind.Spell, Id: > 0 } match &&
                        spellBook.HasExact(match.Id) &&
                        spellBook.TryGetValue(match.Id, out var matchedSpell) &&
                        matchedSpell.Name == spell.Name;
                    if (spellActionResolved)
                    {
                        resolvedSpellName = spell.Name;
                        resolvedSpellAction = candidate;
                        break;
                    }
                }

            bool liveReadinessMatches = resolvedSpellAction is { } known &&
                bars.Cooldowns?.GetSlot(known.Slot) is { } fill &&
                bars.ActionStates?.GetSlot(known.Slot) is { } cooldownState &&
                ForeverCastGate.IsActionReady(bars, known, spellBook.SpellDB) ==
                    (fill == 0 && cooldownState.Usable &&
                        !cooldownState.NoMana);

            bool absoluteRejected = false;
            try { _ = player.HealthCurrent(); }
            catch (NotSupportedException) { absoluteRejected = true; }

            await Task.Delay(900);
            // Every Forever value comes from this one expiring sample.
            bool staleSampleRejected = !tracker.TryGetLatest(out _);
            bool staleRejected = false;
            try { _ = player.HealthPercent(); }
            catch (InvalidOperationException) { staleRejected = true; }
            bool staleSpellActionRejected = resolvedSpellName is { } name &&
                !KeyReader.ReadKey(NullLogger.Instance,
                    new KeyAction { Name = name });
            spellBook.UpdateForever(null);
            bindingReader.UpdateForever(null);
            bool staleSpellBookReaderReset = !spellBook.IsInitialized &&
                !spellBook.AllRanksReceived && spellBook.Count == 0;
            bool staleBindingReaderReset = !bindingReader.IsInitialized &&
                bindingReader.Count == 0 &&
                bindingReader.SecondaryBindings.Count == 0;
            staleBindingReaderReset &=
                !KeyReader.ResolveFromSlot(new KeyAction { Slot = 1 });
            KeyReader.ForeverActionBarProvider = null;
            bool stalePositionRejected = false;
            try { _ = player.WorldPos; }
            catch (InvalidOperationException) { stalePositionRejected = true; }
            player.Update(screen);
            bool staleMapIdCleared = player.UIMapId.Value == 0;
            bool staleMaximumRejected = false;
            try { _ = player.HealthMax(); }
            catch (InvalidOperationException) { staleMaximumRejected = true; }
            bool staleManaRejected = false;
            try { _ = player.ManaPercent(); }
            catch (InvalidOperationException) { staleManaRejected = true; }

            Console.WriteLine($"PlayerReader: health={health}%, power={power}%, " +
                $"powerType={bars.PlayerPowerType?.ToString() ?? "missing"}, " +
                $"powerTypeReadable={powerTypeReadable}, " +
                $"identity={bars.PlayerIdentity?.ToString() ?? "missing"}, " +
                $"identityMatches={identityMatches}, " +
                $"trialFactionMatches={trialFactionMatches}, " +
                $"controllerFrameReady={controllerFrameReady}, " +
                $"targetFlags={bars.TargetFlags?.ToString() ?? "missing"}, " +
                $"targetFlagsMatch={targetFlagsMatch}, " +
                $"targetIdentity={bars.TargetIdentity?.ToString() ?? "missing"}, " +
                $"targetIdentityReady={targetIdentityReady}, " +
                $"castEvent={bars.LastCastEvent?.ToString() ?? "missing"}, " +
                $"castEventMatches={castEventMatches}, " +
                $"mana={(mana is { } mp ? mp + "%" : "unavailable")}, " +
                $"manaMatches={manaMatches}, " +
                $"staleManaRejected={staleManaRejected}, " +
                $"target={(target is { } value ? value + "%" : "unavailable")}, " +
                $"actionBar={ActionBarText(bars.ActionBar)}, " +
                $"actionStates={liveStates?.ReadableCount.ToString() ?? "missing"}/120, " +
                $"actionStatesMatch={actionStatesMatch}, usableActionCount={usableActionCount}, " +
                $"cooldowns={bars.Cooldowns?.ReadableCount.ToString() ?? "missing"}/12, " +
                $"cooldownSlot1={bars.Cooldowns?.GetSlot(1)?.ToString("P0") ?? "missing"}, " +
                $"cooldownSlot4={bars.Cooldowns?.GetSlot(4)?.ToString("P0") ?? "missing"}, " +
                $"cooldownGridMatches={cooldownGridMatches}, " +
                $"bags={bars.Bags?.ReadableHeaders.ToString() ?? "missing"}/5, " +
                $"bagSlots={bars.Bags?.ReadableSlots.ToString() ?? "missing"}/200, " +
                $"bagItemCount={bagItemCount}, regularFreeSlots={regularFreeSlots}, " +
                $"bagGridMatches={bagGridMatches}, " +
                $"liveReadinessMatches={liveReadinessMatches}, " +
                $"inRangeActionCount={inRangeActionCount}, " +
                $"outOfRangeActionCount={outOfRangeActionCount}, " +
                $"unknownRangeActionCount={unknownRangeActionCount}, " +
                $"healthMax={bars.PlayerHealthMax?.ToString() ?? "unavailable"}, " +
                $"powerMax={bars.PlayerPowerMax?.ToString() ?? "unavailable"}, " +
                $"maximaMatch={maximaMatch}, staleMaximumRejected={staleMaximumRejected}, " +
                $"worldPositionAvailable={worldPositionAvailable}, " +
                $"spellBook={SpellBookText(visualBook)}, " +
                $"spellBookMatches={spellBookMatches}, " +
                $"staleSpellBookReaderReset={staleSpellBookReaderReset}, " +
                $"bindings={BindingsText(visualBindings)}, " +
                $"bindingMatches={bindingMatches}, " +
                $"spellActionResolved={spellActionResolved}, " +
                $"staleSpellActionRejected={staleSpellActionRejected}, " +
                $"staleBindingReaderReset={staleBindingReaderReset}, " +
                $"positionMatches={positionMatches}, " +
                $"stalePositionRejected={stalePositionRejected}, " +
                $"staleMapIdCleared={staleMapIdCleared}, " +
                $"absoluteRejected={absoluteRejected}, staleRejected={staleRejected}, " +
                $"staleSampleRejected={staleSampleRejected}");
            return actionStatesMatch && cooldownGridMatches &&
                bagGridMatches && liveReadinessMatches && maximaMatch &&
                powerTypeReadable && manaMatches && identityMatches &&
                trialFactionMatches && controllerFrameReady &&
                targetFlagsMatch && castEventMatches &&
                spellBookMatches && staleSpellBookReaderReset &&
                bindingMatches && spellActionResolved &&
                staleSpellActionRejected && staleBindingReaderReset &&
                positionMatches && stalePositionRejected && staleMapIdCleared &&
                staleMaximumRejected && staleManaRejected &&
                absoluteRejected && staleRejected && staleSampleRejected ? 0 : 2;
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

        Vector3 fromMap = map.ToWorld(area);
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

        string cache = ForeverNavmesh.CacheDirectory(config, area.Continent);
        if (!Directory.Exists(cache))
        {
            Console.Error.WriteLine($"No baked Forever navmesh at {cache}.");
            return 2;
        }

        using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, cache);
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

        foreach (float heightHint in new[] { startZ, 0f })
        for (int step = 0; step < 8; step++)
        {
            float angle = (float)map.Direction + step * MathF.Tau / 8;
            Vector2 ahead = fromWorld + new Vector2(MathF.Cos(angle),
                MathF.Sin(angle)) * 3;
            bool straight = nav.TryWalkStraight(new(start.X, start.Y, 0), ahead,
                out Vector3 walkEnd, out string walkReason);
            var local = nav.FindPath(new Vector3(start.X, start.Y, heightHint),
                new Vector3(ahead.X, ahead.Y, heightHint), null);
            if (local is not { locations.Count: >= 2 })
            {
                Console.WriteLine($"Local Z={heightHint:F2} {step * 45,3}°: no path");
                continue;
            }
            float length = 0;
            for (int point = 1; point < local.locations.Count; point++)
                length += Vector2.Distance(
                    new(local.locations[point - 1].X,
                        local.locations[point - 1].Y),
                    new(local.locations[point].X,
                        local.locations[point].Y));
            Vector3 first = local.locations[0];
            Vector3 last = local.locations[^1];
            float startError = Vector2.Distance(new(first.X, first.Y), fromWorld);
            float endError = Vector2.Distance(new(last.X, last.Y), ahead);
            Console.WriteLine($"Local Z={heightHint:F2} {step * 45,3}°: length={length:F2}, " +
                $"snap={startError:F2}/{endError:F2}, surface={first.Z:F2}/{last.Z:F2}, points={local.locations.Count}");
            if (heightHint == 0)
                Console.WriteLine($"  connected straight walk={straight}, Z={walkEnd.Z:F2}; {walkReason}");
        }

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

static bool HasTrialBinding(VisualBarSample sample, int slot,
    ConsoleKey expected)
{
    if (slot is < 1 or > 12 ||
        sample.Bindings is not
            { ReadableCount: VisualBindingSnapshot.Count } bindings)
        return false;

    int? code = bindings.GetCode(20 + slot);
    if (code is not > 0 ||
        KeyReader.DecodeBinding(code.Value) is not { } decoded)
        return false;

    return decoded.bindingId == BindingID.ACTIONBUTTON1 + (slot - 1) &&
        decoded.key1 == expected && decoded.mod1 == ModifierKey.None;
}

static async Task<int> RunTargetAudit(WowScreenWGC screen,
    ForeverVisualBarTracker tracker,
    Action<VisualTargetIdentity>? onEligible = null,
    bool requirePosition = false)
{
    VisualTargetIdentity? firstIdentity = null;
    long firstFrame = 0;
    for (int i = 0; i < 60; i++)
    {
        screen.Update();
        tracker.Update();
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.HasTargetIdentity(bars) ||
            bars.TargetFlags is not { } flags ||
            bars.TargetIdentity is not { } identity ||
            bars.PlayerIdentity is not { } playerIdentity)
        {
            await Task.Delay(100);
            continue;
        }

        if (!identity.Present)
        {
            await Task.Delay(100);
            continue;
        }

        bool eligible = flags is
                { Attackable: true, Dead: false, PlayerDead: false } &&
            !identity.IsPlayer && identity.Level <= playerIdentity.Level + 1 &&
            bars.TargetHealth > 0 && bars.PlayerHealth >= 0.70 &&
            bars.PlayerPower >= (flags.PlayerCombat ? 0.25 : 0.60) &&
            (!requirePosition ||
                (bars.TargetWorldPosition is { } targetWorld &&
                 bars.WorldPosition is { } playerWorld &&
                 targetWorld.InstanceMapId == playerWorld.InstanceMapId &&
                 Vector2.Distance(new((float)targetWorld.X, (float)targetWorld.Y),
                     new((float)playerWorld.X, (float)playerWorld.Y)) <= 50) ||
                bars.TargetMapPosition is { } targetMap &&
                targetMap.UIMapId == bars.Position?.UIMapId ||
                bars.TargetScreenPosition is not null);
        if (firstIdentity is null)
        {
            firstIdentity = identity;
            firstFrame = bars.FrameId;
            await Task.Delay(200);
            continue;
        }
        if (bars.FrameId == firstFrame)
        {
            await Task.Delay(100);
            continue;
        }

        bool stable = identity == firstIdentity.Value;
        Console.WriteLine($"Target audit: identity={identity}, " +
            $"playerLevel={playerIdentity.Level}, targetHealth={bars.TargetHealth:P0}, " +
            $"playerHealth={bars.PlayerHealth:P0}, mana={bars.PlayerPower:P0}, " +
            $"attackable={flags.Attackable}, combat={flags.PlayerCombat}, " +
            $"stable={stable}, eligible={eligible}, " +
            $"targetWorld={bars.TargetWorldPosition?.ToString() ?? "missing"}, " +
            $"targetMap={bars.TargetMapPosition?.ToString() ?? "missing"}, " +
            $"targetScreen={bars.TargetScreenPosition?.ToString() ?? "missing"}, " +
            $"targetPlate={bars.Nameplates?.Target?.Slot.ToString() ?? "missing"}, " +
            $"map={bars.Position?.UIMapId.ToString() ?? "missing"}; no input sent.");
        if (stable && eligible)
            onEligible?.Invoke(identity);
        return stable && eligible ? 0 : 2;
    }

    Console.Error.WriteLine("No two fresh complete frames with a selected target; no input sent.");
    return 2;
}

static async Task<int> RunTrialInteractApproach(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, WowProcess process,
    CancellationTokenSource cts)
{
    VisualTargetIdentity? auditedTarget = null;
    if (await RunTargetAudit(screen, tracker,
            identity => auditedTarget = identity) != 0)
        return 2;

    screen.Update();
    tracker.Update();
    if (!tracker.TryGetLatest(out VisualBarSample before) ||
        before.TargetIdentity != auditedTarget ||
        before.WorldPosition is not { } start ||
        before.TargetFlags is not
            { Present: true, Attackable: true, Dead: false,
              PlayerCombat: false, PlayerDead: false } ||
        before.Bindings?.GetCode(18) is not > 0 ||
        KeyReader.DecodeBinding(before.Bindings.GetCode(18)!.Value) is not { } binding ||
        binding.bindingId != BindingID.INTERACTTARGET ||
        !((binding.key1 == ConsoleKey.D3 && binding.mod1 == ModifierKey.Shift) ||
          (binding.key2 == ConsoleKey.D3 && binding.mod2 == ModifierKey.Shift)))
    {
        Console.Error.WriteLine("Interact approach refused: target or Shift+3 binding changed; no input sent.");
        return 2;
    }

    WowProcessInput input = new(NullLogger<WowProcessInput>.Instance, cts, process);
    try
    {
        input.PressRandomWithModifier(ConsoleKey.D3, ModifierKey.Shift, 75, cts.Token);
    }
    finally
    {
        input.Reset();
    }

    VisualBarSample after = default;
    for (int i = 0; i < 30; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample sample) &&
            sample.TargetIdentity == auditedTarget &&
            sample.WorldPosition is not null)
            after = sample;
        await Task.Delay(100);
    }
    if (after.TargetIdentity != auditedTarget ||
        after.WorldPosition is not { } end)
    {
        Console.Error.WriteLine("Interact approach outcome unreadable; no further input sent.");
        return 2;
    }
    float moved = Vector2.Distance(new((float)start.X, (float)start.Y),
        new((float)end.X, (float)end.Y));
    bool inRange = after.ActionStates?.GetSlot(2) is { InRange: true };
    Console.WriteLine($"One verified Interact Target press: moved={moved:F2} yd, " +
        $"JudgementInRange={inRange}, combat={after.TargetFlags?.PlayerCombat}, " +
        $"targetStable={after.TargetIdentity == auditedTarget}.");
    return moved >= 0.20 || inRange ? 0 : 2;
}

static async Task<int> RunTrialCheck(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, bool requireNearRoute = true)
{
    // Read-only gate for the candidate profile. No controller or input service
    // is constructed here; every value comes from one fresh visual capture.
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    string profileFile = Path.Combine(config.Class, ForeverVerifiedSetup.PaladinTrialProfile);
    ClassConfiguration profile = JsonConvert.DeserializeObject<ClassConfiguration>(
        File.ReadAllText(profileFile)) ?? throw new InvalidDataException("Trial profile is empty.");
    KeyAction[] actions = [.. profile.Pull.Sequence, .. profile.Combat.Sequence];
    (string Name, int Id, int Slot)[] expected =
    [
        ("Holy Light", 635, 1),
        ("Judgement", 20271, 2),
        ("Holy Strike", 679, 3)
    ];
    static bool HasRules(KeyAction action, params string[] rules) =>
        action.Requirements.Count == rules.Length &&
        rules.All(action.Requirements.Contains);
    bool profileSafe = profile.Paths.Length == 0 &&
        !profile.Loot && !profile.UseMount &&
        profile.Pull.Sequence.Length == 1 &&
        profile.Combat.Sequence.Length == 3 &&
        HasRules(profile.Pull.Sequence[0], "Spell:20271", "TargetHealth% > 0") &&
        HasRules(profile.Combat.Sequence[0], "Spell:635", "Health% < 60", "Mana% >= 25") &&
        HasRules(profile.Combat.Sequence[1], "Spell:679", "TargetHealth% > 0") &&
        HasRules(profile.Combat.Sequence[2], "Spell:20271", "TargetHealth% > 0") &&
        actions.All(a => a.WhenUsable && !a.BaseAction &&
            !a.AfterCastWaitBuff && !a.AfterCastAuraExpected &&
            string.IsNullOrEmpty(a.MacroText) &&
            expected.Any(e => e.Name == a.Name && e.Id == a.SpellId &&
                e.Slot == a.Slot && a.Key == e.Slot.ToString())) &&
        expected.All(e => actions.Any(a => a.Name == e.Name &&
            a.SpellId == e.Id && a.Slot == e.Slot));
    if (!profileSafe)
    {
        Console.Error.WriteLine("Trial profile has unsupported or changed actions, rules, or route.");
        return 2;
    }

    WorldMapAreaDB maps = new(config);
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

        if (map.UIMapId != 1420 || world.InstanceMapId != 0 ||
            bars.ActionBar is not { ReadableCount: VisualActionBarSnapshot.SlotCount } bar ||
            bars.ActionStates is not { ReadableCount: VisualActionBarSnapshot.SlotCount } states ||
            bars.Cooldowns is not { ReadableCount: VisualCooldownGridSnapshot.SlotCount } cooldowns ||
            bars.SpellBook is not { Count: > 0 } book || book.ReadableCount != book.Count ||
            bars.Bindings is not { ReadableCount: VisualBindingSnapshot.Count } bindings ||
            bars.PlayerPowerType != 0 ||
            bars.PlayerIdentity is not { ClassId: (int)UnitClass.Paladin } ||
            bars.TargetFlags is not { } targetFlags ||
            bars.LastCastEvent is null ||
            targetFlags.Present != bars.TargetPresent ||
            (targetFlags.PlayerCombat && requireNearRoute) ||
            targetFlags.PlayerDead)
        {
            Console.Error.WriteLine("Trial data is incomplete or the character left Tirisfal.");
            return 2;
        }

        SpellBookReader spellBook = new(new SpellDB(config));
        spellBook.UpdateForever(book);
        bool actionsMatch = spellBook.IsInitialized && spellBook.AllRanksReceived;
        // This bounded route/cast trial uses Holy Light and Judgement. The
        // character may have upgraded Holy Strike on slot 3 independently.
        foreach (var expectedAction in expected.Where(a => a.Slot is 1 or 2))
        {
            int slot = expectedAction.Slot;
            ConsoleKey expectedKey = slot switch
            {
                1 => ConsoleKey.D1,
                2 => ConsoleKey.D2,
                3 => ConsoleKey.D3,
                _ => ConsoleKey.NoName
            };
            actionsMatch &= bar.GetSlot(slot) is
                    { Kind: VisualActionKind.Spell } placed &&
                placed.Id == expectedAction.Id &&
                spellBook.HasExact(expectedAction.Id) &&
                spellBook.TryGetValue(expectedAction.Id, out var spell) &&
                spell.Name == expectedAction.Name &&
                states.GetSlot(slot) is not null &&
                cooldowns.GetSlot(slot) is not null &&
                HasTrialBinding(bars, slot, expectedKey);
            KeyAction trialAction = new()
            {
                Name = expectedAction.Name,
                SpellId = expectedAction.Id,
                Slot = slot
            };
            bool expectedReady = states.GetSlot(slot) is
                    { Usable: true, NoMana: false } &&
                cooldowns.GetSlot(slot) == 0;
            actionsMatch &= ForeverCastGate.IsActionReady(
                bars, trialAction, spellBook.SpellDB) ==
                expectedReady;
        }
        if (!actionsMatch)
        {
            Console.Error.WriteLine("Trial spells, bindings, or action signals do not match the profile.");
            return 2;
        }

        if (!maps.TryGet(1420, out WorldMapArea area))
        {
            Console.Error.WriteLine("Trial map 1420 is missing from the Forever map table.");
            return 2;
        }
        Vector3 mappedPlayer = map.ToWorld(area);
        float mapError = Vector2.Distance(new(mappedPlayer.X, mappedPlayer.Y),
            new((float)world.X, (float)world.Y));
        if (mapError > 3f)
        {
            await Task.Delay(100);
            continue;
        }

        bool routeConnected = true;
        if (requireNearRoute)
        {
            if (bars.PlayerHealth < 0.70 || bars.PlayerPower < 0.60 || targetFlags.Present)
            {
                Console.Error.WriteLine("Local route requires high health, mana, and no target.");
                return 2;
            }

            string cache = ForeverNavmesh.CacheDirectory(config, area.Continent);
            if (!Directory.Exists(cache))
            {
                Console.Error.WriteLine($"Local navmesh cache is missing: {cache}");
                return 2;
            }
            Vector2 here = new((float)world.X, (float)world.Y);
            Vector2 ahead = here + new Vector2(
                MathF.Cos((float)map.Direction), MathF.Sin((float)map.Direction)) * 3f;
            using NavmeshPathfinder nav = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, cache);
            float startZ = 0;
            float endZ = 0;
            routeConnected = nav.TryGetHeight(here.X, here.Y, out startZ) &&
                nav.TryGetHeight(ahead.X, ahead.Y, out endZ) &&
                MathF.Abs(endZ - startZ) <= 1.5f;
            if (routeConnected)
            {
                var path = nav.FindPath(new(here.X, here.Y, startZ),
                    new(ahead.X, ahead.Y, endZ), null);
                routeConnected = path is { locations.Count: >= 2 } &&
                    nav.LastStats.TilesBaked == 0 &&
                    Vector2.Distance(new(path.locations[0].X, path.locations[0].Y), here) <= 0.5f &&
                    Vector2.Distance(new(path.locations[^1].X, path.locations[^1].Y), ahead) <= 0.5f;
            }
        }
        Console.WriteLine($"Trial: profileSafe={profileSafe}, actionsMatch={actionsMatch}, " +
            $"mapError={mapError:F2} yd, routeConnected={routeConnected}, " +
            $"health={bars.PlayerHealth:P0}, mana={bars.PlayerPower:P0}, " +
            $"target={bars.TargetPresent}, attackable={targetFlags.Attackable}, " +
            $"targetDead={targetFlags.Dead}, " +
            $"identity={bars.PlayerIdentity}, bagSlots={bars.Bags?.ReadableSlots.ToString() ?? "missing"}/200");
        return routeConnected ? 0 : 2;
    }

    Console.Error.WriteLine("No consistent fresh Forever trial sample was captured.");
    return 2;
}

static async Task<int> RunTrialMove(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, WowProcess process,
    CancellationTokenSource cts)
{
    if (await RunTrialCheck(screen, tracker) != 0)
        return 2;

    async Task<VisualBarSample?> Capture()
    {
        for (int i = 0; i < 20; i++)
        {
            screen.Update();
            tracker.Update();
            if (tracker.TryGetLatest(out VisualBarSample sample) &&
                sample.WorldPosition is not null && sample.Position is not null &&
                sample.TargetFlags is not null)
                return sample;
            await Task.Delay(100);
        }
        return null;
    }

    VisualBarSample? before = await Capture();
    await Task.Delay(400);
    VisualBarSample? stationary = await Capture();
    if (before is not { } first || stationary is not { } current ||
        first.WorldPosition is not { } firstWorld ||
        current.WorldPosition is not { } startWorld ||
        current.Position is not { } startMap ||
        first.PlayerHealth < 0.70 || current.PlayerHealth < 0.70 ||
        first.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        current.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        current.TargetPresent != false ||
        startMap.UIMapId != 1420 || startWorld.InstanceMapId != 0 ||
        Vector2.Distance(new((float)firstWorld.X, (float)firstWorld.Y),
            new((float)startWorld.X, (float)startWorld.Y)) > 0.3f)
    {
        Console.Error.WriteLine("Movement trial refused: the character is moving, targeted, or below 70% health.");
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

    VisualBarSample? after = null;
    for (int i = 0; i < 25; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample sample) &&
            sample.WorldPosition is { } end &&
            Vector2.Distance(new((float)startWorld.X, (float)startWorld.Y),
                new((float)end.X, (float)end.Y)) >= 0.25f)
        {
            after = sample;
            break;
        }
        await Task.Delay(100);
    }
    if (after is not { } observed ||
        observed.WorldPosition is not { } endWorld ||
        observed.Position is not { UIMapId: 1420 } ||
        observed.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        observed.PlayerHealth < 0.65)
    {
        Console.Error.WriteLine("Movement trial failed to confirm a safe fresh endpoint.");
        return 2;
    }

    float distance = Vector2.Distance(
        new((float)startWorld.X, (float)startWorld.Y),
        new((float)endWorld.X, (float)endWorld.Y));
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    WorldMapAreaDB maps = new(config);
    if (!maps.TryGet(1420, out WorldMapArea area))
        return 2;
    NavmeshBakeOptions bake = new();
    string version = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
    string hash = NavmeshSettings.ComputeSettingsHash("forever", version,
        bake.AgentRadius, bake.AgentMaxClimb, bake.WalkableSlope);
    string cache = Path.Combine(config.Navmesh, area.Continent, hash);
    using NavmeshPathfinder nav = new(NullLogger.Instance, null, cache,
        bake, new NavmeshQueryOptions());
    bool startWalkable = nav.TryGetHeight((float)startWorld.X,
        (float)startWorld.Y, out float startZ);
    bool endWalkable = nav.TryGetHeight((float)endWorld.X,
        (float)endWorld.Y, out float endZ);
    bool walkable = startWalkable && endWalkable;
    if (!walkable)
        return 2;
    var path = nav.FindPath(
        new((float)startWorld.X, (float)startWorld.Y, startZ),
        new((float)endWorld.X, (float)endWorld.Y, endZ), null);
    bool connected = path is { locations.Count: >= 2 } &&
        nav.LastStats.TilesBaked == 0;
    Console.WriteLine($"Single 200 ms Up Arrow: {distance:F2} yd, " +
        $"meshConnected={connected}, health={observed.PlayerHealth:P0}, " +
        $"target={observed.TargetFlags?.Present}");
    return distance is >= 0.25f and <= 4f && connected ? 0 : 2;
}

static async Task<int> RunTrialHeal(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, WowProcess process,
    CancellationTokenSource cts)
{
    if (await RunTrialCheck(screen, tracker, requireNearRoute: false) != 0)
        return 2;
    screen.Update();
    tracker.Update();
    if (!tracker.TryGetLatest(out VisualBarSample before) ||
        before.PlayerHealth < 0.70 || before.PlayerPower < 0.60 ||
        before.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        before.SpellBook is not { } visualBook ||
        before.LastCastEvent is not { } beforeCast)
    {
        Console.Error.WriteLine("Heal trial refused: 70% health, 60% mana, no target, or spellbook check failed.");
        return 2;
    }

    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBook = new(new SpellDB(config));
    spellBook.UpdateForever(visualBook);
    KeyAction heal = new()
        { Name = "Holy Light", SpellId = 635, Slot = 1,
          ConsoleKey = ConsoleKey.D1 };
    if (!ForeverCastGate.CanPress(before, heal, spellBook.SpellDB, selfCast: true))
    {
        Console.Error.WriteLine("Heal trial refused: Holy Light is not verified ready.");
        return 2;
    }

    InputWindowsNative input = new(process, cts, 0);
    try
    {
        input.PressFixed((int)ConsoleKey.D1, 75, cts.Token);
    }
    finally
    {
        input.KeyUp((int)ConsoleKey.D1);
    }

    bool observedCast = false;
    VisualCastEvent? confirmedCast = null;
    double lastMana = before.PlayerPower;
    for (int i = 0; i < 50; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample after))
        {
            if (after.PlayerHealth < 0.90 ||
                after.TargetFlags is not
                    { Present: false, PlayerCombat: false,
                      PlayerDead: false })
            {
                Console.Error.WriteLine("Heal trial stopped: health or target safety changed.");
                return 2;
            }
            lastMana = after.PlayerPower;
            if (after.LastCastEvent is { } cast &&
                cast.ConfirmsNewSuccess(beforeCast, 635))
            {
                observedCast = true;
                confirmedCast = cast;
                break;
            }
        }
        await Task.Delay(100);
    }
    Console.WriteLine($"Single Holy Light keypress: observed={observedCast}, " +
        $"castEvent={confirmedCast?.ToString() ?? "missing"}, " +
        $"mana={before.PlayerPower:P0}->{lastMana:P0}, target=none");
    return observedCast ? 0 : 2;
}

static async Task<int> RunArmedJudgement(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, WowProcess process,
    CancellationTokenSource cts)
{
    if (await RunTrialCheck(screen, tracker, requireNearRoute: false) != 0)
        return 2;

    Console.WriteLine("Judgement trial armed and capturing. Send go on stdin for one immediate safety check and keypress.");
    Task<string?> command = Console.In.ReadLineAsync();
    while (!command.IsCompleted && !cts.IsCancellationRequested)
    {
        screen.Update();
        tracker.Update();
        await Task.Delay(50);
    }
    if (cts.IsCancellationRequested ||
        !string.Equals(await command, "go", StringComparison.OrdinalIgnoreCase))
        return 2;

    return await RunTrialJudgement(screen, tracker, process, cts,
        profilePrechecked: true);
}

static async Task<int> RunTrialJudgement(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, WowProcess process,
    CancellationTokenSource cts, bool profilePrechecked = false)
{
    if (!profilePrechecked &&
        await RunTrialCheck(screen, tracker, requireNearRoute: false) != 0)
        return 2;
    VisualTargetIdentity? auditedTarget = null;
    if (await RunTargetAudit(screen, tracker,
            identity => auditedTarget = identity) != 0)
        return 2;
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBook = new(new SpellDB(config));
    KeyAction judgement = new()
        { Name = "Judgement", SpellId = 20271, Slot = 2,
          ConsoleKey = ConsoleKey.D2 };
    VisualBarSample before = default;
    bool ready = false;
    for (int i = 0; i < 100; i++)
    {
        screen.Update();
        tracker.Update();
        if (!tracker.TryGetLatest(out VisualBarSample current) ||
            !ForeverFrame.HasTargetIdentity(current))
        {
            await Task.Delay(100);
            continue;
        }
        if (current.TargetIdentity != auditedTarget ||
            current.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerDead: false } ||
            current.TargetHealth <= 0 ||
            current.PlayerHealth < 0.70)
        {
            Console.Error.WriteLine("Judgement trial refused: target changed, died, or player health fell below 70%; no input sent.");
            return 2;
        }
        spellBook.UpdateForever(current.SpellBook);
        if (ForeverCastGate.CanPress(current, judgement, spellBook.SpellDB))
        {
            before = current;
            ready = true;
            break;
        }
        await Task.Delay(100);
    }
    if (!ready)
    {
        Console.Error.WriteLine("Judgement trial refused: action was not safely ready within 10 seconds; no input sent.");
        return 2;
    }

    InputWindowsNative input = new(process, cts, 0);
    try
    {
        input.PressFixed((int)ConsoleKey.D2, 75, cts.Token);
    }
    finally
    {
        input.KeyUp((int)ConsoleKey.D2);
    }

    bool observedAction = false;
    VisualCastEvent? confirmedCast = null;
    VisualCastEvent beforeCast = before.LastCastEvent!.Value;
    double lastMana = before.PlayerPower;
    double lastTargetHealth = before.TargetHealth;
    bool flagsReadable = true;
    bool identityStable = true;
    for (int i = 0; i < 30; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample after))
        {
            flagsReadable &= after.TargetFlags is not null;
            identityStable &= after.TargetIdentity == before.TargetIdentity;
            lastMana = after.PlayerPower;
            lastTargetHealth = after.TargetHealth;
            if (after.LastCastEvent is { } cast &&
                cast.ConfirmsNewSuccess(beforeCast, 20271))
            {
                observedAction = true;
                confirmedCast = cast;
                break;
            }
        }
        await Task.Delay(100);
    }
    Console.WriteLine($"Single Judgement keypress: observed={observedAction}, " +
        $"castEvent={confirmedCast?.ToString() ?? "missing"}, " +
        $"mana={before.PlayerPower:P0}->{lastMana:P0}, " +
        $"targetHealth={before.TargetHealth:P0}->{lastTargetHealth:P0}, " +
        $"targetFlagsReadable={flagsReadable}, identityStable={identityStable}; " +
        "finish the fight manually.");
    return observedAction && flagsReadable && identityStable ? 0 : 2;
}

static bool HasChanged(VisualBarSample previous, VisualBarSample current)
    => Math.Abs(previous.PlayerHealth - current.PlayerHealth) >= 0.02
    || Math.Abs(previous.PlayerPower - current.PlayerPower) >= 0.02
    || Math.Abs(previous.TargetHealth - current.TargetHealth) >= 0.02
    || previous.TargetPresent != current.TargetPresent
    || previous.PlayerHealthMax != current.PlayerHealthMax
    || previous.PlayerPowerMax != current.PlayerPowerMax
    || previous.WorldPosition != current.WorldPosition
    || previous.LastCastEvent != current.LastCastEvent
    || previous.TargetIdentity != current.TargetIdentity
    || previous.TargetWorldPosition != current.TargetWorldPosition
    || previous.TargetMapPosition != current.TargetMapPosition
    || ActionBarChanged(previous.ActionBar, current.ActionBar)
    || SpellBookChanged(previous.SpellBook, current.SpellBook)
    || BindingsChanged(previous.Bindings, current.Bindings)
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
    // The Paladin trial setup keeps Seal of Righteousness on this slot.
    const int sealSlot = 5;
    KeyReader.RequireGameBindings = true;
    DataConfig dbc = new()
        { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBookReader = new(new SpellDB(dbc));
    SpellDB spells = spellBookReader.SpellDB;
    KeyBindingsReader bindingReader = new(NullLogger<KeyBindingsReader>.Instance);
    foreach (double scale in new[] { 0.75, 1.0, 1.25 })
    {
        const int width = 900;
        const int height = 1000;
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
        DrawPositionPixel(0, 42);
        DrawPositionPixel(1, (int)Math.Floor(0.345678 * 16777215));
        DrawPositionPixel(2, (int)Math.Floor(0.789012 * 16777215));
        DrawPositionPixel(3, (int)Math.Floor(1.23456 * 100000));
        DrawPositionPixel(4, (0xA55AA5 - 42 -
            (int)Math.Floor(0.345678 * 16777215) -
            (int)Math.Floor(0.789012 * 16777215) -
            (int)Math.Floor(1.23456 * 100000)) & 0xFFFFFF);
        DrawPositionPixel(7, 0x00FF00);
        DrawPositionPixel(10, 0x5AA55A);
        DrawPositionPixel(11, 742);
        DrawPositionPixel(12, (0xA55A11 - 742) & 0xFFFFFF);
        DrawPositionPixel(13, 1000);
        DrawPositionPixel(14, (0xA55A22 - 1000) & 0xFFFFFF);
        DrawPositionPixel(15, 0x5A88A5);
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
        DrawWorldPixel(9, 0);
        DrawWorldPixel(10, 0xA56644);
        int identity = 2 + 5 * 64 + 12 * 16384;
        DrawWorldPixel(11, identity);
        DrawWorldPixel(12, (0xA57744 - identity) & 0xFFFFFF);
        DrawWorldPixel(13, 35);
        DrawWorldPixel(14, 0xA57755 - 35);
        DrawCastPixel(0, 0);
        DrawCastPixel(1, 0);
        DrawCastPixel(2, 0xA5CC11);
        const int targetHash1 = 123456;
        const int targetHash2 = 654321;
        const int targetLevel = 12;
        DrawCastPixel(3, targetHash1);
        DrawCastPixel(4, targetHash2);
        DrawCastPixel(5, targetLevel * 2);
        DrawCastPixel(6, (0xA58866 - targetHash1 - targetHash2 - targetLevel * 2) & 0xFFFFFF);
        const int targetX = 8388608 - 61234;
        const int targetY = 8388608 + 123756;
        const int targetZ = 8388608 + 8765;
        DrawCastPixel(7, targetX);
        DrawCastPixel(8, targetY);
        DrawCastPixel(9, targetZ);
        DrawCastPixel(10, 0);
        DrawCastPixel(11, (0xA59977 - targetX - targetY - targetZ -
            targetHash1 - targetHash2) & 0xFFFFFF);
        const int targetMapId = 42;
        const int targetMapX = 6000000;
        const int targetMapY = 11000000;
        DrawCastPixel(12, targetMapId);
        DrawCastPixel(13, targetMapX);
        DrawCastPixel(14, targetMapY);
        DrawCastPixel(15, (0xA5AA88 - targetMapId - targetMapX - targetMapY -
            targetHash1 - targetHash2) & 0xFFFFFF);
        // The target holds roster slot 1, whose marker is drawn below.
        // DrawLifecycle writes the right-hand column the roster shares.
        DrawLifecycle(245, RosterCodec.Header(plates: 1, attackable: 1, friendly: 0));
        DrawLifecycle(260, RosterCodec.Row(1, new PlateSpec(1, targetHash1, targetHash2,
            Level: targetLevel, IsTarget: 2), new VisualTargetIdentity(targetHash1, targetHash2, targetLevel, false)));
        for (int slot = 2; slot <= VisualNameplateRoster.SlotCount; slot++)
            DrawLifecycle(260 + (slot - 1) * 15, RosterCodec.Row(slot, null));
        // Slot 5 (Seal) has a matching aura, slot 4 a reference without one,
        // and every other slot is empty.
        int auraTop = top + rowStep * 4 + (int)Math.Round(632 * scale);
        DrawCastPixel(28, VisualBarReader.SlotAuraState);
        DrawCastPixel(29, (0xA5B5A9 - VisualBarReader.SlotAuraState) & 0xFFFFFF);
        DrawAuraIcon(pixels, VisualAuraReader.ReferenceColumn(sealSlot), 0, 80);
        DrawAuraIcon(pixels, VisualAuraReader.ReferenceColumn(4), 0, 200);
        DrawAuraIcon(pixels, 7, 1, 80);
        DrawLifecycle(205, [7, targetHash1, targetHash2,
            (0xA5D611 - 7 - targetHash1 - targetHash2) & 0xFFFFFF]);
        DrawLifecycle(225, [3, 3, 8, 2, 0, 1, targetHash1, targetHash2,
            (0xA5D722 - 3 - 3 - 8 - 2 - 1 - targetHash1 - targetHash2) & 0xFFFFFF]);
        int markerSize = (int)Math.Round(28 * scale);
        int markerBlack = (int)Math.Round(22 * scale);
        int markerCore = Math.Max(3, (int)Math.Round(6 * scale));
        for (int y = 600 - markerSize / 2; y < 600 + markerSize / 2; y++)
            for (int x = 780 - markerSize / 2; x < 780 + markerSize / 2; x++)
                pixels[y * width + x] = new Bgra32
                    { R = 255, B = 255, A = 255 };
        for (int y = 600 - markerBlack / 2; y < 600 + markerBlack / 2; y++)
            for (int x = 780 - markerBlack / 2; x < 780 + markerBlack / 2; x++)
                pixels[y * width + x] = new Bgra32 { A = 255 };
        for (int y = 600 - markerCore / 2; y < 600 + markerCore / 2; y++)
            for (int x = 780 - markerCore / 2; x < 780 + markerCore / 2; x++)
                pixels[y * width + x] = new Bgra32
                    { R = 16, G = 255, B = 255, A = 255 };
        DrawSpellGridSlot(1, VisualSpellIdSource.SpellId, 6603);
        DrawSpellGridSlot(2, VisualSpellIdSource.ActionId, 270);
        DrawSpellGridSlot(3, VisualSpellIdSource.SpellId, 7744);
        for (int index = 1; index <= VisualBindingSnapshot.Count; index++)
            DrawBindingSlot(index, 0);
        DrawBindingSlot(21, (21 << 14) | (28 << 7));
        DrawBindingSlot(45, (45 << 14) | 52);
        for (int slot = 1; slot <= VisualActionBarSnapshot.SlotCount; slot++)
        {
            DrawActionGridSlot(slot, 0, 0);
            DrawActionStateSlot(slot, 0, 0, 1);
        }
        DrawActionGridSlot(4, 1, 7744);
        DrawActionGridSlot(26, 4, 270);
        DrawActionStateSlot(4, 1, 7744, 6);
        DrawActionStateSlot(26, 4, 270, 1);
        for (int slot = 1; slot <= VisualCooldownGridSnapshot.SlotCount; slot++)
            DrawCooldownSlot(slot, slot == 4 ? 1 : 0,
                slot == 4 ? 7744 : 0, slot == 4 ? 0.4 : 0);
        for (int bag = 0; bag < VisualBagSnapshot.BagCount; bag++)
            DrawBagHeader(bag, bag == 0 ? 16 : 0,
                bag == 0 ? 14 : 0, 0);
        for (int bag = 0; bag < VisualBagSnapshot.BagCount; bag++)
            for (int slot = 1; slot <= VisualBagSnapshot.SlotsPerBag; slot++)
                DrawBagSlot(bag, slot, bag == 0 && slot == 1 ? 6948 :
                    bag == 0 && slot == 2 ? 117 : 0,
                    bag == 0 && slot == 1 ? 1 :
                    bag == 0 && slot == 2 ? 3 : 0);

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
            || result.PlayerPowerType != 0
            || result.PlayerIdentity is not
                { ClassId: 2, RaceId: 5, Level: 12 }
            || result.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerCombat: false, PlayerDead: false }
            || result.TargetIdentity is not
                { GuidHash1: targetHash1, GuidHash2: targetHash2,
                  Level: targetLevel, IsPlayer: false }
            || result.TargetWorldPosition is not { } targetPosition
            || Math.Abs(targetPosition.X + 612.34) > 0.001
            || Math.Abs(targetPosition.Y - 1237.56) > 0.001
            || Math.Abs(targetPosition.Z - 87.65) > 0.001
            || targetPosition.InstanceMapId != 0
            || result.TargetMapPosition is not { UIMapId: targetMapId } targetMapPosition
            || Math.Abs(targetMapPosition.MapX - targetMapX / 16777215.0) > 0.00001
            || Math.Abs(targetMapPosition.MapY - targetMapY / 16777215.0) > 0.00001
            || result.Nameplates is not { PlateCount: 1, ReadableCount: VisualNameplateRoster.SlotCount }
            || result.Nameplates.Target is not { Slot: 1, IsEmpty: false }
            || result.SlotAuras?.IsActive(sealSlot) != true
            || result.SlotAuras?.IsActive(4) != false
            || result.SlotAuras?.IsActive(1) is not null
            || result.TargetScreenPosition is not { } targetScreen
            || Math.Abs(targetScreen.X - 780.0 / width) > 0.01
            || Math.Abs(targetScreen.Y - 0.4) > 0.01
            || result.LastCastEvent is not { Sequence: 0, SpellId: 0 }
            || result.ActionBar is not { ReadableCount: 120, OccupiedCount: 2 } actionBar
            || actionBar.GetSlot(4) is not { Kind: VisualActionKind.Spell, Id: 7744 }
            || actionBar.GetSlot(26) is not { Kind: VisualActionKind.Flyout, Id: 270 }
            || result.ActionStates is not
                { ReadableCount: VisualActionBarSnapshot.SlotCount } actionStates
            || actionStates.GetSlot(4) is not
                { Usable: true, NoMana: false, InRange: true }
            || actionStates.GetSlot(1) is not
                { Usable: false, NoMana: false, InRange: null }
            || result.Cooldowns is not
                { ReadableCount: VisualCooldownGridSnapshot.SlotCount } cooldownGrid
            || cooldownGrid.GetSlot(4) is not { } cooldown4 ||
                Math.Abs(cooldown4 - 0.4) > 0.02
            || cooldownGrid.GetSlot(1) != 0
            || result.Bags is not { IsComplete: true } bags ||
                bags.GetBag(0) is not { SlotCount: 16, FreeSlots: 14,
                    Family: 0 } ||
                bags.GetSlot(0, 1) is not { ItemId: 6948, Count: 1 } ||
                bags.GetSlot(0, 2) is not { ItemId: 117, Count: 3 } ||
                bags.GetSlot(0, 3) is not { ItemId: 0, Count: 0 } ||
            result.SpellBook is not { Count: 3, ReadableCount: 3 } spellBook
            || spellBook.GetSlot(1) is not { Source: VisualSpellIdSource.SpellId, Id: 6603 }
            || spellBook.GetSlot(2) is not { Source: VisualSpellIdSource.ActionId, Id: 270 }
            || spellBook.GetSlot(3) is not { Source: VisualSpellIdSource.SpellId, Id: 7744 })
        {
            Console.Error.WriteLine($"Visual reader self-test failed at UI scale {scale}; " +
                $"auras={result.SlotAuras?.ToString() ?? "missing"}.");
            return 1;
        }
        using (SyntheticWowScreen capture = new(Image.LoadPixelData(pixels, width, height)))
        {
            ForeverVisualBarTracker fullScreen = new(capture,
                new StartupClientVersion(new Version(1, 60, 1)));
            fullScreen.Update();
            if (!fullScreen.TryGetLatest(out var captured) ||
                captured.TargetScreenPosition is not { } marker ||
                Math.Abs(marker.X - 780.0 / width) > 0.01 ||
                Math.Abs(marker.Y - 0.4) > 0.01)
            {
                Console.Error.WriteLine($"Full-viewport marker tracking failed at scale {scale}.");
                return 1;
            }
        }

        // The reference and aura textures share scale and rendering. A
        // different icon must not match; a missing reference is unknown. The
        // last slot's reference and the last aura sit at the strip's far edges.
        Bgra32[] auraPixels = new Bgra32[width * height];
        DrawAuraIcon(auraPixels, VisualAuraReader.ReferenceColumn(12), 0, 80);
        DrawAuraIcon(auraPixels, 40 % 21, 40 / 21, 80);
        if (VisualAuraReader.Read(auraPixels, width, height, left, auraTop, scale) is not { } auras ||
            auras.IsActive(12) != true || auras.IsActive(1) is not null)
            return 1;
        DrawAuraIcon(auraPixels, 40 % 21, 40 / 21, 200);
        if (VisualAuraReader.Read(auraPixels, width, height, left, auraTop, scale)?.IsActive(12) != false)
            return 1;
        Array.Clear(auraPixels);
        if (VisualAuraReader.Read(auraPixels, width, height, left, auraTop, scale)?.IsActive(12) is not null ||
            VisualAuraReader.Read(auraPixels, width, height, width - 100, auraTop, scale) is not null)
            return 1;
        // The previous Seal-only layout (state 2) is refused rather than misread.
        DrawCastPixel(28, 2);
        DrawCastPixel(29, (0xA5B5A9 - 2) & 0xFFFFFF);
        if (!VisualBarReader.TryRead(pixels, width, height, out VisualBarSample oldLayout) ||
            oldLayout.SlotAuras is not null)
            return 1;
        DrawCastPixel(28, VisualBarReader.SlotAuraState);
        DrawCastPixel(29, (0xA5B5A9 - VisualBarReader.SlotAuraState) & 0xFFFFFF);

        if (!ForeverFrame.HasControllerInputs(result) ||
            !ForeverFrame.HasTargetIdentity(result) ||
            ForeverFrame.HasTargetIdentity(result with { TargetIdentity = null }) ||
            ForeverFrame.HasControllerInputs(result with { ActionStates = null }) ||
            ForeverFrame.HasControllerInputs(result with { Bags = null }) ||
            ForeverFrame.HasControllerInputs(result with { LastCastEvent = null }) ||
            !ForeverFrame.HasTrialInputs(result with { Bags = null }) ||
            ForeverFrame.HasTrialInputs(result with { ActionStates = null }) ||
            ForeverFrame.HasTrialInputs(result with { LastCastEvent = null }))
        {
            Console.Error.WriteLine($"Forever controller frame gate failed at UI scale {scale}.");
            return 1;
        }
        // Range facts come from the profile: slot 4 holds spell 7744 in range.
        ClassConfiguration noActions = new();
        ClassConfiguration slot4Combat = new();
        slot4Combat.Combat.Sequence = [new KeyAction { Name = "Slot 4", SpellId = 7744, Slot = 4 }];
        if (!ForeverGoapWorldState.TryBuild(result, noActions, out var worldState) ||
            !worldState[1 << (int)GoapKey.hastarget] ||
            !worldState[1 << (int)GoapKey.targetisalive] ||
            !worldState[1 << (int)GoapKey.targethostile] ||
            worldState[1 << (int)GoapKey.incombat] ||
            worldState[1 << (int)GoapKey.withinpullrange] ||
            worldState[1 << (int)GoapKey.incombatrange] ||
            !ForeverGoapWorldState.TryBuild(result, slot4Combat, out var slot4State) ||
            !slot4State[1 << (int)GoapKey.incombatrange] ||
            slot4State[1 << (int)GoapKey.withinpullrange] ||
            !ForeverGoapWorldState.TryBuild(result with { Bags = null }, noActions, out _) ||
            ForeverGoapWorldState.TryBuild(result with { ActionStates = null }, noActions, out _))
        {
            Console.Error.WriteLine($"Forever GOAP state gate failed at UI scale {scale}.");
            return 1;
        }

        DrawBagSlot(0, 2, 117, 3, corruptMarker: true);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var corruptBag) ||
            corruptBag.Bags is not { ReadableSlots: 199, IsComplete: false })
        {
            Console.Error.WriteLine($"Forever bag checksum failed at UI scale {scale}.");
            return 1;
        }
        DrawBagSlot(0, 2, 117, 3);

        DrawWorldPixel(9, 1);
        DrawWorldPixel(10, 0xA56643);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var nonManaPower) || nonManaPower.PlayerPowerType != 1)
        {
            Console.Error.WriteLine($"Forever power type decode failed at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(10, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedPower) || damagedPower.PlayerPowerType is not null)
        {
            Console.Error.WriteLine($"Forever power type checksum failed at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(9, 0);
        DrawWorldPixel(10, 0xA56644);

        DrawWorldPixel(12, 0);
        DrawWorldPixel(14, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedIdentity) ||
            damagedIdentity.PlayerIdentity is not null ||
            damagedIdentity.TargetFlags is not null)
        {
            Console.Error.WriteLine($"Forever identity/target checksum failed at UI scale {scale}.");
            return 1;
        }
        DrawWorldPixel(12, (0xA57744 - identity) & 0xFFFFFF);
        DrawWorldPixel(14, 0xA57755 - 35);
        DrawWorldPixel(13, 32);
        DrawWorldPixel(14, 0xA57755 - 32);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var noTargetFlags) ||
            noTargetFlags.TargetFlags is not
                { Present: false, Attackable: false, Dead: false,
                  PlayerCombat: false, PlayerDead: false })
            return 1;
        DrawWorldPixel(13, 38);
        DrawWorldPixel(14, 0xA57755 - 38);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var impossibleTargetFlags) ||
            impossibleTargetFlags.TargetFlags is not null)
            return 1;
        DrawWorldPixel(13, 3);
        DrawWorldPixel(14, 0xA57755 - 3);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var oldTargetFlags) ||
            oldTargetFlags.TargetFlags is not null)
            return 1;
        DrawWorldPixel(13, 43);
        DrawWorldPixel(14, 0xA57755 - 43);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var combatTargetFlags) ||
            combatTargetFlags.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerCombat: true, PlayerDead: false })
            return 1;
        DrawWorldPixel(13, 51);
        DrawWorldPixel(14, 0xA57755 - 51);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var deadTargetFlags) ||
            deadTargetFlags.TargetFlags is not
                { Present: true, Attackable: true, Dead: false,
                  PlayerCombat: false, PlayerDead: true })
            return 1;
        DrawWorldPixel(13, 35);
        DrawWorldPixel(14, 0xA57755 - 35);
        DrawCastPixel(0, 7);
        DrawCastPixel(1, 20271);
        DrawCastPixel(2, 0xA5CC11 - 7 - 20271);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var successfulCast) ||
            successfulCast.LastCastEvent is not
                { Sequence: 7, SpellId: 20271 } castEvent ||
            !castEvent.ConfirmsNewSuccess(new(6, 635), 20271) ||
            castEvent.ConfirmsNewSuccess(new(7, 635), 20271) ||
            castEvent.ConfirmsNewSuccess(new(6, 635), 635))
            return 1;
        DrawCastPixel(2, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedCast) ||
            damagedCast.LastCastEvent is not null)
            return 1;
        DrawCastPixel(0, 0);
        DrawCastPixel(1, 0);
        DrawCastPixel(2, 0xA5CC11);

        DrawCastPixel(6, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedTargetIdentity) ||
            damagedTargetIdentity.TargetIdentity is not null ||
            ForeverFrame.HasTargetIdentity(damagedTargetIdentity))
            return 1;
        DrawCastPixel(6, (0xA58866 - targetHash1 - targetHash2 - targetLevel * 2) & 0xFFFFFF);

        DrawCastPixel(11, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedTargetPosition) ||
            damagedTargetPosition.TargetWorldPosition is not null)
            return 1;
        DrawCastPixel(11, (0xA59977 - targetX - targetY - targetZ -
            targetHash1 - targetHash2) & 0xFFFFFF);
        DrawCastPixel(15, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var damagedTargetMapPosition) ||
            damagedTargetMapPosition.TargetMapPosition is not null)
            return 1;
        DrawCastPixel(15, (0xA5AA88 - targetMapId - targetMapX - targetMapY -
            targetHash1 - targetHash2) & 0xFFFFFF);

        DrawCastPixel(5, targetLevel * 2 + 1);
        DrawCastPixel(6, (0xA58866 - targetHash1 - targetHash2 - targetLevel * 2 - 1) & 0xFFFFFF);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var playerTargetIdentity) ||
            playerTargetIdentity.TargetIdentity is not { IsPlayer: true, Level: targetLevel })
            return 1;
        DrawCastPixel(5, targetLevel * 2);
        DrawCastPixel(6, (0xA58866 - targetHash1 - targetHash2 - targetLevel * 2) & 0xFFFFFF);

        DrawActionStateSlot(4, 1, 7744, 2, 6);
        if (!VisualBarReader.TryRead(pixels, width, height, out var damagedState) ||
            damagedState.ActionStates is not { ReadableCount: 119 } ||
            damagedState.ActionStates.GetSlot(4) is not null)
        {
            Console.Error.WriteLine($"Forever action-state checksum failed at UI scale {scale}.");
            return 1;
        }
        DrawActionStateSlot(4, 1, 7744, 6);
        DrawActionStateSlot(4, 1, 7744, 10);
        if (!VisualBarReader.TryRead(pixels, width, height, out var outOfRange) ||
            outOfRange.ActionStates?.GetSlot(4) is not
                { Usable: true, InRange: false })
        {
            Console.Error.WriteLine($"Forever out-of-range state failed at UI scale {scale}.");
            return 1;
        }
        DrawActionStateSlot(4, 1, 7744, 6);
        DrawPositionPixel(15, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out var missingState) ||
            missingState.ActionStates is not null)
        {
            Console.Error.WriteLine($"Forever action-state marker failed at UI scale {scale}.");
            return 1;
        }
        DrawPositionPixel(15, 0x5A88A5);
        DrawCooldownSlot(4, 1, 7744, 0.7);
        if (!VisualBarReader.TryRead(pixels, width, height, out var changingCooldown) ||
            changingCooldown.Cooldowns?.GetSlot(4) is not { } changedFill ||
            Math.Abs(changedFill - 0.7) > 0.02)
        {
            Console.Error.WriteLine($"Forever cooldown fill change failed at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0.4);
        DrawEncodedRect(left + (int)Math.Round(3 * 20 * scale),
            top + (int)Math.Round(130 * scale),
            (int)Math.Round(10 * scale), 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out var missingCooldown) ||
            missingCooldown.Cooldowns is not { ReadableCount: 11 } ||
            missingCooldown.Cooldowns.GetSlot(4) is not null)
        {
            Console.Error.WriteLine($"Forever cooldown marker failed at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0.4);

        if (result.Bindings is not
                { ReadableCount: VisualBindingSnapshot.Count } visualBindings ||
            visualBindings.GetCode(21) != ((21 << 14) | (28 << 7)) ||
            visualBindings.GetCode(22) != 0 ||
            visualBindings.GetCode(45) != ((45 << 14) | 52) ||
            !HasTrialBinding(result, 1, ConsoleKey.D1) ||
            HasTrialBinding(result, 1, ConsoleKey.D2) ||
            HasTrialBinding(result, 2, ConsoleKey.D2) ||
            HasTrialBinding(result with { Bindings = null }, 1, ConsoleKey.D1))
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

        string spellName = spells.Spells[7744].Name;
        KeyAction cooldownAction = new() { Name = spellName, Slot = 4,
            SpellId = 7744 };
        var cooldownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CD_Judgement", "CD_Judgement of Wisdom" };
        var auraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Seal of Righteousness", "F_Devotion Aura" };
        if (ForeverCastGate.IsActionReady(result, cooldownAction, spells) ||
            ForeverProfileRules.FindActionCost(
                "Cost_Judgement >= 10") != "Cost_Judgement" ||
            ForeverProfileRules.FindActionCost(
                "Cost < Mana") != "Cost" ||
            ForeverProfileRules.FindActionCost(
                "Health% > 50 && Mana% >= 25") is not null ||
            ForeverProfileRules.FindActionCost(
                "CostumeCount > 0") is not null ||
            ForeverProfileRules.FindCooldownComparison(
                "CD_Judgement <= GCD", cooldownKeys) != "CD_Judgement" ||
            ForeverProfileRules.FindCooldownComparison(
                "CD_Judgement of Wisdom == 0", cooldownKeys) !=
                    "CD_Judgement of Wisdom" ||
            ForeverProfileRules.FindCooldownComparison(
                "TargetHealth% > CD_MIN_TARGET_HP%", cooldownKeys) is not null ||
            ForeverProfileRules.FindUnsupportedVital(
                "Mana% < 30 && Health% < 50") is not null ||
            ForeverProfileRules.FindUnsupportedVital(
                "Mana < 20") != "Mana" ||
            ForeverProfileRules.FindUnsupportedVital(
                "FocusHealth% < 50") != "FocusHealth%" ||
            ForeverProfileRules.FindAura(
                "!Seal of Righteousness", auraKeys) != "Seal of Righteousness" ||
            ForeverProfileRules.FindAura(
                "F_Devotion Aura && Health% > 50", auraKeys) != "F_Devotion Aura" ||
            ForeverProfileRules.FindAura(
                "Mana% > 30", auraKeys) is not null)
        {
            Console.Error.WriteLine($"Forever cooldown comparison audit failed at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var readyCooldown))
            return 1;
        if (!ForeverCastGate.IsActionReady(readyCooldown, cooldownAction, spells) ||
            ForeverCastGate.IsActionReady(readyCooldown with { SpellBook = null },
                cooldownAction, spells) ||
            ForeverCastGate.IsActionReady(readyCooldown, new KeyAction
                { Name = "Wrong spell", Slot = 4 }, spells) ||
            ForeverCastGate.IsActionReady(readyCooldown, new KeyAction
                { Name = spellName, Slot = 4, SpellId = 6603 }, spells) ||
            ForeverCastGate.IsActionReady(readyCooldown, new KeyAction
                { Name = spellName, Slot = 13 }, spells))
        {
            Console.Error.WriteLine($"Forever cooldown readiness failed at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0.4);
        KeyReader.ForeverActionBarProvider = () => actionBar;
        if (KeyReader.ReadKey(NullLogger.Instance,
                new KeyAction { Name = spellName }))
        {
            Console.Error.WriteLine($"Forever action resolution accepted an unbound spell at UI scale {scale}.");
            return 1;
        }
        KeyReader.GameBindings[BindingID.ACTIONBUTTON4] =
            (ConsoleKey.D4, ModifierKey.None);
        KeyAction spellAction = new() { Name = spellName };
        if (!KeyReader.ReadKey(NullLogger.Instance, spellAction) ||
            spellAction.Slot != 4 || spellAction.ConsoleKey != ConsoleKey.D4 ||
            KeyReader.FindForeverSpellSlot(actionBar, spellBookReader,
                spellName, 6603) != 0 ||
            KeyReader.ReadKey(NullLogger.Instance,
                new KeyAction { Name = "Unlearned Spell" }))
        {
            Console.Error.WriteLine($"Forever spell ID resolution failed at UI scale {scale}.");
            return 1;
        }
        DrawActionGridSlot(4, 4, 7744);
        if (!VisualBarReader.TryRead(pixels, width, height, out var flyoutResult) ||
            flyoutResult.ActionBar is not { } flyoutBar ||
            flyoutResult.ActionStates is not { ReadableCount: 119 } ||
            flyoutResult.Cooldowns is not { ReadableCount: 11 } ||
            KeyReader.FindForeverSpellSlot(flyoutBar, spellBookReader,
                spellName) != 0)
        {
            Console.Error.WriteLine($"Forever action resolution treated a flyout as a spell at UI scale {scale}.");
            return 1;
        }
        DrawActionGridSlot(4, 1, 7744);
        KeyReader.ForeverActionBarProvider = () => null;
        if (KeyReader.ReadKey(NullLogger.Instance,
                new KeyAction { Name = spellName }))
        {
            Console.Error.WriteLine($"Forever action resolution accepted a stale bar at UI scale {scale}.");
            return 1;
        }
        KeyReader.ForeverActionBarProvider = null;

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

        DrawPositionPixel(4, 0);
        if (!VisualBarReader.TryRead(pixels, width, height, out result) ||
            result.Position is not null)
        {
            Console.Error.WriteLine($"Visual reader accepted missing position marker at UI scale {scale}.");
            return 1;
        }

        // The normal cast adapter must use the action, binding, readiness,
        // target, and event from one checked capture before sending input.
        DrawPositionPixel(4, (0xA55AA5 - 42 -
            (int)Math.Floor(0.345678 * 16777215) -
            (int)Math.Floor(0.789012 * 16777215) -
            (int)Math.Floor(1.23456 * 100000)) & 0xFFFFFF);
        DrawActionGridSlot(4, 1, 7744);
        DrawActionGridSlot(2, 1, 20271);
        DrawActionStateSlot(2, 1, 20271, 6);
        DrawCooldownSlot(2, 1, 20271, 0);
        DrawSpellGridSlot(3, VisualSpellIdSource.SpellId, 20271);
        DrawBindingSlot(22, (22 << 14) | (29 << 7));
        DrawPositionPixel(7, 0x00FF00);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var trialCastSample) ||
            trialCastSample.SpellBook is not { } trialBook)
            return 1;
        trialCastSample = trialCastSample with
        {
            PlayerHealth = 1,
            PlayerPower = 1,
            TargetHealth = 1,
            Position = new(1420, 0.35, 0.79, 1.23),
            WorldPosition = new(100, 200, 0, 0)
        };
        spellBookReader.UpdateForever(trialBook);
        KeyAction trialJudgement = new()
        {
            Name = "Judgement", SpellId = 20271, Slot = 2,
            ConsoleKey = ConsoleKey.D2
        };
        if (!ForeverCastGate.CanPress(trialCastSample,
                trialJudgement, spells) ||
            !ForeverCastGate.CanPress(trialCastSample with
                { PlayerHealth = 0.70, PlayerPower = 0.25,
                  TargetHealth = 0.50,
                  TargetFlags = new(true, true, false, true, false) },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with
                { PlayerHealth = 0.69, PlayerPower = 1,
                  TargetHealth = 0.50,
                  TargetFlags = new(true, true, false, false, false) },
                trialJudgement, spells) ||
            // Buffs are the rotation's concern; the gate only checks the press.
            !ForeverCastGate.CanPress(trialCastSample with { SlotAuras = null },
                trialJudgement, spells) ||
            // Mana gates apply to mana users only; rage starts empty.
            !ForeverCastGate.CanPress(trialCastSample with
                { PlayerPower = 0, PlayerPowerType = 1 },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with { PlayerPower = 0.59 },
                trialJudgement, spells) ||
            // Only a verified class presses anything.
            ForeverCastGate.CanPress(trialCastSample with
                { PlayerIdentity = new((int)UnitClass.Warrior, 5, 12) },
                trialJudgement, spells) ||
            // A self cast needs no target.
            !ForeverCastGate.CanPress(trialCastSample with
                { TargetIdentity = null, TargetFlags = new(false, false, false, false, false),
                  TargetPresent = false },
                trialJudgement, spells, selfCast: true) ||
            ForeverCastGate.CanPress(trialCastSample with { TargetIdentity = null },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with
                { TargetIdentity = new(targetHash1, targetHash2, 12, true) },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with
                { TargetIdentity = new(targetHash1, targetHash2, 14, false) },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with { Bindings = null },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with { LastCastEvent = null },
                trialJudgement, spells) ||
            ForeverCastGate.CanPress(trialCastSample with
                { TargetFlags = new(true, true, true, false, false) },
                trialJudgement, spells))
        {
            Console.Error.WriteLine($"Forever cast preflight failed at UI scale {scale}.");
            return 1;
        }
        DrawBindingSlot(22, (22 << 14) | (30 << 7));
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var reboundTrial) ||
            ForeverCastGate.CanPress(reboundTrial with
                { PlayerHealth = 1, PlayerPower = 1, TargetHealth = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spells))
        {
            Console.Error.WriteLine($"Forever cast accepted a changed key at UI scale {scale}.");
            return 1;
        }
        DrawBindingSlot(22, (22 << 14) | (29 << 7));
        DrawCooldownSlot(2, 1, 20271, 0.4);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var busyTrial) ||
            ForeverCastGate.CanPress(busyTrial with
                { PlayerHealth = 1, PlayerPower = 1, TargetHealth = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spells))
        {
            Console.Error.WriteLine($"Forever cast accepted a running cooldown at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(2, 1, 20271, 0);
        DrawActionStateSlot(2, 1, 20271, 10);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var distantTrial) ||
            ForeverCastGate.CanPress(distantTrial with
                { PlayerHealth = 1, PlayerPower = 1, TargetHealth = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spells))
        {
            Console.Error.WriteLine($"Forever cast accepted an out-of-range action at UI scale {scale}.");
            return 1;
        }

        // Events are latched independently of the currently selected target.
        DrawWorldPixel(13, 32);
        DrawWorldPixel(14, 0xA57755 - 32);
        DrawPositionPixel(7, 0xFF0000);
        DrawCastPixel(3, 0);
        DrawCastPixel(4, 0);
        DrawCastPixel(5, 0);
        DrawCastPixel(6, 0xA58866);
        VisualTargetIdentity deadTarget = new(targetHash1, targetHash2, targetLevel, false);
        if (!VisualBarReader.TryRead(pixels, width, height, out var afterDeath) ||
            afterDeath.LastDeathEvent is not { } death || !death.Confirms(deadTarget, 6) ||
            death.Confirms(deadTarget, 7) ||
            death.Confirms(deadTarget with { GuidHash2 = targetHash2 + 1 }, 6) ||
            afterDeath.Loot is not { IsOpen: false, InitialSlots: 2, RemainingSlots: 0,
                OpenSequence: 3, CloseSequence: 3, ClearedSequence: 8 })
        {
            Console.Error.WriteLine($"Death after target clearing / completed loot test failed at {scale}.");
            return 1;
        }
        VisualLootState finishedLoot = afterDeath.Loot!.Value;
        VisualLootState priorLoot = finishedLoot with
            { OpenSequence = 2, CloseSequence = 2, ClearedSequence = 6 };
        if (!finishedLoot.CompletedSince(priorLoot) ||
            finishedLoot.CompletedSince(finishedLoot) ||
            (finishedLoot with { RemainingSlots = 1 }).CompletedSince(priorLoot) ||
            (finishedLoot with { IsOpen = true }).CompletedSince(priorLoot) ||
            (finishedLoot with { ClearedSequence = 6 }).CompletedSince(priorLoot))
        {
            Console.Error.WriteLine($"Loot completion accepted stale/partial events at {scale}.");
            return 1;
        }
        DrawLifecycle(205, [7, targetHash1, targetHash2, 0]);
        DrawLifecycle(225, [3, 3, 8, 2, 0, 1, targetHash1, targetHash2, 0]);
        if (!VisualBarReader.TryRead(pixels, width, height, out var corruptEvents) ||
            corruptEvents.LastDeathEvent is not null || corruptEvents.Loot is not null)
        {
            Console.Error.WriteLine($"Corrupt lifecycle events accepted at {scale}.");
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

        void DrawCastPixel(int index, int encoded)
        {
            int xStart = left + (int)Math.Round(index * 20 * scale);
            int yStart = top + (int)Math.Round(190 * scale);
            int side = (int)Math.Round(10 * scale);
            DrawEncodedRect(xStart, yStart, side, encoded);
        }

        // Writes only the pixels the aura matcher samples, a gradient whose
        // blue channel tells icons apart.
        void DrawAuraIcon(Bgra32[] target, int column, int row, byte blue)
        {
            for (int y = 3; y <= 12; y++)
                for (int x = 3; x <= 12; x++)
                    target[(auraTop + (int)Math.Round((row * 18 + y) * scale)) * width +
                        left + (int)Math.Round((column * 18 + x) * scale)] =
                        new Bgra32((byte)(x * 15), (byte)(y * 15), blue);
        }

        void DrawLifecycle(int row, int[] values)
        {
            for (int i = 0; i < values.Length; i++)
                DrawEncodedRect(left + (int)Math.Round((340 + i * 20) * scale),
                    top + (int)Math.Round(row * scale), (int)Math.Round(10 * scale), values[i]);
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

        void DrawActionStateSlot(int slot, int kind, int id, int code,
            int? markerCode = null)
        {
            int column = (slot - 1) % 20;
            int row = (slot - 1) / 20;
            int yStart = top + (int)Math.Round((340 + row * 8) * scale);
            int side = (int)Math.Round(8 * scale);
            DrawEncodedRect(left + (int)Math.Round(column * 16 * scale),
                yStart, side, code);
            DrawEncodedRect(left + (int)Math.Round((column * 16 + 8) * scale),
                yStart, side, (0xA58811 - id - slot - kind -
                    (markerCode ?? code)) & 0xFFFFFF);
        }

        void DrawCooldownSlot(int slot, int kind, int id, double fill)
        {
            DrawEncodedRect(
                left + (int)Math.Round((slot - 1) * 20 * scale),
                top + (int)Math.Round(130 * scale),
                (int)Math.Round(10 * scale),
                (0xA59911 - slot - kind - id) & 0xFFFFFF);
            int yStart = top + (int)Math.Round((400 + (slot - 1) * 8) * scale);
            int barHeight = Math.Max(1, (int)Math.Round(6 * scale));
            int fillWidth = (int)Math.Round(320 * scale * fill);
            for (int y = yStart; y < yStart + barHeight; y++)
                for (int x = left; x < left + barWidth; x++)
                    pixels[y * width + x] = x - left < fillWidth
                        ? new Bgra32 { G = 255, B = 255, A = 255 }
                        : new Bgra32 { A = 255 };
        }

        void DrawBagHeader(int bag, int slots, int free, int family)
        {
            int packed = slots + free * 64;
            int y = top + (int)Math.Round(500 * scale);
            int side = (int)Math.Round(10 * scale);
            DrawEncodedRect(left + (int)Math.Round(bag * 60 * scale),
                y, side, packed);
            DrawEncodedRect(left + (int)Math.Round((bag * 60 + 20) * scale),
                y, side, family);
            DrawEncodedRect(left + (int)Math.Round((bag * 60 + 40) * scale),
                y, side, (0xA5BA11 - bag - packed - family) & 0xFFFFFF);
        }

        void DrawBagSlot(int bag, int slot, int id, int count,
            bool corruptMarker = false)
        {
            int index = bag * VisualBagSnapshot.SlotsPerBag + slot - 1;
            int col = index % 10;
            int row = index / 10;
            int y = top + (int)Math.Round((520 + row * 8) * scale);
            int side = (int)Math.Round(8 * scale);
            DrawEncodedRect(left + (int)Math.Round(col * 30 * scale),
                y, side, id);
            DrawEncodedRect(left + (int)Math.Round((col * 30 + 10) * scale),
                y, side, count);
            DrawEncodedRect(left + (int)Math.Round((col * 30 + 20) * scale),
                y, side, corruptMarker ? 0 :
                    (0xA5BB11 - index - id - count) & 0xFFFFFF);
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
    if (!NameplateSelfTest.Run())
        return 1;
    Console.WriteLine("Visual reader self-test passed at 75%, 100%, and 125% UI scale.");
    return 0;
}
