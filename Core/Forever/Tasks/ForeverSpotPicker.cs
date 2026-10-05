using SharedLib.Forever;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// Picks the spots a roam walks between, the way upstream's generated Wander
/// routes pick their stops: a seeded random point of the area, snapped onto
/// the mesh, kept only while still inside the area and reachable from where
/// the player stands, and not too near the spot before it. The mesh is asked
/// through <c>snap</c> and <c>reachable</c>, so the choice itself is plain
/// code and the seed decides it.
/// </summary>
public sealed class ForeverSpotPicker
{
    /// <summary>Draws per spot before the picker reports none.</summary>
    public const int MaxAttempts = 40;
    /// <summary>Consecutive spots are at least this far apart, or less in a small area.</summary>
    public const float MaxSpacingYards = 25;
    /// <summary>A walk from outside takes the nearest of this many spots inside.</summary>
    public const int ApproachCandidates = 6;
    // In a small area the spacing is this share of its narrower side.
    private const float SpacingShare = 0.4f;

    private readonly ForeverWorldArea area;
    private readonly Random random;
    private Vector2? last;

    public ForeverSpotPicker(ForeverWorldArea area, int seed)
    {
        this.area = area;
        random = new(seed);
        Spacing = MathF.Min(MaxSpacingYards, SpacingShare * MathF.Min(area.Width, area.Height));
    }

    /// <summary>How far apart consecutive spots are at least.</summary>
    public float Spacing { get; }

    /// <summary>A spot inside the area, on the mesh and reachable, away from the last one; null when none turns up.</summary>
    public Vector2? Next(Func<Vector2, Vector2?> snap, Func<Vector2, bool> reachable)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (Candidate(snap) is not { } spot ||
                last is { } previous && Vector2.Distance(previous, spot) < Spacing ||
                !reachable(spot))
                continue;
            last = spot;
            return spot;
        }
        return null;
    }

    /// <summary>
    /// The reachable spot nearest <paramref name="here"/> among a few inside
    /// the area: how a walk from outside enters it. Only the nearest are
    /// asked whether they are reachable, since a long way costs.
    /// </summary>
    public Vector2? Nearest(Vector2 here, Func<Vector2, Vector2?> snap, Func<Vector2, bool> reachable)
    {
        List<Vector2> candidates = [];
        for (int attempt = 0; attempt < MaxAttempts && candidates.Count < ApproachCandidates; attempt++)
        {
            if (Candidate(snap) is { } spot)
                candidates.Add(spot);
        }
        foreach (Vector2 spot in candidates.OrderBy(spot => Vector2.DistanceSquared(here, spot)))
        {
            if (!reachable(spot))
                continue;
            last = spot;
            return spot;
        }
        return null;
    }

    // A random point of the area snapped onto the mesh, still inside the area.
    private Vector2? Candidate(Func<Vector2, Vector2?> snap) =>
        snap(area.RandomPoint(random)) is { } spot && area.Contains(spot) ? spot : null;
}
