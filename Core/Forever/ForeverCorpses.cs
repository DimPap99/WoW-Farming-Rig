using System.Collections.Generic;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// A body to loot. It lies on the segment from <see cref="Near"/> to
/// <see cref="Far"/>: along the bearing the unit was last seen at, from the
/// nearest to the farthest it could have been.
/// </summary>
public sealed record ForeverCorpse(int GuidHash1, int GuidHash2, Vector2 Near, Vector2 Far, long DiedAt)
{
    public bool Is(int hash1, int hash2) => GuidHash1 == hash1 && GuidHash2 == hash2;

    /// <summary>How far <paramref name="point"/> is from where the body may lie.</summary>
    public float DistanceFrom(Vector2 point)
    {
        Vector2 along = Far - Near;
        float length = along.LengthSquared();
        float t = length == 0 ? 0 : Vector2.Dot(point - Near, along) / length;
        return Vector2.Distance(point, Near + along * System.Math.Clamp(t, 0, 1));
    }
}

/// <summary>
/// The units killed in the current fight and where they fell, so they can be
/// looted once it is over. A corpse has no nameplate (the client fades the
/// plate out and clears the target as the unit dies), so a body is found again
/// from the unit's last sighting: where the player stood, the bearing to the
/// unit, and how far it could have been. Decisions over the frames it is
/// shown; it presses nothing.
/// </summary>
public sealed class ForeverCorpses
{
    /// <summary>A unit within duel distance lies at most this far away.</summary>
    public const float CloseYards = 8;
    /// <summary>The farthest a plate is drawn when the roster does not say.</summary>
    public const float PlateYards = 45;
    // A sighting older than this says nothing about where a unit fell.
    private const int SightingMs = 10000;
    // A body is looted within this long or not at all.
    private const int CorpseMs = 180000;

    private readonly record struct Sighting(Vector2 From, double Bearing, float NearYards,
        float FarYards, long At);

    private readonly Dictionary<(int, int), Sighting> sightings = [];
    private readonly List<ForeverCorpse> corpses = [];
    private int? deathSequence;

    /// <summary>The bodies still to loot, oldest first.</summary>
    public IReadOnlyList<ForeverCorpse> Pending => corpses;

    public bool Any => corpses.Count > 0;

    /// <inheritdoc cref="Observe(VisualBarSample, long)"/>
    public void Observe(VisualBarSample bars) => Observe(bars, GetTimestamp());

    /// <summary>
    /// Notes where the target and every plate with an identity and a marker
    /// are seen on this frame, and turns a new death of a recently seen unit
    /// into a corpse. The death latch read on the first frame is history,
    /// not a kill of this run.
    /// </summary>
    /// <param name="now">The frame's time, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</param>
    public void Observe(VisualBarSample bars, long now)
    {
        if (bars.WorldPosition is { } world && bars.Position is { } map)
        {
            Vector2 here = new((float)world.X, (float)world.Y);
            float farthest = bars.Nameplates is { MaxDistance: > 0 } shown ? shown.MaxDistance : PlateYards;
            if (bars.TargetIdentity is { Present: true } target && bars.TargetScreenPosition is { } marker)
                See(target.GuidHash1, target.GuidHash2, here, map, marker, bars.Attack?.TargetClose, farthest, now);
            if (bars.Nameplates is { } roster)
            {
                for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
                {
                    if (roster.GetSlot(slot) is { IsEmpty: false, HasIdentity: true, Screen: { } plateMarker } plate)
                        See(plate.GuidHash1, plate.GuidHash2, here, map, plateMarker, plate.Close, farthest, now);
                }
            }
        }

        if (bars.LastDeathEvent is { } death && death.Sequence != deathSequence)
        {
            if (deathSequence is not null && death.Sequence > 0)
                Died(death.GuidHash1, death.GuidHash2, now);
            deathSequence = death.Sequence;
        }
        Prune(now);
    }

    /// <summary>The pending body nearest to <paramref name="here"/>.</summary>
    public ForeverCorpse? Nearest(Vector2 here)
    {
        ForeverCorpse? nearest = null;
        float best = float.MaxValue;
        foreach (ForeverCorpse corpse in corpses)
        {
            float distance = corpse.DistanceFrom(here);
            if (distance < best)
            {
                best = distance;
                nearest = corpse;
            }
        }
        return nearest;
    }

    /// <summary>The body was looted, found empty or given up.</summary>
    public void Remove(int hash1, int hash2) => corpses.RemoveAll(corpse => corpse.Is(hash1, hash2));

    public void Clear() => corpses.Clear();

    private void See(int hash1, int hash2, Vector2 here, VisualPositionSample map,
        VisualScreenPointSample marker, bool? close, float farthest, long now)
    {
        (float near, float far) = close switch
        {
            true => (0f, CloseYards),
            false => (CloseYards, farthest),
            null => (0f, farthest),
        };
        sightings[(hash1, hash2)] = new(here, ForeverNavigator.BearingOf(map, marker), near, far, now);
    }

    private void Died(int hash1, int hash2, long now)
    {
        if (!sightings.TryGetValue((hash1, hash2), out Sighting seen) ||
            corpses.Exists(corpse => corpse.Is(hash1, hash2)))
            return;
        corpses.Add(new(hash1, hash2,
            ForeverNavigator.Along(seen.From, seen.Bearing, seen.NearYards),
            ForeverNavigator.Along(seen.From, seen.Bearing, seen.FarYards), now));
    }

    private void Prune(long now)
    {
        double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        List<(int, int)>? stale = null;
        foreach (((int, int) key, Sighting seen) in sightings)
        {
            if ((now - seen.At) / ticksPerMs > SightingMs)
                (stale ??= []).Add(key);
        }
        if (stale is not null)
            foreach ((int, int) key in stale)
                sightings.Remove(key);
        corpses.RemoveAll(corpse => (now - corpse.DiedAt) / ticksPerMs > CorpseMs);
    }
}
