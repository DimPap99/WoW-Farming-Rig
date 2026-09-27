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
/// </summary>
public sealed class ForeverVisualBarTracker
{
    private const int MaxRoiWidth = 650;
    private const int MaxRoiHeight = 1000;
    private static readonly TimeSpan MaxSampleAge = TimeSpan.FromMilliseconds(750);

    private readonly IWowScreen screen;
    private readonly bool enabled;
    public bool IsEnabled => enabled;
    private Bgra32[] pixels = [];
    private long lastFrameId;
    public long LatestFrameId => lastFrameId;
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
        if (frameId == 0 || frameId == lastFrameId)
            return;
        lastFrameId = frameId;

        Image<Bgra32> image = screen.ScreenImage;
        int width = Math.Min(image.Width, MaxRoiWidth);
        int height = Math.Min(image.Height, MaxRoiHeight);
        if (pixels.Length != width * height)
            pixels = new Bgra32[width * height];

        Bgra32[] buffer = pixels;
        image.ProcessPixelRows(source =>
        {
            for (int y = 0; y < height; y++)
                source.GetRowSpan(y).Slice(0, width).CopyTo(buffer.AsSpan(y * width, width));
        });

        Snapshot? next = VisualBarReader.TryRead(buffer, width, height, out VisualBarSample bars)
            ? new(bars, Stopwatch.GetTimestamp())
            : null;
        Volatile.Write(ref current, next);
    }

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
