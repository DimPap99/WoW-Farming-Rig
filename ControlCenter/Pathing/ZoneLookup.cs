using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>
/// The zone a world point is in, as the service's baked area grid has it (a town lies in its parent
/// zone); where the grid is not baked, or not asked yet, the zone maps' guess
/// (<see cref="ZoneMath.ZoneAt"/>). Zone maps overlap a lot - Durotar's covers the east of the
/// Barrens, the Barrens' the south of Durotar - so the guess is wrong near a border and the grid is
/// not. A page shows what is <see cref="Known"/> at once and asks about its points with
/// <see cref="LearnAsync"/>, drawing again when something was learned. Answers are kept per small
/// cell, and forgotten when the service comes back, as it may serve another client then.
/// </summary>
public sealed class ZoneLookup(IServiceProvider services)
{
    /// <summary>Points this close share one answer: half the grid's own cell (33 yd).</summary>
    public const float CellYd = 16f;

    /// <summary>Questions to the service at once.</summary>
    public const int Concurrent = 4;

    private readonly ConcurrentDictionary<(int MapId, int X, int Y), int> known = new();

    /// <summary>Forgets every answer, as when the service comes back: it may serve another client.</summary>
    public void Forget()
    {
        known.Clear();
    }

    /// <summary>
    /// The zone at a point as far as known now: the grid's, when asked before, baked, and its map
    /// holds the point (a few areas belong to a zone whose map does not reach them, and a point is
    /// named in 0 to 100 of its zone's map); else the maps' guess.
    /// </summary>
    public ZoneInfo? Known(WorldSnapshot world, int mapId, Point3 at)
    {
        return known.TryGetValue(CellOf(mapId, at), out int uiMap) && uiMap != 0 && world.Zone(uiMap) is ZoneInfo zone &&
               ZoneMath.Contains(zone, mapId, at.X, at.Y)
            ? zone
            : ZoneMath.ZoneAt(world.World.Zones, mapId, at.X, at.Y);
    }

    /// <summary>
    /// Asks the service about every point not asked about yet, a few at a time: true when an answer
    /// is new. A service that does not answer is not asked again for this call's other points.
    /// </summary>
    public async Task<bool> LearnAsync(int mapId, IEnumerable<Point3> points, CancellationToken token)
    {
        (int MapId, int X, int Y)[] cells = [.. points.Select(p => CellOf(mapId, p)).Distinct().Where(c => !known.ContainsKey(c))];
        if (cells.Length == 0)
        {
            return false;
        }

        IPathingClient pathing = services.GetRequiredService<IPathingClient>();
        int learned = 0;
        using CancellationTokenSource down = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await Parallel.ForEachAsync(cells, new ParallelOptions { MaxDegreeOfParallelism = Concurrent, CancellationToken = down.Token },
                async (cell, cancel) =>
                {
                    // The cell's middle stands for the cell.
                    ServiceResult<AreaAtPoint> area = await pathing.GetAreaAsync(cell.MapId, (cell.X + 0.5f) * CellYd, (cell.Y + 0.5f) * CellYd, cancel);
                    if (area.Value is AreaAtPoint answer)
                    {
                        known[cell] = answer.UiMapId;
                        Interlocked.Increment(ref learned);
                    }
                    else
                    {
                        await down.CancelAsync();
                    }
                });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The service stopped answering: what was learned is kept.
        }

        return learned > 0;
    }

    private static (int MapId, int X, int Y) CellOf(int mapId, Point3 at)
    {
        return (mapId, (int)MathF.Floor(at.X / CellYd), (int)MathF.Floor(at.Y / CellYd));
    }
}
