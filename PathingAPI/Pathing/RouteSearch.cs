using PathingAPI.Watch;

using PPather;

using SharedLib;
using PPather.Graph;

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using static System.Diagnostics.Stopwatch;

namespace PathingAPI.Pathing;

/// <summary>
/// Per-request route tuning. Applied to the engine on every search, so one
/// request's jitter or edge margin never leaks into the next one - the engine
/// keeps them as mutable state, and endpoints that did not set them used to
/// inherit whatever the previous caller left there.
/// </summary>
/// <param name="JitterYards">Random corner offset in yards; 0 disables it.</param>
/// <param name="JitterSeed">Seeds the jitter; null draws a fresh seed.</param>
/// <param name="EdgeMarginYards">Edge margin override; null keeps the engine default.</param>
public readonly record struct RouteOptions(float JitterYards = 0f, int? JitterSeed = null, float? EdgeMarginYards = null);

/// <summary>What an interactive search found, for the page that asked.</summary>
public readonly record struct SearchOutcome(int Points, double ElapsedMs, string? Error)
{
    public bool Found => Points > 0;

    public string Summary =>
        Error ?? (Found ? $"{Points} points in {ElapsedMs:F0} ms" : $"No path found ({ElapsedMs:F0} ms)");
}

/// <summary>
/// The only code that drives <see cref="PPatherService"/>'s two-call search. The
/// synchronous members expect the caller to hold the <see cref="PatherGate"/>
/// (HTTP and AnTCP take it their own way); the async ones take it themselves and
/// run off the caller's thread, for the web UI.
/// </summary>
public sealed class RouteSearch(PPatherService service, PatherGate gate, WatchBroadcaster watch)
{
    /// <summary>The only strategy the navmesh engine has; SpotAStar honours the others.</summary>
    public const SearchStrategy DefaultStrategy = SearchStrategy.A_Star_With_Model_Avoidance;

    public PathingEngine Engine => service.Engine;

    /// <summary>Searches from <paramref name="from"/> to <paramref name="to"/>. Caller holds the gate.</summary>
    public Path? Find(Vector4 from, Vector4 to, RouteOptions options,
        SearchStrategy strategy = DefaultStrategy, PathStyle? style = null)
    {
        service.PathJitterYards = options.JitterYards;
        service.PathJitterSeed = options.JitterSeed;
        service.PathEdgeMarginYards = options.EdgeMarginYards;

        watch.NextPathStyle(style ?? PathStyle.Api);
        service.SetLocations(from, to);
        return service.DoSearch(strategy);
    }

    /// <summary>Shows <paramref name="points"/> as a path without searching. Caller holds the gate.</summary>
    public void Draw(float mapId, ReadOnlySpan<Vector3> points, PathStyle? style = null)
    {
        if (points.IsEmpty)
            return;

        PathStyle drawn = style ?? PathStyle.Api;
        watch.NextPathStyle(drawn);
        service.SetLocations(new(points[0], mapId), new(points[^1], mapId));

        // The engine grows spots around each point, which needs the continent's
        // geometry; a tiles-only install has none, so show the points as they are.
        if (service.DiskOnly)
        {
            watch.ShowPath(points.ToArray(), drawn);
            return;
        }

        service.DrawPath(mapId, points);
    }

    // The async members wait for the gate with the page's token. A page closed while
    // waiting gets a result, not an exception: thrown into a Blazor event handler, it
    // would end the whole circuit.

    public async Task<SearchOutcome> FindAsync(Vector4 from, Vector4 to, SearchStrategy strategy,
        RouteOptions options, CancellationToken token = default)
    {
        SearchOutcome outcome = new(0, 0, "Cancelled");
        await RunAsync(() =>
        {
            long start = GetTimestamp();
            try
            {
                Path? path = Find(from, to, options, strategy, PathStyle.For(strategy));
                outcome = new SearchOutcome(path?.locations.Count ?? 0, GetElapsedTime(start).TotalMilliseconds, null);
            }
            catch (Exception e)
            {
                outcome = new SearchOutcome(0, GetElapsedTime(start).TotalMilliseconds, e.Message);
            }
        }, token);

        return outcome;
    }

    public Task<bool> DrawAsync(float mapId, Vector3[] points, PathStyle style, CancellationToken token = default)
    {
        return RunAsync(() => Draw(mapId, points, style), token);
    }

    /// <summary>Drops the engine's resident state so a fresh 3D view receives every chunk again.</summary>
    public Task<bool> ResetAsync(CancellationToken token = default)
    {
        return RunAsync(service.Reset, token);
    }

    /// <summary>Switches engine at runtime (A/B without a restart), like <c>POST Engine</c>.</summary>
    public Task<bool> SetEngineAsync(PathingEngine engine, CancellationToken token = default)
    {
        return RunAsync(() =>
        {
            service.Reset();
            service.Engine = engine;
        }, token);
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the gate on a pool thread. False when
    /// <paramref name="token"/> was cancelled before the gate came free; exceptions
    /// from the action itself are the caller's.
    /// </summary>
    public async Task<bool> RunAsync(Action action, CancellationToken token = default)
    {
        try
        {
            await Task.Run(async () =>
            {
                using IDisposable lease = await gate.EnterAsync(token).ConfigureAwait(false);
                action();
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }
}
