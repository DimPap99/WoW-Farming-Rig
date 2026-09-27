using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;
using static WinAPI.NativeMethods;

namespace Game;

/// <summary>Foreground right-mouse steering with bounded relative corrections.</summary>
public sealed class MouseCameraInput : IDisposable
{
    private readonly WowProcess process;
    private bool held;
    private long lastMove;

    public MouseCameraInput(WowProcess process) => this.process = process;

    public bool Turn(double horizontalError)
    {
        nint window = process.MainWindowHandle;
        if (!held)
        {
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool attached = currentThread != foregroundThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            try { SetForegroundWindow(window); }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
            for (int attempt = 0; attempt < 10 &&
                WinAPI.NativeMethods.GetForegroundWindow() != window; attempt++)
                Thread.Sleep(25);
            if (WinAPI.NativeMethods.GetForegroundWindow() != window)
                return false;
            WinAPI.NativeMethods.GetWindowRect(window, out var rect);
            WinAPI.NativeMethods.SetCursorPos(rect.X + rect.Width / 2,
                rect.Y + rect.Height / 3);
            MouseEvent(0x0008, 0, 0, 0, 0); // right down
            held = true;
            lastMove = GetTimestamp();
            Thread.Sleep(30);
        }
        if (WinAPI.NativeMethods.GetForegroundWindow() != window)
        {
            Stop();
            return false;
        }
        double seconds = Math.Min(0.1, GetElapsedTime(lastMove).TotalSeconds);
        if (seconds < 0.03) return true;
        lastMove = GetTimestamp();
        // Proportional steering eases as the marker approaches screen center.
        int dx = Math.Abs(horizontalError) <= 0.018 ? 0 :
            (int)Math.Round(Math.Clamp(horizontalError * 600, -300, 300) * seconds);
        if (dx != 0)
            MouseEvent(0x0001, unchecked((uint)dx), 0, 0, 0);
        return true;
    }

    public void Stop()
    {
        if (held) MouseEvent(0x0010, 0, 0, 0, 0); // right up
        held = false;
    }

    public void Dispose() => Stop();

}
