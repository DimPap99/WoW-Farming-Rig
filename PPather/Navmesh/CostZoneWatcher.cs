#nullable enable
using System;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace PPather.Navmesh;

/// <summary>
/// Calls back whenever an authored road or danger-zone file under the road root changes on
/// disk, whoever wrote it. The map UI asks for a reload after saving, but hand edits,
/// scripts and the DangerZoneGenerator utility do not - without this they take effect only
/// after a restart. Zones are query-time only, so the reload the callback does is a pointer
/// swap: no rebake, no navmesh invalidation.
/// </summary>
public sealed class CostZoneWatcher : IDisposable
{
    /// <summary>
    /// Coalescing window for zone file edits. One save from the map UI writes a road file
    /// and a danger file, and most editors emit several events per write, so reloading per
    /// event would reload four or five times for one logical edit.
    /// </summary>
    public const int DebounceMs = 500;

    private readonly ILogger logger;
    private readonly Action onChanged;
    private readonly Timer debounce;
    private FileSystemWatcher? watcher;

    private CostZoneWatcher(ILogger logger, Action onChanged)
    {
        this.logger = logger;
        this.onChanged = onChanged;
        debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Watches <paramref name="root"/> and its subfolders (<c>dangerzone/</c> lives under each
    /// continent), creating it when it is missing: an era with no zones yet (cata, mop) gets them
    /// later, from the agent's data sync or a first save, and they must count. Null when the folder
    /// cannot be created or watched: the Pathing Service's reload endpoint still works then, and a
    /// bot reads its zones again only when it restarts.
    /// </summary>
    public static CostZoneWatcher? TryStart(ILogger logger, string root, Action onChanged)
    {
        CostZoneWatcher zones = new(logger, onChanged);
        try
        {
            Directory.CreateDirectory(root);
            zones.Start(root);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Cost zone watcher live on {Root}.", root);
            }
            return zones;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start the cost zone watcher; use the reload endpoint instead.");
            zones.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }

        debounce.Dispose();
    }

    private void Start(string root)
    {
        watcher = new FileSystemWatcher(root, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileChanged;
        watcher.Error += (_, e) =>
            logger.LogWarning(e.GetException(), "Cost zone watcher error; edits may need a manual reload.");

        watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        debounce.Change(DebounceMs, Timeout.Infinite);
    }

    // Timer callback. Must never throw: an escaping exception on a timer thread takes the
    // process down.
    private void Fire()
    {
        try
        {
            onChanged();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reloading cost zones from disk failed; the previous zones stay in effect.");
        }
    }
}
