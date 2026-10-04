using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Core.Forever;

/// <summary>Why a patrol heads where it does.</summary>
public enum ForeverPatrolLead
{
    /// <summary>A mob seen lately inside the area, out of view now.</summary>
    Remembered,
    /// <summary>A dot of the minimap's tracking inside the area.</summary>
    Tracked,
    /// <summary>The part of the area seen least lately.</summary>
    Uncovered,
}

/// <summary>Where a patrol heads next, on the mesh and reachable, and why.</summary>
/// <param name="Where">The world point to walk to.</param>
/// <param name="Lead">What sent the patrol there.</param>
/// <param name="Mark">The point the lead was about, kept when the walk finds nothing there.</param>
public readonly record struct ForeverPatrolGoal(Vector2 Where, ForeverPatrolLead Lead, Vector2 Mark);

/// <summary>
/// Where to look for the next fight inside an area, the way a person patrols
/// it. What the camera shows comes first: the task pulls it on every frame
/// (<see cref="ForeverPulling"/>), until nothing is left in view. With nothing
/// in view the patrol heads, in this order, for a mob seen lately and out of
/// view now (<see cref="TryRemembered"/>, any class), a dot of the minimap's
/// tracking (<see cref="TryTracked"/>, where the class tracks), or the part
/// of the area seen least (<see cref="TryLeastCovered"/>);
/// <see cref="TryNext"/> asks them in that order. The area is split into cells
/// of <see cref="CellYards"/>; a cell counts as seen when the player looked
/// round within <see cref="LookYards"/> of it or walked within
/// <see cref="PassYards"/>, and the one seen longest ago, never seen first,
/// the nearest of those, is the next to see. Decisions only; the task walks.
/// </summary>
public sealed class ForeverPatrol
{
    /// <summary>The side of a cell of the area.</summary>
    public const float CellYards = 25;
    /// <summary>
    /// A look round shows every plate out to the plate distance, 45 yards in
    /// the client; the cells this near count as seen.
    /// </summary>
    public const float LookYards = 40;
    /// <summary>Walking shows what lies ahead; the cells this near count as seen.</summary>
    public const float PassYards = 20;
    /// <summary>
    /// A tracked dot nearer than this after a look round showed no plate
    /// worth a pull: a critter, or a mob the profile does not take.
    /// </summary>
    public const float SightYards = 35;
    /// <summary>A lead this near a place a walk found nothing at is passed over a while.</summary>
    public const float VisitedYards = 10;
    /// <summary>How long a place a walk found nothing at is passed over.</summary>
    public const int VisitedMs = 90000;
    /// <summary>Cells asked whether they are reachable before the patrol gives up a pick.</summary>
    public const int MaxReachChecks = 6;
    /// <summary>
    /// A mob or a dot this far past the area's bounds still leads: a patrol
    /// covers what stands around its area, not only inside the line.
    /// </summary>
    public const float EdgeYards = 15;

    private readonly ForeverWorldArea area;
    private readonly ForeverMobMemory memory;
    private readonly Vector2[] cells;
    private readonly long[] seenAt;
    private readonly List<(Vector2 At, long When)> visited = [];

    public ForeverPatrol(ForeverWorldArea area, ForeverMobMemory memory)
    {
        this.area = area;
        this.memory = memory;
        List<Vector2> inside = [];
        for (float x = area.Min.X + CellYards / 2; x < area.Max.X; x += CellYards)
        {
            for (float y = area.Min.Y + CellYards / 2; y < area.Max.Y; y += CellYards)
            {
                if (area.Contains(new(x, y)))
                    inside.Add(new(x, y));
            }
        }
        if (inside.Count == 0)
            inside.Add(area.Centre);
        cells = [.. inside];
        seenAt = new long[cells.Length];
    }

    /// <summary>How many cells the area has, and how many have been seen.</summary>
    public (int Seen, int Cells) Coverage => (seenAt.Count(at => at != 0), cells.Length);

    /// <summary>The cells within <paramref name="yards"/> of <paramref name="here"/> are seen as of <paramref name="now"/>.</summary>
    public void Cover(Vector2 here, float yards, long now)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (Vector2.Distance(here, cells[i]) <= yards)
                seenAt[i] = now;
        }
    }

    /// <summary>A walk to a lead about <paramref name="at"/> found nothing there.</summary>
    public void Visited(Vector2 at, long now)
    {
        visited.RemoveAll(place => Elapsed(place.When, now) > VisitedMs);
        visited.Add((at, now));
    }

    /// <summary>
    /// The nearest mob seen lately in or around the area (<see cref="EdgeYards"/>) that the run may pull,
    /// not set aside, not on screen now, and not where a walk found nothing.
    /// </summary>
    public bool TryRemembered(VisualBarSample bars, ClassConfiguration config, ForeverExclusions exclusions,
        long now, out ForeverSeenMob mob)
    {
        mob = default;
        if (bars.PlayerIdentity is not { } player || bars.WorldPosition is not { } world)
            return false;
        Vector2 here = new((float)world.X, (float)world.Y);
        bool found = false;
        float best = float.MaxValue;
        foreach (ForeverSeenMob seen in memory.All)
        {
            float yards = Vector2.Distance(here, seen.At);
            if (yards >= best || !Around(seen.At) ||
                !ForeverTargetPolicy.IsEligible(seen.Plate, player, config) ||
                exclusions.IsExcluded(seen.Plate) ||
                ForeverTargetPolicy.ReportedDead(bars, seen.Plate.GuidHash1, seen.Plate.GuidHash2) ||
                OnScreen(bars, seen.Plate) || WasVisited(seen.At, now))
                continue;
            mob = seen;
            best = yards;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// The nearest dot of the minimap's tracking in or around the area, beyond
    /// <see cref="SightYards"/>, and not where a walk found nothing.
    /// </summary>
    public bool TryTracked(VisualBarSample bars, long now, out Vector2 dot)
    {
        dot = default;
        if (bars.TrackedBlips is not { Count: > 0 } blips || bars.WorldPosition is not { } world)
            return false;
        Vector2 here = new((float)world.X, (float)world.Y);
        bool found = false;
        float best = float.MaxValue;
        foreach (Vector2 blip in blips)
        {
            float yards = Vector2.Distance(here, blip);
            if (yards >= best || yards <= SightYards || !Around(blip) || WasVisited(blip, now))
                continue;
            dot = blip;
            best = yards;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// A spot in the cell seen longest ago, never seen first, the nearest of
    /// those: on the mesh, inside the area and reachable.
    /// </summary>
    public bool TryLeastCovered(Vector2 here, Func<Vector2, Vector2?> snap, Func<Vector2, bool> reachable,
        out Vector2 spot)
    {
        spot = default;
        int checks = 0;
        foreach (int i in Enumerable.Range(0, cells.Length)
            .OrderBy(i => seenAt[i]).ThenBy(i => Vector2.DistanceSquared(here, cells[i])))
        {
            if (snap(cells[i]) is not { } ground || !area.Contains(ground))
                continue;
            if (++checks > MaxReachChecks)
                return false;
            if (!reachable(ground))
                continue;
            spot = ground;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Where to go with nothing in view: a remembered mob, then a tracked dot,
    /// then the cell seen least. A lead whose place is off the mesh or out of
    /// reach is passed over a while, and the next is asked.
    /// </summary>
    public bool TryNext(VisualBarSample bars, ClassConfiguration config, ForeverExclusions exclusions,
        Func<Vector2, Vector2?> snap, Func<Vector2, bool> reachable, long now, out ForeverPatrolGoal goal)
    {
        goal = default;
        if (bars.WorldPosition is not { } world)
            return false;
        while (TryRemembered(bars, config, exclusions, now, out ForeverSeenMob mob))
        {
            if (snap(mob.At) is { } ground && reachable(ground))
            {
                goal = new(ground, ForeverPatrolLead.Remembered, mob.At);
                return true;
            }
            Visited(mob.At, now);
        }
        while (TryTracked(bars, now, out Vector2 dot))
        {
            if (snap(dot) is { } ground && reachable(ground))
            {
                goal = new(ground, ForeverPatrolLead.Tracked, dot);
                return true;
            }
            Visited(dot, now);
        }
        if (!TryLeastCovered(new((float)world.X, (float)world.Y), snap, reachable, out Vector2 spot))
            return false;
        goal = new(spot, ForeverPatrolLead.Uncovered, spot);
        return true;
    }

    // The unit's marker shows where a click reaches it: the pull choice
    // decides about it. One the camera shows out of reach of a click, as far
    // off near the top of the screen, still leads the walk.
    private static bool OnScreen(VisualBarSample bars, VisualNameplate plate)
    {
        if (bars.Nameplates is not { } roster)
            return false;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is { Screen: { } marker } shown && ForeverTargetPolicy.Clickable(marker) &&
                shown.GuidHash1 == plate.GuidHash1 && shown.GuidHash2 == plate.GuidHash2)
                return true;
        }
        return false;
    }

    // Inside the area, or within EdgeYards of its bounds.
    private bool Around(Vector2 point) =>
        area.Contains(point) ||
        point.X >= area.Min.X - EdgeYards && point.X <= area.Max.X + EdgeYards &&
        point.Y >= area.Min.Y - EdgeYards && point.Y <= area.Max.Y + EdgeYards;

    private bool WasVisited(Vector2 at, long now) =>
        visited.Exists(place => Elapsed(place.When, now) <= VisitedMs && Vector2.Distance(place.At, at) <= VisitedYards);

    private static double Elapsed(long since, long now) =>
        (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
