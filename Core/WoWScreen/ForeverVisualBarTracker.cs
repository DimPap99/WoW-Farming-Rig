using Game;

using SharedLib;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Core;

/// <summary>
/// Tracks the /forevervisual bars and validity pixels from normal screen capture.
/// It is read-only and only active for Forever. A missing calibration or a
/// stopped capture invalidates the sample instead of reusing old percentages,
/// and <see cref="Problem"/> says which.
/// <see cref="Update"/> must be called from one thread; reads are thread-safe.
/// </summary>
public sealed class ForeverVisualBarTracker
{
    private static readonly TimeSpan MaxSampleAge = TimeSpan.FromMilliseconds(750);

    private readonly IWowScreen screen;
    private readonly bool enabled;
    public bool IsEnabled => enabled;
    private Bgra32[] searchPixels = [];
    private Bgra32[] pixels = [];
    private Bgra32[] framePixels = [];
    private Bgra32[] minimapPixels = [];
    // The last screen frame decoded, so one capture is never decoded twice.
    private long decodedFrameId;
    private Snapshot? current;
    // Why the last capture gave no sample, and whether that stretch's first
    // capture was saved.
    private volatile string readProblem = "no screen capture yet";
    private bool saved;
    private bool readOnce;

    /// <summary>
    /// When set, the first capture of each stretch the bars cannot be read
    /// from is saved in this folder as a PNG, to show what hid them.
    /// </summary>
    public string? UnreadableFrameFolder { get; set; }

    /// <summary>Why <see cref="TryGetLatest"/> has no sample now; empty while it has one.</summary>
    public string Problem
    {
        get
        {
            Snapshot? snapshot = Volatile.Read(ref current);
            if (snapshot is null)
                return readProblem;
            TimeSpan age = Stopwatch.GetElapsedTime(snapshot.CapturedAt);
            return age > MaxSampleAge
                ? $"no new screen capture for {age.TotalMilliseconds:0} ms"
                : string.Empty;
        }
    }

    private sealed record Snapshot(VisualBarSample Bars, long CapturedAt);

    public ForeverVisualBarTracker(IWowScreen screen, StartupClientVersion clientVersion)
    {
        this.screen = screen;
        enabled = clientVersion.Version == ClientVersion.Forever;
    }

    public void Update()
    {
        if (!enabled)
            return;

        long frameId = screen.ScreenFrameId;
        if (frameId == 0 || frameId == decodedFrameId)
            return;
        decodedFrameId = frameId;

        Image<Bgra32> image = screen.ScreenImage;
        bool read = TryReadFrame(image, out VisualBarSample bars, out string problem);
        Volatile.Write(ref current, read
            ? new Snapshot(bars with { FrameId = frameId }, Stopwatch.GetTimestamp())
            : null);
        if (read)
        {
            saved = false;
            readOnce = true;
            return;
        }
        readProblem = problem;
        // Only a stretch after the bars have read is saved, not the first
        // captures before the window shows them.
        if (!saved && readOnce && UnreadableFrameFolder is { } folder)
        {
            saved = true;
            readProblem = $"{problem}; the capture is saved as {Save(image, folder)}";
        }
    }

    // A capture the bars could not be read from, for a person to look at.
    private static string Save(Image<Bgra32> image, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            // The capture's own configuration knows only JPEG.
            image.Save(path, new PngEncoder());
            return path;
        }
        catch (Exception ex)
        {
            // A diagnostic never stops the reader.
            return $"nothing ({ex.Message})";
        }
    }

    // The telemetry sits in the top-left corner, so copy only that region each
    // frame. The nameplate markers can be anywhere and their normalized
    // coordinates use the full viewport, so the whole frame is copied only on
    // the frames where a roster slot may show one.
    private bool TryReadFrame(Image<Bgra32> image, out VisualBarSample bars, out string problem)
    {
        bars = default;
        int searchWidth = Math.Min(image.Width, VisualBarReader.SearchWidth);
        int searchHeight = Math.Min(image.Height, VisualBarReader.SearchHeight);
        Copy(image, ref searchPixels, searchWidth, searchHeight);
        if (!VisualBarReader.TryGetReadRegion(searchPixels, searchWidth, searchHeight,
                image.Width, image.Height, out int width, out int height))
        {
            problem = "the add-on's bars were not found at the top left of the screen: off, hidden or covered";
            return false;
        }

        Copy(image, ref pixels, width, height);
        if (!VisualBarReader.TryRead(pixels, width, height, out bars, locateMarkers: false))
        {
            problem = "the add-on's bars were found but did not read";
            return false;
        }
        problem = string.Empty;

        if (VisualBarReader.NeedsMarkerScan(bars))
        {
            Copy(image, ref framePixels, image.Width, image.Height);
            bars = VisualBarReader.LocateMarkers(framePixels, image.Width, image.Height, bars);
        }
        // The tracked units' dots, from the minimap's own small square of the frame.
        if (bars.Minimap is { } minimap && bars.WorldPosition is { } world && bars.Position is { } map &&
            VisualMinimapReader.Region(minimap, image.Width, image.Height) is { Size: > 0 } square)
        {
            CopyRegion(image, ref minimapPixels, square.Left, square.Top, square.Size);
            bars = bars with
            {
                TrackedBlips = VisualMinimapReader.FindBlips(minimapPixels, square.Left, square.Top,
                    square.Size, minimap, image.Width, image.Height,
                    new((float)world.X, (float)world.Y), map.Direction)
            };
        }
        return true;
    }

    private static void CopyRegion(Image<Bgra32> image, ref Bgra32[] buffer, int left, int top, int size)
    {
        if (buffer.Length != size * size)
            buffer = new Bgra32[size * size];

        Bgra32[] target = buffer;
        image.ProcessPixelRows(source =>
        {
            for (int y = 0; y < size; y++)
                source.GetRowSpan(top + y).Slice(left, size).CopyTo(target.AsSpan(y * size, size));
        });
    }

    private static void Copy(Image<Bgra32> image, ref Bgra32[] buffer, int width, int height)
    {
        if (buffer.Length != width * height)
            buffer = new Bgra32[width * height];

        Bgra32[] target = buffer;
        image.ProcessPixelRows(source =>
        {
            for (int y = 0; y < height; y++)
                source.GetRowSpan(y).Slice(0, width).CopyTo(target.AsSpan(y * width, width));
        });
    }

    /// <summary>The latest decoded sample; its <see cref="VisualBarSample.FrameId"/> names the capture.</summary>
    public bool TryGetLatest(out VisualBarSample bars)
    {
        Snapshot? snapshot = Volatile.Read(ref current);
        if (snapshot is null || Stopwatch.GetElapsedTime(snapshot.CapturedAt) > MaxSampleAge)
        {
            bars = default;
            return false;
        }

        bars = snapshot.Bars;
        return true;
    }
}
