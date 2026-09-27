using GameOverlay.Drawing;
using GameOverlay.Windows;

using System;

namespace ForeverVisualReader;

/// <summary>Read-only desktop text attached to the game client area.</summary>
internal sealed class MarkerCoordinateOverlay : IDisposable
{
    private readonly GraphicsWindow window;
    private Font? font;
    private SolidBrush? textBrush;
    private volatile string label = "Target marker not visible";

    public MarkerCoordinateOverlay(IntPtr gameWindow)
    {
        Graphics graphics = new()
        {
            MeasureFPS = false,
            PerPrimitiveAntiAliasing = false,
            TextAntiAliasing = true,
            VSync = true,
            WindowHandle = IntPtr.Zero
        };
        window = new StickyWindow(gameWindow, graphics)
        {
            FPS = 10,
            AttachToClientArea = true,
            BypassTopmost = true
        };
        window.SetupGraphics += (_, e) =>
        {
            font = e.Graphics.CreateFont("Arial", 20);
            textBrush = e.Graphics.CreateSolidBrush(255, 255, 255, 255);
        };
        window.DestroyGraphics += (_, _) =>
        {
            textBrush?.Dispose();
            font?.Dispose();
        };
        window.DrawGraphics += (_, e) =>
        {
            e.Graphics.ClearScene();
            if (font is not null && textBrush is not null)
                e.Graphics.DrawText(font, 20, textBrush, 680, 35,
                    label);
        };
        window.Create();
    }

    public void Update(int? screenX, int? screenY, bool stale = false)
    {
        if (screenX is { } nextX && screenY is { } nextY)
            label = stale
                ? $"Last marker  X={nextX}  Y={nextY} px (stale)"
                : $"Target marker  X={nextX}  Y={nextY} px";
        else
            label = "Target marker not visible";
    }

    public void Dispose() => window.Dispose();
}
