using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;
using static WinAPI.NativeMethods;

namespace Game;

/// <summary>
/// Foreground right-mouse steering. While the right button is held, a pump
/// moves the mouse in small steps several dozen times a second at the speed
/// the last <see cref="Turn"/> asked for, as a hand turns the camera, rather
/// than one jump per captured frame. A speed that is not renewed for a moment
/// falls to zero, so a stalled caller never leaves the camera turning.
/// </summary>
public sealed class MouseCameraInput : IDisposable
{
    // Pixels per second for each unit of error, and the fastest turn: a marker
    // at the screen edge swings in within a fraction of a second, then eases.
    private const double Gain = 1500;
    private const double MaxSpeed = 1200;
    // A marker this close to the centre is on target.
    private const double DeadZone = 0.018;
    // A speed Turn has not renewed for this long is dropped.
    private const int SpeedHoldMs = 200;
    private const int PumpIntervalMs = 8;

    private readonly WowProcess process;
    // Orders the button and the pump's moves: no move follows the release.
    private readonly object sync = new();
    private bool held;
    private double speed;
    private long speedSetAt;
    private Thread? pump;
    private volatile bool disposed;

    public MouseCameraInput(WowProcess process) => this.process = process;

    /// <summary>The game is the active window; a click elsewhere is how a person takes the PC back.</summary>
    public bool HasFocus => WinAPI.NativeMethods.GetForegroundWindow() == process.MainWindowHandle;

    /// <summary>
    /// Brings the game window to the foreground, as a person's first click
    /// would; the game only follows a mouse it has focus for.
    /// </summary>
    public bool Focus()
    {
        nint window = process.MainWindowHandle;
        if (WinAPI.NativeMethods.GetForegroundWindow() == window)
            return true;
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
        return WinAPI.NativeMethods.GetForegroundWindow() == window;
    }

    /// <summary>The right button is held for turning.</summary>
    public bool IsHeld
    {
        get
        {
            lock (sync)
                return held;
        }
    }

    /// <summary>
    /// Holds the right button and turns at a speed proportional to
    /// <paramref name="horizontalError"/>, positive to the right, easing as the
    /// error shrinks. Call it on every frame the turn should go on.
    /// </summary>
    /// <param name="pressX">Where a new hold presses the button, across the
    /// window from the left. A press on a nameplate or a unit targets it when
    /// the button comes up, so the caller picks empty ground.</param>
    /// <param name="pressY">The same, up from the bottom.</param>
    public bool Turn(double horizontalError, double pressX = 0.5, double pressY = 2.0 / 3)
    {
        nint window = process.MainWindowHandle;
        bool starting;
        lock (sync)
            starting = !held;
        if (starting)
        {
            if (!Focus())
                return false;
            WinAPI.NativeMethods.GetWindowRect(window, out var rect);
            WinAPI.NativeMethods.SetCursorPos(rect.X + (int)Math.Round(pressX * rect.Width),
                rect.Y + (int)Math.Round((1 - pressY) * rect.Height));
            lock (sync)
            {
                MouseEvent(0x0008, 0, 0, 0, 0); // right down
                held = true;
                speed = 0;
            }
            StartPump();
            Thread.Sleep(30);
        }
        if (WinAPI.NativeMethods.GetForegroundWindow() != window)
        {
            Stop();
            return false;
        }
        double next = Math.Abs(horizontalError) <= DeadZone ? 0 :
            Math.Clamp(horizontalError * Gain, -MaxSpeed, MaxSpeed);
        lock (sync)
        {
            speed = next;
            speedSetAt = GetTimestamp();
        }
        return true;
    }

    /// <summary>Stops turning at once, the right button still held.</summary>
    public void StopTurning()
    {
        lock (sync)
            speed = 0;
    }

    public void Stop()
    {
        lock (sync)
        {
            if (held) MouseEvent(0x0010, 0, 0, 0, 0); // right up
            held = false;
            speed = 0;
        }
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
    }

    private void StartPump()
    {
        if (pump is not null)
            return;
        pump = new Thread(Pump) { IsBackground = true, Name = "Forever camera turn" };
        pump.Start();
    }

    // Runs for this object's lifetime and moves only while the button is held
    // and the game has focus. Fractions of a pixel carry over between steps.
    private void Pump()
    {
        double carry = 0;
        long last = GetTimestamp();
        nint window = process.MainWindowHandle;
        while (!disposed)
        {
            Thread.Sleep(PumpIntervalMs);
            double seconds = Math.Min(0.05, GetElapsedTime(last).TotalSeconds);
            last = GetTimestamp();
            lock (sync)
            {
                if (!held || GetElapsedTime(speedSetAt).TotalMilliseconds > SpeedHoldMs ||
                    WinAPI.NativeMethods.GetForegroundWindow() != window)
                {
                    carry = 0;
                    continue;
                }
                carry += speed * seconds;
                int dx = (int)carry;
                carry -= dx;
                if (dx != 0)
                    MouseEvent(0x0001, unchecked((uint)dx), 0, 0, 0);
            }
        }
    }
}
