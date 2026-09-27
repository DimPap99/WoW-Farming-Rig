using Core;

using Game;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SharedLib;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                            $"target={current.TargetHealth:P0}, {TargetText(current.TargetPresent)}, " +
                            $"calibration={current.Calibration:P0}, " +
                            PositionText(current.Position) + ", " +
                            CooldownText(current.ActionCooldownFill) + ", " +
                            ActionText(current.WatchedAction) + ", " +
                            ActionBarText(current.ActionBar));
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
        new Rectangle(0, 0, Math.Min(650, image.Width), Math.Min(350, image.Height))));
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
    Console.WriteLine(TargetText(bars.TargetPresent));
    Console.WriteLine(PositionText(bars.Position));
    Console.WriteLine(CooldownText(bars.ActionCooldownFill));
    Console.WriteLine(ActionText(bars.WatchedAction));
    Console.WriteLine(ActionBarText(bars.ActionBar));
}

static string PositionText(VisualPositionSample? position) => position is { } value
    ? $"map={value.UIMapId}, x={value.MapX:F5}, y={value.MapY:F5}, facing={value.Direction:F2}"
    : "position=unavailable";

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

static string TargetText(bool? present) => present switch
{
    true => "target presence=present",
    false => "target presence=absent",
    null => "target presence=unknown"
};

static async Task<int> RunReaderSmoke(WowScreenWGC screen, ForeverVisualBarTracker tracker)
{
    // This focused test only calls vital getters. The legacy map/area services
    // are intentionally absent, as they are not part of the Forever reader yet.
    PlayerReader player = new(screen, null!, null!, new AddonBits(),
        new SpellInRange(), new Stance(), tracker);

    for (int i = 0; i < 50; i++)
    {
        screen.Update();
        tracker.Update();
        if (player.TryGetForeverVisualVitals(out VisualBarSample bars))
        {
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

            bool absoluteRejected = false;
            try { _ = player.HealthCurrent(); }
            catch (NotSupportedException) { absoluteRejected = true; }

            await Task.Delay(900);
            bool staleRejected = false;
            try { _ = player.HealthPercent(); }
            catch (InvalidOperationException) { staleRejected = true; }
            bool staleActionRejected = !player.TryGetForeverWatchedAction(out _);
            bool staleActionBarRejected = !player.TryGetForeverActionBar(out _);

            Console.WriteLine($"PlayerReader: health={health}%, power={power}%, " +
                $"target={(target is { } value ? value + "%" : "unavailable")}, " +
                $"actionMatches={actionMatches}, actionBarMatches={actionBarMatches}, " +
                $"absoluteRejected={absoluteRejected}, staleRejected={staleRejected}, " +
                $"staleActionRejected={staleActionRejected}, " +
                $"staleActionBarRejected={staleActionBarRejected}");
            return actionMatches && actionBarMatches && absoluteRejected &&
                staleRejected && staleActionRejected && staleActionBarRejected ? 0 : 2;
        }

        await Task.Delay(100);
    }

    Console.Error.WriteLine("No fresh Forever visual sample was captured.");
    return 2;
}

static bool HasChanged(VisualBarSample previous, VisualBarSample current)
    => Math.Abs(previous.PlayerHealth - current.PlayerHealth) >= 0.02
    || Math.Abs(previous.PlayerPower - current.PlayerPower) >= 0.02
    || Math.Abs(previous.TargetHealth - current.TargetHealth) >= 0.02
    || previous.TargetPresent != current.TargetPresent
    || previous.WatchedAction != current.WatchedAction
    || ActionBarChanged(previous.ActionBar, current.ActionBar)
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

static int RunSelfTest()
{
    foreach (double scale in new[] { 0.75, 1.0, 1.25 })
    {
        const int width = 700;
        const int height = 350;
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
        DrawPositionPixel(4, 0xA55AA5);
        DrawPositionPixel(6, 0x00FFFF);
        DrawPositionPixel(7, 0x00FF00);
        DrawPositionPixel(8, 7744);
        DrawPositionPixel(9, 0xA50401);
        DrawPositionPixel(10, 0x5AA55A);
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
            || result.TargetPresent != true
            || result.WatchedAction is not { Slot: 4, Kind: VisualActionKind.Spell, Id: 7744 }
            || result.ActionBar is not { ReadableCount: 120, OccupiedCount: 2 } actionBar
            || actionBar.GetSlot(4) is not { Kind: VisualActionKind.Spell, Id: 7744 }
            || actionBar.GetSlot(26) is not { Kind: VisualActionKind.Flyout, Id: 270 })
        {
            Console.Error.WriteLine($"Visual reader self-test failed at UI scale {scale}.");
            return 1;
        }

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

    Console.WriteLine("Visual reader self-test passed at 75%, 100%, and 125% UI scale.");
    return 0;
}
