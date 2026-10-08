using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using System.Runtime.InteropServices;

namespace HeadlessServer.Hosting;

/// <summary>
/// Stops the host on what the Generic Host's console lifetime leaves out (it takes Ctrl+C, Ctrl+Break
/// and SIGTERM), and holds the signal until the host has stopped, so a run releases input before the
/// process goes (known issue 65):
/// <list type="bullet">
/// <item>the window closing: SIGHUP, which .NET raises for CTRL_CLOSE on Windows; Windows ends the
/// process once the handler returns, or after 5 s;</item>
/// <item>the session ending on Windows (a logoff, a shutdown): a process that loaded user32, as any
/// Classic run does, hears that only through a window, which <see cref="SystemEvents"/> keeps.</item>
/// </list>
/// Task Scheduler's End terminates the process: nothing can stop it cleanly.
/// </summary>
public sealed partial class StopOnSignals(IHostApplicationLifetime lifetime, ILogger<StopOnSignals> logger) : IHostedService, IDisposable
{
    /// <summary>How long a signal is held while the host stops: Windows ends a closing console's processes after 5 s.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(4.5);

    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PosixSignalRegistration? hangup;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());
        hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnHangup);
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Stops the host, and waits until it has stopped or <paramref name="hold"/> passed: true when it stopped.</summary>
    public bool StopAndWait(string why, TimeSpan hold)
    {
        LogStopping(logger, why);
        lifetime.StopApplication();
        return stopped.Task.Wait(hold);
    }

    public void Dispose()
    {
        hangup?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
    }

    private void OnHangup(PosixSignalContext context)
    {
        // Handled here: on Linux the process is not ended for it; Windows ends it once this returns.
        context.Cancel = true;
        StopAndWait("the window closed", Hold);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        StopAndWait(e.Reason == SessionEndReasons.Logoff ? "the user logs off" : "Windows shuts down", Hold);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping: {Why}")]
    static partial void LogStopping(ILogger logger, string why);
}
