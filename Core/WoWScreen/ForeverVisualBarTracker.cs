using Game;

using SharedLib;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Diagnostics;
using System.Threading;

namespace Core;

/// <summary>
/// Tracks the /forevervisual bars and validity pixels from normal screen capture.
/// It is read-only and only active for Forever. A missing calibration or a
/// stopped capture invalidates the sample instead of reusing old percentages.
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
    // The last screen frame decoded, so one capture is never decoded twice.
    private long decodedFrameId;
    private Snapshot? current;

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
        Volatile.Write(ref current, TryReadFrame(image, out VisualBarSample bars)
            ? new Snapshot(bars with { FrameId = frameId }, Stopwatch.GetTimestamp())
            : null);
    }

    // The telemetry sits in the top-left corner, so copy only that region each
    // frame. The nameplate markers can be anywhere and their normalized
    // coordinates use the full viewport, so the whole frame is copied only on
    // the frames where a roster slot may show one.
    private bool TryReadFrame(Image<Bgra32> image, out VisualBarSample bars)
    {
        bars = default;
        int searchWidth = Math.Min(image.Width, VisualBarReader.SearchWidth);
        int searchHeight = Math.Min(image.Height, VisualBarReader.SearchHeight);
        Copy(image, ref searchPixels, searchWidth, searchHeight);
        if (!VisualBarReader.TryGetReadRegion(searchPixels, searchWidth, searchHeight,
                image.Width, image.Height, out int width, out int height))
            return false;

        Copy(image, ref pixels, width, height);
        if (!VisualBarReader.TryRead(pixels, width, height, out bars, locateMarkers: false))
            return false;

        if (VisualBarReader.NeedsMarkerScan(bars))
        {
            Copy(image, ref framePixels, image.Width, image.Height);
            bars = VisualBarReader.LocateMarkers(framePixels, image.Width, image.Height, bars);
        }
        return true;
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
