using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.Threading;

namespace Core;

/// <summary>
/// Read-only controller for the unfinished Forever port. It keeps the regular
/// screen-capture/tracker path alive without constructing pathing, GOAP, or
/// game input services. This must not become an automation controller.
/// </summary>
public sealed class ForeverDiagnosticController : IBotController, IDisposable
{
    private readonly ILogger<ForeverDiagnosticController> logger;
    private readonly CancellationTokenSource cts;
    private readonly IWowScreen screen;
    private readonly ForeverVisualBarTracker visualBars;
    private readonly Thread captureThread;

    public bool IsBotActive => false;
    public string SelectedClassFilename => string.Empty;
    public Dictionary<int, string> SelectedPathFilename { get; } = [];
    public ClassConfiguration? ClassConfig => null;
    public GoapAgent? GoapAgent => null;
    public RouteInfo? RouteInfo => null;
    public double AvgScreenLatency => 0;
    public double AvgNPCLatency => 0;

    public event Action? ProfileLoaded;
    public event Action? StatusChanged;

    public ForeverDiagnosticController(
        ILogger<ForeverDiagnosticController> logger,
        CancellationTokenSource cts,
        IWowScreen screen,
        ForeverVisualBarTracker visualBars)
    {
        this.logger = logger;
        this.cts = cts;
        this.screen = screen;
        this.visualBars = visualBars;

        captureThread = new(CaptureLoop)
        {
            IsBackground = true,
            Name = "Forever read-only capture"
        };
        captureThread.Start();
        logger.LogInformation("Forever diagnostic mode: gameplay automation is disabled");
    }

    private void CaptureLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                screen.Update();
                visualBars.Update();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forever screen capture failed; visual data will expire");
                cts.Token.WaitHandle.WaitOne(1000);
            }

            cts.Token.WaitHandle.WaitOne(100);
        }
    }

    public ClassConfiguration ResolveLoadedProfile() =>
        throw new InvalidOperationException("Forever gameplay profiles are not supported yet.");

    public void ToggleBotStatus()
    {
        logger.LogWarning("Forever bot start refused: gameplay data and navigation are not ported");
        StatusChanged?.Invoke();
    }

    public void MinimapNodeFound() { }
    public void Shutdown() => cts.Cancel();
    public IEnumerable<string> ClassFiles() => [];
    public IEnumerable<string> PathFiles() => [];
    public bool LoadClassProfile(string classFilename)
    {
        logger.LogWarning("Forever class profile refused: gameplay data and navigation are not ported");
        ProfileLoaded?.Invoke();
        return false;
    }
    public void LoadPathProfile(Dictionary<int, string> pathFilenames) { }
    public void OverrideClassConfig(ClassConfiguration classConfig) { }
    public void SaveClassConfig() { }

    public void Dispose()
    {
        cts.Cancel();

        // Screen capture is owned by the outer service provider. Let the
        // capture loop leave Update before that provider disposes the device.
        if (Thread.CurrentThread != captureThread && captureThread.IsAlive)
            captureThread.Join();
    }
}
