using System;
using System.Threading;

namespace PathingAPI.Watch;

/// <summary>
/// What the server streams to every viewer. These used to live on each open page's
/// own copy of the watch component, but every copy broadcast to every viewer, so a
/// toggle in one tab was overridden by any other tab. One shared set is what
/// actually happens on the wire, so that is what the toolbar now edits.
/// </summary>
public sealed class WatchSettings
{
    private int viewers;

    public event Action? Changed;

    public GeometryMode Geometry { get; private set; } = GeometryMode.All;

    public bool NavmeshTiles { get; private set; } = true;

    /// <summary>SpotAStar debug points; only produced while <c>PathGraph.DelayMs</c> slows the search.</summary>
    public bool TestPoints { get; private set; }

    /// <summary>Open watch-hub connections. Nothing is streamed while this is 0.</summary>
    public int Viewers => Volatile.Read(ref viewers);

    public void SetGeometry(GeometryMode mode) => Set(() => Geometry = mode);

    public void SetNavmeshTiles(bool on) => Set(() => NavmeshTiles = on);

    public void SetTestPoints(bool on) => Set(() => TestPoints = on);

    public void ViewerJoined() => Set(() => Interlocked.Increment(ref viewers));

    public void ViewerLeft() => Set(() => Interlocked.Decrement(ref viewers));

    private void Set(Action change)
    {
        change();
        Changed?.Invoke();
    }
}
