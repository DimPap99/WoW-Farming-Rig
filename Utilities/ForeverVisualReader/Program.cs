using Core;
using Core.Database;
using Core.Goals;

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

bool readerSmoke = args.Length == 1 && args[0] == "--reader-smoke";
bool routeProbe = args.Length == 1 && args[0] == "--route-probe";
bool trialCheck = args.Length == 1 && args[0] == "--trial-check";
bool trialMove = args.Length == 1 && args[0] == "--trial-move";
bool trialHeal = args.Length == 1 && args[0] == "--trial-heal";
bool trialJudgement = args.Length == 1 && args[0] == "--trial-judgement";
bool trialJudgementArmed = args.Length == 1 && args[0] == "--trial-judgement-armed";

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
    Console.WriteLine(CooldownText(bars.ActionCooldownFill));
    Console.WriteLine(ActionText(bars.WatchedAction));
    Console.WriteLine(ActionBarText(bars.ActionBar));
    Console.WriteLine(SpellBookText(bars.SpellBook));
    Console.WriteLine(BindingsText(bars.Bindings));
    Console.WriteLine($"last successful player cast={bars.LastCastEvent?.ToString() ?? "unavailable"}");
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
    AddonBits targetBits = new();
    PlayerReader player = new(screen, maps, null!, targetBits,
        new SpellInRange(), new Stance(), tracker);
    targetBits.ForeverTargetFlagsProvider = () =>
        player.TryGetForeverVisualVitals(out var latest) &&
        latest.TargetFlags is { } flags &&
        latest.TargetPresent == flags.Present ? flags : null;

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
            int? mana = bars.PlayerPowerType == 0
                ? player.ManaPercent() : null;
            int? target = bars.TargetPresent == true
                ? player.TargetHealthPercent() : null;
            bool actionMatches = bars.WatchedAction is not { } watched ||
                (player.TryGetForeverWatchedAction(out VisualActionSample action) &&
                action == watched);
            bool actionBarMatches = bars.ActionBar is not { } liveBar ||
                (player.TryGetForeverActionBar(out VisualActionBarSnapshot actionBar) &&
                ReferenceEquals(actionBar, liveBar));
            VisualActionStateSnapshot? liveStates = bars.ActionStates;
            bool actionStatesMatch = liveStates is
                    { ReadableCount: VisualActionBarSnapshot.SlotCount } &&
                player.TryGetForeverActionStates(out var states) &&
                ReferenceEquals(states, liveStates);
            bool cooldownGridMatches = bars.Cooldowns is
                    { ReadableCount: VisualCooldownGridSnapshot.SlotCount } liveCooldowns &&
                player.TryGetForeverCooldowns(out var cooldowns) &&
                ReferenceEquals(cooldowns, liveCooldowns);
            VisualBagSnapshot? liveBags = bars.Bags;
            bool bagGridMatches = liveBags is { IsComplete: true } &&
                player.TryGetForeverBags(out var currentBags) &&
                ReferenceEquals(currentBags, liveBags);
            int bagItemCount = 0;
            int regularFreeSlots = 0;
            bool bagReaderMatches = false;
            ItemDB liveItemDB = new(config, new IconDB(
                NullLogger<IconDB>.Instance, config, spellBook.SpellDB));
            using BagReader liveBagReader = new(liveItemDB,
                new EquipmentReader(liveItemDB));
            liveBagReader.ForeverBagsProvider = () =>
                player.TryGetForeverBags(out var latest) ? latest : null;
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
                liveBagReader.UpdateForever(liveBags);
                bagReaderMatches = liveBagReader.BagItemCount() == bagItemCount &&
                    liveBagReader.BagsFull() == (regularFreeSlots == 0);
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
            ActionBarBits<IUsableAction>.ForeverStatesProvider =
                () => player.TryGetForeverActionStates(out var latest)
                    ? latest : null;
            ActionBarBits<IUsableAction> usableReader = new(1);
            bool usableReaderMatches = actionStatesMatch &&
                usableReader.Count == usableActionCount &&
                usableReader.Any == (usableActionCount > 0);
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
            bool controllerFrameReady =
                AddonReader.HasForeverControllerFrame(bars);
            bool targetFlagsMatch = bars.TargetFlags is { } flags &&
                flags.Present == bars.TargetPresent;
            bool castEventMatches = bars.LastCastEvent is { } castEvent &&
                player.TryGetForeverCastEvent(out VisualCastEvent readerCast) &&
                readerCast == castEvent;
            bool targetBitsMatch = bars.TargetFlags is { } targetFlags &&
                targetFlagsMatch &&
                targetBits.Target() == targetFlags.Present &&
                targetBits.NoTarget() == !targetFlags.Present &&
                targetBits.Target_Dead() ==
                    (targetFlags.Present && targetFlags.Dead) &&
                targetBits.Target_Hostile() ==
                    (targetFlags.Present && targetFlags.Attackable &&
                        !targetFlags.Dead) &&
                targetBits.Combat() == targetFlags.PlayerCombat &&
                targetBits.Dead() == targetFlags.PlayerDead;
            bool manaMatches = powerTypeReadable &&
                (bars.PlayerPowerType != 0 || mana == power);
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

            KeyReader.ForeverActionBarProvider =
                () => player.TryGetForeverActionBar(out var live) ? live : null;
            ActionBarSlotValidator slotValidator = new(
                NullLogger<ActionBarSlotValidator>.Instance, null!, null!,
                player, spellBook);
            bool spellActionResolved = false;
            bool spellSlotValidated = false;
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
                        spellSlotValidated = slotValidator.Validate(candidate) ==
                            SlotValidationStatus.Valid &&
                            slotValidator.Validate(new KeyAction
                                { Name = "Unlearned Spell", Slot = candidate.Slot }) ==
                            SlotValidationStatus.Mismatch;
                        resolvedSpellName = spell.Name;
                        resolvedSpellAction = candidate;
                        break;
                    }
                }

            ActionBarCooldownReader cooldownReader = new()
            {
                ForeverSampleProvider = () =>
                    player.TryGetForeverVisualVitals(out var latest)
                        ? latest : null
            };
            bool liveReadinessMatches = resolvedSpellAction is { } known &&
                bars.Cooldowns?.GetSlot(known.Slot) is { } fill &&
                bars.ActionStates?.GetSlot(known.Slot) is { } cooldownState &&
                cooldownReader.IsForeverReady(known, spellBook) ==
                    (fill == 0 && cooldownState.Usable &&
                        !cooldownState.NoMana);

            bool absoluteRejected = false;
            try { _ = player.HealthCurrent(); }
            catch (NotSupportedException) { absoluteRejected = true; }

            await Task.Delay(900);
            bool staleRejected = false;
            try { _ = player.HealthPercent(); }
            catch (InvalidOperationException) { staleRejected = true; }
            bool staleActionRejected = !player.TryGetForeverWatchedAction(out _);
            bool staleActionBarRejected = !player.TryGetForeverActionBar(out _);
            bool staleActionStatesRejected =
                !player.TryGetForeverActionStates(out _);
            bool staleCooldownGridRejected =
                !player.TryGetForeverCooldowns(out _);
            bool staleBagGridRejected = !player.TryGetForeverBags(out _);
            bool staleCastEventRejected = !player.TryGetForeverCastEvent(out _);
            bool staleBagReaderRejected = false;
            try { _ = liveBagReader.BagItemCount(); }
            catch (InvalidOperationException) { staleBagReaderRejected = true; }
            bool staleCooldownReadinessRejected =
                resolvedSpellAction is { } staleSpell &&
                !cooldownReader.IsForeverReady(staleSpell, spellBook);
            bool staleUsableReaderReset = usableReader.Count == 0 &&
                !usableReader.Any && !usableReader.IsSlotUsable(1);
            ActionBarBits<IUsableAction>.ForeverStatesProvider = null;
            bool staleSpellActionRejected = resolvedSpellName is { } name &&
                !KeyReader.ReadKey(NullLogger.Instance,
                    new KeyAction { Name = name });
            bool staleSlotValidationUnavailable = resolvedSpellName is { } staleName &&
                slotValidator.Validate(new KeyAction
                    { Name = staleName, Slot = 1 }) ==
                SlotValidationStatus.Unavailable;
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
            bool stalePlayerFlagsRejected = false;
            try { _ = targetBits.Combat(); }
            catch (InvalidOperationException) { stalePlayerFlagsRejected = true; }
            try { _ = targetBits.Dead(); stalePlayerFlagsRejected = false; }
            catch (InvalidOperationException) { }

            Console.WriteLine($"PlayerReader: health={health}%, power={power}%, " +
                $"powerType={bars.PlayerPowerType?.ToString() ?? "missing"}, " +
                $"powerTypeReadable={powerTypeReadable}, " +
                $"identity={bars.PlayerIdentity?.ToString() ?? "missing"}, " +
                $"identityMatches={identityMatches}, " +
                $"trialFactionMatches={trialFactionMatches}, " +
                $"controllerFrameReady={controllerFrameReady}, " +
                $"targetFlags={bars.TargetFlags?.ToString() ?? "missing"}, " +
                $"targetFlagsMatch={targetFlagsMatch}, " +
                $"targetBitsMatch={targetBitsMatch}, " +
                $"stalePlayerFlagsRejected={stalePlayerFlagsRejected}, " +
                $"castEvent={bars.LastCastEvent?.ToString() ?? "missing"}, " +
                $"castEventMatches={castEventMatches}, " +
                $"staleCastEventRejected={staleCastEventRejected}, " +
                $"mana={(mana is { } mp ? mp + "%" : "unavailable")}, " +
                $"manaMatches={manaMatches}, " +
                $"staleManaRejected={staleManaRejected}, " +
                $"target={(target is { } value ? value + "%" : "unavailable")}, " +
                $"actionMatches={actionMatches}, actionBarMatches={actionBarMatches}, " +
                $"actionStates={liveStates?.ReadableCount.ToString() ?? "missing"}/120, " +
                $"actionStatesMatch={actionStatesMatch}, usableActionCount={usableActionCount}, " +
                $"cooldowns={bars.Cooldowns?.ReadableCount.ToString() ?? "missing"}/12, " +
                $"cooldownSlot1={bars.Cooldowns?.GetSlot(1)?.ToString("P0") ?? "missing"}, " +
                $"cooldownSlot4={bars.Cooldowns?.GetSlot(4)?.ToString("P0") ?? "missing"}, " +
                $"cooldownGridMatches={cooldownGridMatches}, " +
                $"bags={bars.Bags?.ReadableHeaders.ToString() ?? "missing"}/5, " +
                $"bagSlots={bars.Bags?.ReadableSlots.ToString() ?? "missing"}/200, " +
                $"bagItemCount={bagItemCount}, regularFreeSlots={regularFreeSlots}, " +
                $"bagGridMatches={bagGridMatches}, bagReaderMatches={bagReaderMatches}, " +
                $"staleBagGridRejected={staleBagGridRejected}, " +
                $"staleBagReaderRejected={staleBagReaderRejected}, " +
                $"staleCooldownGridRejected={staleCooldownGridRejected}, " +
                $"liveReadinessMatches={liveReadinessMatches}, " +
                $"staleCooldownReadinessRejected={staleCooldownReadinessRejected}, " +
                $"inRangeActionCount={inRangeActionCount}, " +
                $"outOfRangeActionCount={outOfRangeActionCount}, " +
                $"unknownRangeActionCount={unknownRangeActionCount}, " +
                $"usableReaderMatches={usableReaderMatches}, " +
                $"staleUsableReaderReset={staleUsableReaderReset}, " +
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
                $"spellActionResolved={spellActionResolved}, " +
                $"spellSlotValidated={spellSlotValidated}, " +
                $"staleSpellActionRejected={staleSpellActionRejected}, " +
                $"staleSlotValidationUnavailable={staleSlotValidationUnavailable}, " +
                $"staleBindingsRejected={staleBindingsRejected}, " +
                $"staleBindingReaderReset={staleBindingReaderReset}, " +
                $"positionMatches={positionMatches}, " +
                $"stalePositionRejected={stalePositionRejected}, " +
                $"staleMapIdCleared={staleMapIdCleared}, " +
                $"absoluteRejected={absoluteRejected}, staleRejected={staleRejected}, " +
                $"staleActionRejected={staleActionRejected}, " +
                $"staleActionBarRejected={staleActionBarRejected}");
            return actionMatches && actionBarMatches && actionStatesMatch &&
                usableReaderMatches && staleUsableReaderReset &&
                staleActionStatesRejected && cooldownGridMatches &&
                bagGridMatches && bagReaderMatches && staleBagGridRejected &&
                staleBagReaderRejected &&
                staleCooldownGridRejected && liveReadinessMatches &&
                staleCooldownReadinessRejected && maximaMatch &&
                powerTypeReadable && manaMatches && identityMatches &&
                trialFactionMatches && controllerFrameReady &&
                targetFlagsMatch && targetBitsMatch &&
                stalePlayerFlagsRejected &&
                castEventMatches && staleCastEventRejected &&
                worldPositionMatches && staleWorldPositionRejected &&
                spellBookMatches && staleSpellBookRejected &&
                staleSpellBookReaderReset &&
                bindingMatches && spellActionResolved && spellSlotValidated &&
                staleSpellActionRejected && staleSlotValidationUnavailable &&
                staleBindingsRejected &&
                staleBindingReaderReset &&
                positionMatches && stalePositionRejected && staleMapIdCleared &&
                staleMaximumRejected && staleManaRejected &&
                absoluteRejected && staleRejected &&
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

static async Task<int> RunTrialCheck(WowScreenWGC screen,
    ForeverVisualBarTracker tracker, bool requireNearRoute = true)
{
    // Read-only gate for the candidate profile. No controller or input service
    // is constructed here; every value comes from one fresh visual capture.
    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    string profileFile = Path.Combine(config.Class, "Paladin_Forever_Trial.json");
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
    bool profileSafe = profile.Paths.Length == 1 &&
        profile.Paths[0].UIMapId == 1420 &&
        profile.Paths[0].PathFilename == "Forever_Tirisfal_LocalTrial.json" &&
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
            bars.Bags is not { IsComplete: true } || bars.PlayerPowerType != 0 ||
            bars.PlayerIdentity is not { ClassId: (int)UnitClass.Paladin } ||
            bars.TargetFlags is not { } targetFlags ||
            bars.LastCastEvent is null ||
            targetFlags.Present != bars.TargetPresent ||
            targetFlags.PlayerCombat || targetFlags.PlayerDead)
        {
            Console.Error.WriteLine("Trial data is incomplete or the character left Tirisfal.");
            return 2;
        }

        SpellBookReader spellBook = new(new SpellDB(config));
        spellBook.UpdateForever(book);
        bool actionsMatch = spellBook.IsInitialized && spellBook.AllRanksReceived;
        foreach (var expectedAction in expected)
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
            actionsMatch &= ActionBarCooldownReader.IsForeverReady(
                bars, trialAction, spellBook) ==
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
        Vector3 mappedPlayer = WorldMapAreaDB.ToWorld_FlipXY(
            new Vector3((float)(map.MapX * 100), (float)(map.MapY * 100), 0), area);
        float mapError = Vector2.Distance(new(mappedPlayer.X, mappedPlayer.Y),
            new((float)world.X, (float)world.Y));
        if (mapError > 3f)
        {
            await Task.Delay(100);
            continue;
        }

        string routeFile = Path.Combine(config.Path, profile.Paths[0].PathFilename);
        Vector3[] routeMap = JsonConvert.DeserializeObject<Vector3[]>(
            File.ReadAllText(routeFile)) ?? [];
        if (routeMap.Length != 2)
        {
            Console.Error.WriteLine("Trial route must have exactly two anchors.");
            return 2;
        }
        Vector3[] routeWorld = routeMap.Select(p =>
            WorldMapAreaDB.ToWorld_FlipXY(p, area)).ToArray();
        float startDistance = Vector2.Distance(
            new((float)world.X, (float)world.Y),
            new(routeWorld[0].X, routeWorld[0].Y));
        NavmeshBakeOptions bake = new();
        string version = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        string hash = NavmeshSettings.ComputeSettingsHash("forever", version,
            bake.AgentRadius, bake.AgentMaxClimb, bake.WalkableSlope);
        string cache = Path.Combine(config.Navmesh, area.Continent, hash);
        if (!Directory.Exists(cache))
        {
            Console.Error.WriteLine($"Trial navmesh cache is missing: {cache}");
            return 2;
        }
        using NavmeshPathfinder nav = new(NullLogger.Instance, null, cache,
            bake, new NavmeshQueryOptions());
        bool firstHeightFound = nav.TryGetHeight(
            routeWorld[0].X, routeWorld[0].Y, out float startZ);
        bool lastHeightFound = nav.TryGetHeight(
            routeWorld[1].X, routeWorld[1].Y, out float endZ);
        bool heightFound = firstHeightFound && lastHeightFound;
        if (!heightFound)
        {
            Console.Error.WriteLine("Trial route has no walkable mesh height.");
            return 2;
        }
        var path = nav.FindPath(
            new(routeWorld[0].X, routeWorld[0].Y, startZ),
            new(routeWorld[1].X, routeWorld[1].Y, endZ), null);
        bool routeConnected = path is { locations.Count: >= 2 } &&
            nav.LastStats.TilesBaked == 0 &&
            Vector2.Distance(new(path.locations[^1].X, path.locations[^1].Y),
                new(routeWorld[1].X, routeWorld[1].Y)) < 5;
        Console.WriteLine($"Trial: profileSafe={profileSafe}, actionsMatch={actionsMatch}, " +
            $"mapError={mapError:F2} yd, routeConnected={routeConnected}, " +
            $"routeStartDistance={startDistance:F1} yd, " +
            $"health={bars.PlayerHealth:P0}, mana={bars.PlayerPower:P0}, " +
            $"target={bars.TargetPresent}, attackable={targetFlags.Attackable}, " +
            $"targetDead={targetFlags.Dead}, " +
            $"identity={bars.PlayerIdentity}, bagSlots={bars.Bags.ReadableSlots}/200");
        return routeConnected && (!requireNearRoute || startDistance <= 30)
            ? 0 : 2;
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
        first.PlayerHealth < 0.95 || current.PlayerHealth < 0.95 ||
        first.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        current.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        current.TargetPresent != false ||
        startMap.UIMapId != 1420 || startWorld.InstanceMapId != 0 ||
        Vector2.Distance(new((float)firstWorld.X, (float)firstWorld.Y),
            new((float)startWorld.X, (float)startWorld.Y)) > 0.3f)
    {
        Console.Error.WriteLine("Movement trial refused: the character is moving, targeted, or below 95% health.");
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
        observed.PlayerHealth < 0.90)
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
        before.PlayerHealth < 0.95 || before.PlayerPower < 0.60 ||
        before.TargetFlags is not
            { Present: false, PlayerCombat: false, PlayerDead: false } ||
        before.SpellBook is not { } visualBook ||
        before.LastCastEvent is not { } beforeCast)
    {
        Console.Error.WriteLine("Heal trial refused: full health, mana, no target, or spellbook check failed.");
        return 2;
    }

    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBook = new(new SpellDB(config));
    spellBook.UpdateForever(visualBook);
    KeyAction heal = new()
        { Name = "Holy Light", SpellId = 635, Slot = 1,
          ConsoleKey = ConsoleKey.D1 };
    if (!ForeverCastGate.CanPress(before, heal, spellBook))
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
    screen.Update();
    tracker.Update();
    if (!tracker.TryGetLatest(out VisualBarSample before))
    {
        Console.Error.WriteLine("Judgement trial refused: no fresh visual sample.");
        return 2;
    }
    if (before.PlayerHealth < 0.95 || before.PlayerPower < 0.60)
    {
        Console.Error.WriteLine($"Judgement trial refused: health={before.PlayerHealth:P0}, mana={before.PlayerPower:P0}; need at least 95% and 60%.");
        return 2;
    }
    if (before.TargetFlags is not
            { Present: true, Attackable: true, Dead: false,
              PlayerCombat: false, PlayerDead: false } ||
        before.TargetHealth <= 0 ||
        before.LastCastEvent is null)
    {
        Console.Error.WriteLine($"Judgement trial refused: target={before.TargetFlags}; need a living attackable target.");
        return 2;
    }
    if (before.ActionStates?.GetSlot(2) is not { InRange: true } ||
        before.SpellBook is not { } visualBook)
    {
        Console.Error.WriteLine("Judgement trial refused: range or spellbook is unreadable.");
        return 2;
    }

    DataConfig config = new() { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBook = new(new SpellDB(config));
    spellBook.UpdateForever(visualBook);
    KeyAction judgement = new()
        { Name = "Judgement", SpellId = 20271, Slot = 2,
          ConsoleKey = ConsoleKey.D2 };
    if (!ForeverCastGate.CanPress(before, judgement, spellBook))
    {
        Console.Error.WriteLine("Judgement trial refused: the verified action is not ready.");
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
    for (int i = 0; i < 30; i++)
    {
        screen.Update();
        tracker.Update();
        if (tracker.TryGetLatest(out VisualBarSample after))
        {
            flagsReadable &= after.TargetFlags is not null;
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
        $"targetFlagsReadable={flagsReadable}; finish the fight manually.");
    return observedAction && flagsReadable ? 0 : 2;
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
    || previous.LastCastEvent != current.LastCastEvent
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
    DataConfig dbc = new()
        { Root = Path.GetFullPath("Json"), Exp = "forever" };
    SpellBookReader spellBookReader = new(new SpellDB(dbc));
    KeyBindingsReader bindingReader = new(NullLogger<KeyBindingsReader>.Instance);
    ItemDB itemDB = new(dbc, new IconDB(NullLogger<IconDB>.Instance,
        dbc, spellBookReader.SpellDB));
    using BagReader bagReader = new(itemDB, new EquipmentReader(itemDB));
    foreach (double scale in new[] { 0.75, 1.0, 1.25 })
    {
        const int width = 700;
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
            || result.LastCastEvent is not { Sequence: 0, SpellId: 0 }
            || result.WatchedAction is not { Slot: 4, Kind: VisualActionKind.Spell, Id: 7744 }
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
            Console.Error.WriteLine($"Visual reader self-test failed at UI scale {scale}.");
            return 1;
        }
        if (!AddonReader.HasForeverControllerFrame(result) ||
            AddonReader.HasForeverControllerFrame(result with { ActionStates = null }) ||
            AddonReader.HasForeverControllerFrame(result with { Bags = null }) ||
            AddonReader.HasForeverControllerFrame(result with { LastCastEvent = null }))
        {
            Console.Error.WriteLine($"Forever controller frame gate failed at UI scale {scale}.");
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
        VisualBagSnapshot? bagForProvider = bags;
        bagReader.ForeverBagsProvider = () => bagForProvider;
        bagReader.UpdateForever(bags);
        if (bagReader.BagItemCount() != 2 || bagReader.BagsFull() ||
            bagReader.ItemCount(117) != 3 ||
            bagReader.Bags[0].FreeSlot != 14 ||
            bagReader.BagItems.Any(item => item.IsTradable))
        {
            Console.Error.WriteLine($"Forever bag adapter failed at UI scale {scale}.");
            return 1;
        }
        bagForProvider = null;
        bool staleBagRejected = false;
        try { _ = bagReader.ItemCount(117); }
        catch (InvalidOperationException) { staleBagRejected = true; }
        if (!staleBagRejected)
        {
            Console.Error.WriteLine($"Forever stale bag accepted at UI scale {scale}.");
            return 1;
        }
        bagForProvider = bags;

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

        VisualTargetFlags? targetForBits = result.TargetFlags;
        AddonBits targetBits = new()
        {
            ForeverTargetFlagsProvider = () => targetForBits
        };
        if (!targetBits.Target() || !targetBits.Target_Alive() ||
            !targetBits.Target_Hostile() || targetBits.NoTarget() ||
            targetBits.Target_Dead() || targetBits.Combat() ||
            targetBits.Dead())
            return 1;
        targetForBits = combatTargetFlags.TargetFlags;
        if (!targetBits.Combat() || targetBits.Dead())
            return 1;
        targetForBits = deadTargetFlags.TargetFlags;
        if (targetBits.Combat() || !targetBits.Dead())
            return 1;
        targetForBits = null;
        bool stalePlayerFlagsRejected = false;
        try { _ = targetBits.Combat(); }
        catch (InvalidOperationException) { stalePlayerFlagsRejected = true; }
        try { _ = targetBits.Dead(); stalePlayerFlagsRejected = false; }
        catch (InvalidOperationException) { }
        if (!stalePlayerFlagsRejected || targetBits.Target() || targetBits.Target_Alive() ||
            targetBits.Target_Hostile() || targetBits.NoTarget())
            return 1;
        targetForBits = new(false, false, false, false, false);
        if (!targetBits.NoTarget() || targetBits.Target())
            return 1;

        ActionBarBits<IUsableAction>.ForeverStatesProvider =
            () => result.ActionStates;
        ActionBarBits<IUsableAction> usableBits = new(1);
        if (!usableBits.IsSlotUsable(4) || usableBits.IsSlotUsable(1) ||
            !usableBits.Any || usableBits.Count != 1)
        {
            Console.Error.WriteLine($"Forever usable action adapter failed at UI scale {scale}.");
            return 1;
        }
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
        ActionBarBits<IUsableAction>.ForeverStatesProvider = () => null;
        if (usableBits.IsSlotUsable(4) || usableBits.Any || usableBits.Count != 0)
        {
            Console.Error.WriteLine($"Forever usable actions survived stale data at UI scale {scale}.");
            return 1;
        }
        ActionBarBits<IUsableAction>.ForeverStatesProvider = null;
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

        string spellName = spellBookReader.SpellDB.Spells[7744].Name;
        ActionBarCooldownReader foreverCooldown = new();
        VisualBarSample? cooldownSample = result;
        foreverCooldown.ForeverSampleProvider = () => cooldownSample;
        KeyAction cooldownAction = new() { Name = spellName, Slot = 4,
            SpellId = 7744 };
        var cooldownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CD_Judgement", "CD_Judgement of Wisdom" };
        var auraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Seal of Righteousness", "F_Devotion Aura" };
        bool millisecondsRejected = false;
        try { _ = foreverCooldown.Get(cooldownAction); }
        catch (NotSupportedException) { millisecondsRejected = true; }
        ActionBarCostReader foreverCosts = new(
            NullLogger<ActionBarCostReader>.Instance)
        {
            RejectForeverCosts = true
        };
        bool numericCostRejected = false;
        try { _ = foreverCosts.Get(cooldownAction); }
        catch (NotSupportedException) { numericCostRejected = true; }
        if (foreverCooldown.IsForeverReady(cooldownAction, spellBookReader) ||
            !millisecondsRejected ||
            !numericCostRejected ||
            RequirementFactory.FindUnsupportedForeverActionCost(
                "Cost_Judgement >= 10") != "Cost_Judgement" ||
            RequirementFactory.FindUnsupportedForeverActionCost(
                "Cost < Mana") != "Cost" ||
            RequirementFactory.FindUnsupportedForeverActionCost(
                "Health% > 50 && Mana% >= 25") is not null ||
            RequirementFactory.FindUnsupportedForeverActionCost(
                "CostumeCount > 0") is not null ||
            RequirementFactory.FindUnsupportedForeverCooldownComparison(
                "CD_Judgement <= GCD", cooldownKeys) != "CD_Judgement" ||
            RequirementFactory.FindUnsupportedForeverCooldownComparison(
                "CD_Judgement of Wisdom == 0", cooldownKeys) !=
                    "CD_Judgement of Wisdom" ||
            RequirementFactory.FindUnsupportedForeverCooldownComparison(
                "TargetHealth% > CD_MIN_TARGET_HP%", cooldownKeys) is not null ||
            RequirementFactory.FindUnsupportedForeverVital(
                "Mana% < 30 && Health% < 50") is not null ||
            RequirementFactory.FindUnsupportedForeverVital(
                "Mana < 20") != "Mana" ||
            RequirementFactory.FindUnsupportedForeverVital(
                "FocusHealth% < 50") != "FocusHealth%" ||
            RequirementFactory.FindUnsupportedForeverAura(
                "!Seal of Righteousness", auraKeys) != "Seal of Righteousness" ||
            RequirementFactory.FindUnsupportedForeverAura(
                "F_Devotion Aura && Health% > 50", auraKeys) != "F_Devotion Aura" ||
            RequirementFactory.FindUnsupportedForeverAura(
                "Mana% > 30", auraKeys) is not null)
        {
            Console.Error.WriteLine($"Forever cooldown comparison audit failed at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var readyCooldown))
            return 1;
        cooldownSample = readyCooldown;
        if (!foreverCooldown.IsForeverReady(cooldownAction, spellBookReader) ||
            !ActionBarCooldownReader.IsForeverReady(
                readyCooldown, cooldownAction, spellBookReader) ||
            ActionBarCooldownReader.IsForeverReady(
                readyCooldown with { SpellBook = null },
                cooldownAction, spellBookReader) ||
            foreverCooldown.IsForeverReady(new KeyAction
                { Name = "Wrong spell", Slot = 4 }, spellBookReader) ||
            foreverCooldown.IsForeverReady(new KeyAction
                { Name = spellName, Slot = 4, SpellId = 6603 }, spellBookReader) ||
            foreverCooldown.IsForeverReady(new KeyAction
                { Name = spellName, Slot = 13 }, spellBookReader))
        {
            Console.Error.WriteLine($"Forever cooldown readiness failed at UI scale {scale}.");
            return 1;
        }
        cooldownSample = null;
        if (foreverCooldown.IsForeverReady(cooldownAction, spellBookReader))
        {
            Console.Error.WriteLine($"Forever stale cooldown accepted at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(4, 1, 7744, 0.4);
        if (ActionBarSlotValidator.ValidateForeverAction(
                new KeyAction { Name = spellName, Slot = 4 }, 4,
                actionBar, spellBookReader) != SlotValidationStatus.Valid ||
            ActionBarSlotValidator.ValidateForeverAction(
                new KeyAction { Name = spellName, Slot = 1 }, 1,
                actionBar, spellBookReader) != SlotValidationStatus.EmptySlot ||
            ActionBarSlotValidator.ValidateForeverAction(
                new KeyAction { Name = spellName, Slot = 26 }, 26,
                actionBar, spellBookReader) != SlotValidationStatus.Mismatch ||
            ActionBarSlotValidator.ValidateForeverAction(
                new KeyAction { Name = "Unlearned Spell", Slot = 4 }, 4,
                actionBar, spellBookReader) != SlotValidationStatus.Mismatch)
        {
            Console.Error.WriteLine($"Forever action slot validation failed at UI scale {scale}.");
            return 1;
        }
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
                trialJudgement, spellBookReader) ||
            ForeverCastGate.CanPress(trialCastSample with { Bindings = null },
                trialJudgement, spellBookReader) ||
            ForeverCastGate.CanPress(trialCastSample with { LastCastEvent = null },
                trialJudgement, spellBookReader) ||
            ForeverCastGate.CanPress(trialCastSample with
                { TargetFlags = new(true, true, true, false, false) },
                trialJudgement, spellBookReader))
        {
            Console.Error.WriteLine($"Forever cast preflight failed at UI scale {scale}.");
            return 1;
        }
        DrawBindingSlot(22, (22 << 14) | (30 << 7));
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var reboundTrial) ||
            ForeverCastGate.CanPress(reboundTrial with
                { PlayerHealth = 1, PlayerPower = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spellBookReader))
        {
            Console.Error.WriteLine($"Forever cast accepted a changed key at UI scale {scale}.");
            return 1;
        }
        DrawBindingSlot(22, (22 << 14) | (29 << 7));
        DrawCooldownSlot(2, 1, 20271, 0.4);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var busyTrial) ||
            ForeverCastGate.CanPress(busyTrial with
                { PlayerHealth = 1, PlayerPower = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spellBookReader))
        {
            Console.Error.WriteLine($"Forever cast accepted a running cooldown at UI scale {scale}.");
            return 1;
        }
        DrawCooldownSlot(2, 1, 20271, 0);
        DrawActionStateSlot(2, 1, 20271, 10);
        if (!VisualBarReader.TryRead(pixels, width, height,
                out var distantTrial) ||
            ForeverCastGate.CanPress(distantTrial with
                { PlayerHealth = 1, PlayerPower = 1,
                  Position = trialCastSample.Position,
                  WorldPosition = trialCastSample.WorldPosition },
                trialJudgement, spellBookReader))
        {
            Console.Error.WriteLine($"Forever cast accepted an out-of-range action at UI scale {scale}.");
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
    Console.WriteLine("Visual reader self-test passed at 75%, 100%, and 125% UI scale.");
    return 0;
}
