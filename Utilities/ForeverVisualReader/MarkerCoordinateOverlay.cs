using GameOverlay.Drawing;
using GameOverlay.Windows;

using System;
using System.Collections.Generic;

namespace ForeverVisualReader;

/// <summary>Read-only desktop text attached to the game client area.</summary>
internal sealed class MarkerCoordinateOverlay : IDisposable
{
    private readonly GraphicsWindow window;
    private Font? font;
    private SolidBrush? textBrush;
    private volatile string status = "No nameplate markers";
    private volatile IReadOnlyList<(float X, float Y, string Text)> labels = [];

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
            if (font is null || textBrush is null)
                return;
            e.Graphics.DrawText(font, 20, textBrush, 680, 35, status);
            foreach ((float x, float y, string text) in labels)
                e.Graphics.DrawText(font, 20, textBrush, x + 18, y + 18, text);
        };
        window.Create();
    }

    /// <summary>A status line, and one label beside each marker, in client pixels.</summary>
    public void Update(string line, IReadOnlyList<(float X, float Y, string Text)> markers)
    {
        status = line;
        labels = markers;
    }

    public void Dispose() => window.Dispose();
}
