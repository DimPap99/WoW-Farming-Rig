using System;
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

    /// <summary>The minimap's tracking placed the unit as it died: the body lies at that one point.</summary>
    public bool Tracked => Near == Far;

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
/// unit, and how far it could have been. Where the minimap shows tracked
/// units (a Hunter's tracking), the dot along that bearing places the unit to
/// within a few yards instead. Decisions over the frames it is shown; it
/// presses nothing.
/// </summary>
public sealed class ForeverCorpses
{
    /// <summary>A unit within duel distance lies at most this far away.</summary>
    public const float CloseYards = 8;
    /// <summary>A unit within follow distance lies at most this far away.</summary>
    public const float FollowYards = 28;
    /// <summary>The farthest a plate is drawn when the roster does not say.</summary>
    public const float PlateYards = 45;
    // A sighting older than this says nothing about where a unit fell.
    private const int SightingMs = 10000;
    // A body is looted within this long or not at all.
    private const int CorpseMs = 180000;
    // A tracked dot belongs to a sighting when it lies this many yards across
    // the bearing, at least this angle off it, and as far as the nameplate
    // allows give or take the same yards: a dot is placed to a couple of yards.
    private const float TrackedSlackYards = 4;
    private const double TrackedMinAngle = 6 * Math.PI / 180;
    /// <summary>
    /// The target's marker shows farther out than other plates, which end at
    /// the plate distance: its dot is matched out to this far.
    /// </summary>
    public const float TargetTrackedYards = 100;

    private readonly record struct Sighting(Vector2 From, double Bearing, float NearYards,
        float FarYards, long At, Vector2? Tracked);

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
            IReadOnlyList<Vector2>? blips = bars.TrackedBlips;
            if (bars.TargetIdentity is { Present: true } target && bars.TargetScreenPosition is { } marker)
                See(target.GuidHash1, target.GuidHash2, here, map, marker, bars.Attack?.TargetClose, null, farthest,
                    blips, isTarget: true, now);
            if (bars.Nameplates is { } roster)
            {
                for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
                {
                    if (roster.GetSlot(slot) is { IsEmpty: false, HasIdentity: true, Screen: { } plateMarker } plate)
                        See(plate.GuidHash1, plate.GuidHash2, here, map, plateMarker, plate.Close,
                            plate.WithinFollowDistance, farthest, blips, plate.IsTarget == true, now);
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

    /// <summary>
    /// Where a unit seen in the last few seconds stands, as far as its
    /// nameplate tells: on the segment along the bearing it was last seen at,
    /// from the nearest to the farthest it could be. The game reports no
    /// position for another unit, so this is the estimate a walk toward it uses.
    /// </summary>
    public bool TryLocate(int hash1, int hash2, out Vector2 near, out Vector2 far)
    {
        if (!sightings.TryGetValue((hash1, hash2), out Sighting seen))
        {
            near = far = default;
            return false;
        }
        // The minimap's tracking placed it: one point.
        if (seen.Tracked is { } point)
        {
            near = far = point;
            return true;
        }
        near = ForeverNavigator.Along(seen.From, seen.Bearing, seen.NearYards);
        far = ForeverNavigator.Along(seen.From, seen.Bearing, seen.FarYards);
        return true;
    }

    /// <summary>The body was looted, found empty or given up.</summary>
    public void Remove(int hash1, int hash2) => corpses.RemoveAll(corpse => corpse.Is(hash1, hash2));

    public void Clear() => corpses.Clear();

    // Within duel distance a unit lies within 8 yards; beyond it but within
    // follow distance, 8 to 28; beyond that, out to the farthest plate.
    private void See(int hash1, int hash2, Vector2 here, VisualPositionSample map,
        VisualScreenPointSample marker, bool? close, bool? follow, float farthest,
        IReadOnlyList<Vector2>? blips, bool isTarget, long now)
    {
        float far = close == true ? CloseYards : follow == true ? FollowYards : farthest;
        float near = Math.Min(close == true ? 0 : follow == false ? FollowYards : close == false ? CloseYards : 0, far);
        double bearing = ForeverNavigator.BearingOf(map, marker);
        // Beyond follow distance the target may be past the plate distance too.
        float matchFar = isTarget && close != true && follow != true ? Math.Max(far, TargetTrackedYards) : far;
        sightings[(hash1, hash2)] = new(here, bearing, near, far, now,
            TrackedAlong(blips, here, bearing, near, matchFar));
    }

    /// <summary>
    /// The tracked dot that lies along <paramref name="bearing"/> from
    /// <paramref name="here"/>, as far as the nameplate allows: the unit's
    /// place. With several, the one nearest the bearing; null with none, as
    /// when the minimap tracks another kind of unit or nothing at all.
    /// </summary>
    public static Vector2? TrackedAlong(IReadOnlyList<Vector2>? blips, Vector2 here, double bearing,
        float near, float far)
    {
        if (blips is null)
            return null;
        Vector2? best = null;
        double bestAngle = double.MaxValue;
        foreach (Vector2 blip in blips)
        {
            float distance = Vector2.Distance(here, blip);
            if (distance < near - TrackedSlackYards || distance > far + TrackedSlackYards)
                continue;
            double off = Math.Atan2(blip.Y - here.Y, blip.X - here.X) - bearing;
            double angle = Math.Abs(Math.Atan2(Math.Sin(off), Math.Cos(off)));
            double allowed = Math.Max(TrackedMinAngle, Math.Atan2(TrackedSlackYards, Math.Max(distance, 1)));
            if (angle <= allowed && angle < bestAngle)
            {
                best = blip;
                bestAngle = angle;
            }
        }
        return best;
    }

    private void Died(int hash1, int hash2, long now)
    {
        if (corpses.Exists(corpse => corpse.Is(hash1, hash2)) ||
            !TryLocate(hash1, hash2, out Vector2 near, out Vector2 far))
            return;
        corpses.Add(new(hash1, hash2, near, far, now));
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
