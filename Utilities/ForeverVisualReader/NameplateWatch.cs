using Core;

using Game;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ForeverVisualReader;

/// <summary>Read-only live view of the nameplate roster and its markers.</summary>
internal static class NameplateWatch
{
    private const int Seconds = 60;

    /// <summary>Prints the roster as it changes and labels each marker on screen; sends no input.</summary>
    public static async Task<int> Run(IWowScreen screen, ForeverVisualBarTracker tracker, IntPtr gameWindow)
    {
        Console.WriteLine($"Watching the nameplate roster for {Seconds} seconds; no game input is sent.");
        using MarkerCoordinateOverlay overlay = new(gameWindow);
        string last = string.Empty;
        bool observed = false;
        bool savedMiss = false;
        List<(float X, float Y, string Text)> labels = [];
        for (int i = 0; i < Seconds * 5; i++)
        {
            screen.Update();
            tracker.Update();
            labels.Clear();
            string line;
            if (tracker.TryGetLatest(out VisualBarSample bars) && bars.Nameplates is { } roster)
            {
                line = Describe(roster);
                bool missing = false;
                for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
                {
                    if (roster.GetSlot(slot) is not { IsEmpty: false } plate)
                        continue;
                    if (plate.Screen is { } point)
                    {
                        labels.Add(((float)(point.X * screen.ScreenImage.Width),
                            (float)((1 - point.Y) * screen.ScreenImage.Height), Label(plate)));
                        observed = true;
                    }
                    else if (plate.BehindCamera == false)
                        missing = true;
                }
                // The first frame where a plate in front of the camera has no
                // marker is kept, to see what covered or broke the marker.
                if (missing && observed && !savedMiss)
                {
                    savedMiss = true;
                    SaveCapture(screen);
                }
            }
            else
                line = "nameplate roster unavailable";
            overlay.Update($"{labels.Count} markers", [.. labels]);
            if (line != last)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
                last = line;
            }
            await Task.Delay(200);
        }
        return observed ? 0 : 2;
    }

    /// <summary>The roster on one line: counts, settings, then each occupied slot.</summary>
    public static string Describe(VisualNameplateRoster? roster)
    {
        if (roster is null)
            return "nameplates=unavailable";
        StringBuilder text = new();
        text.Append($"nameplates={roster.PlateCount} ({roster.AttackableCount} attackable, " +
            $"{roster.FriendlyPlayerCount} friendly players), shown={roster.PlatesShown}, " +
            $"max={roster.MaxDistance}yd, rows={roster.ReadableCount}/{VisualNameplateRoster.SlotCount}");
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            VisualNameplate? entry = roster.GetSlot(slot);
            if (entry is { IsEmpty: true })
                continue;
            text.Append(" | ");
            text.Append(entry is { } plate ? $"{slot}:{Label(plate)}{Screen(plate)}" : $"{slot}:unreadable");
        }
        return text.ToString();
    }

    private static string Label(VisualNameplate plate)
    {
        StringBuilder text = new($"#{plate.Incarnation} L{plate.Level}");
        if (plate.IsTarget == true) text.Append(" target");
        if (plate.IsMouseover == true) text.Append(" hover");
        if (plate.Attacks) text.Append(" attacker");
        if (plate.IsPlayer == true) text.Append(" player");
        if (plate.Attackable == false) text.Append(" passive");
        if (plate.TapDenied == true) text.Append(" tapped");
        if (plate.BehindCamera == true) text.Append(" behind");
        if (plate.Occluded == true) text.Append(" occluded");
        if (!plate.HasIdentity) text.Append(" no-guid");
        text.Append(" in:");
        for (int slot = 1; slot <= 12; slot++)
        {
            if (plate.Ranges.InRange(slot) == true)
                text.Append(slot).Append(',');
        }
        return text.ToString().TrimEnd(',');
    }

    private static string Screen(VisualNameplate plate) => plate.Screen is { } point
        ? $" @({point.X:F3},{point.Y:F3})" : string.Empty;

    // The capture's own image is configured for JPEG only, so its pixels are
    // saved through a default image.
    private static void SaveCapture(IWowScreen screen)
    {
        try
        {
            int width = screen.ScreenImage.Width, height = screen.ScreenImage.Height;
            Bgra32[] pixels = new Bgra32[width * height];
            screen.ScreenImage.CopyPixelDataTo(pixels);
            using Image<Bgra32> copy = Image.LoadPixelData<Bgra32>(pixels, width, height);
            string path = Path.Combine(Path.GetTempPath(),
                $"forever-marker-miss-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            copy.SaveAsPng(path);
            Console.WriteLine($"Marker miss capture: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Marker miss capture failed: {ex.Message}");
        }
    }
}
