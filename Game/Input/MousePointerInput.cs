using SharedLib;

using SixLabors.ImageSharp;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;
using static WinAPI.NativeMethods;

namespace Game;

/// <summary>
/// Points at something in the game window and clicks it. Points are in the
/// client area, normalized: X across from the left, Y up from the bottom, as
/// the game's UI and the nameplate marker reader measure them.
/// </summary>
public interface IMousePointer
{
    /// <summary>
    /// Moves the cursor onto the point; true once it rests there. A call that
    /// had to move returns false, so the caller looks at a fresh frame before
    /// it relies on what is under the cursor.
    /// </summary>
    bool MoveToward(double x, double y);

    /// <summary>Left-clicks where the cursor rests.</summary>
    void Click();
}

/// <summary>
/// Moves the cursor the way a hand does: one continuous stroke along a slight
/// arc that speeds up and slows down again, longer for a longer way, instead
/// of jumping to the point.
/// </summary>
public sealed class MousePointerInput(MouseCameraInput camera, IMouseInput mouse,
    IScreenImageProvider screen) : IMousePointer
{
    // Within this many pixels the cursor rests on the point.
    private const int ArrivedPixels = 3;
    // A stroke takes this long plus a little per pixel, and some variation.
    private const int StrokeBaseMs = 120;
    private const double StrokeMsPerPixel = 0.25;
    private const int StrokeVariationMs = 40;
    // The arc bows out by up to this share of the stroke's length.
    private const double MaxBow = 0.08;
    private const int StepMs = 5;

    public bool MoveToward(double x, double y)
    {
        // The cursor is hidden while the right button steers; let go first.
        camera.Stop();
        if (!camera.Focus() || !GetCursorPos(out Point from))
            return false;

        Rectangle client = screen.ScreenRect;
        Point to = new(client.X + (int)Math.Round(x * client.Width),
            client.Y + (int)Math.Round((1 - y) * client.Height));
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= ArrivedPixels)
            return true;

        // A quadratic curve through a control point beside the midpoint.
        double bow = (Random.Shared.NextDouble() * 2 - 1) * MaxBow * distance;
        double controlX = from.X + dx / 2 - dy / distance * bow;
        double controlY = from.Y + dy / 2 + dx / distance * bow;
        double durationMs = StrokeBaseMs + distance * StrokeMsPerPixel +
            Random.Shared.Next(StrokeVariationMs);

        long start = GetTimestamp();
        while (true)
        {
            double t = Math.Min(1, GetElapsedTime(start).TotalMilliseconds / durationMs);
            double s = t * t * (3 - 2 * t);
            double u = 1 - s;
            mouse.SetCursorPos(new Point(
                (int)Math.Round(u * u * from.X + 2 * u * s * controlX + s * s * to.X),
                (int)Math.Round(u * u * from.Y + 2 * u * s * controlY + s * s * to.Y)));
            if (t >= 1)
                return false;
            Thread.Sleep(StepMs);
        }
    }

    public void Click()
    {
        // Both buttons down together walks the character; never click while steering.
        camera.Stop();
        if (GetCursorPos(out Point at))
            mouse.LeftClick(at);
    }
}
