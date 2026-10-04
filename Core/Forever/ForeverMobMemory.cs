using System.Collections.Generic;
using System.Numerics;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// A mob the run saw: its nameplate as last seen, where that placed it, and when.
/// </summary>
public readonly record struct ForeverSeenMob(VisualNameplate Plate, Vector2 At, long SeenAt);

/// <summary>
/// The mobs the run has seen lately and where, so a task can go back to one
/// the camera no longer shows: the client draws a nameplate only for a unit in
/// the camera's view, so turning away or walking on loses it. Every plate with
/// an identity is placed as it shows (<see cref="ForeverCorpses.TryPlace"/>:
/// its marker's bearing and range band, or the minimap's tracked dot along
/// it) and kept a while. A unit reported dead is dropped, and so is one that a
/// look round passing near where it stood did not see again. Needs no class
/// ability; tracking only sharpens the places. Decisions over the frames it
/// is shown; it presses nothing.
/// </summary>
public sealed class ForeverMobMemory
{
    /// <summary>A mob not seen for this long is forgotten.</summary>
    public const int RememberMs = 120000;

    private readonly Dictionary<(int, int), ForeverSeenMob> seen = [];
    private int? deathSequence;

    /// <summary>The mobs remembered, in no order.</summary>
    public IReadOnlyCollection<ForeverSeenMob> All => seen.Values;

    /// <inheritdoc cref="Observe(VisualBarSample, long)"/>
    public void Observe(VisualBarSample bars) => Observe(bars, GetTimestamp());

    /// <summary>Notes every plate this frame places, and forgets a unit reported dead.</summary>
    /// <param name="now">The frame's time, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</param>
    public void Observe(VisualBarSample bars, long now)
    {
        if (bars.Nameplates is { } roster)
        {
            for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
            {
                if (roster.GetSlot(slot) is { IsEmpty: false, HasIdentity: true, Dead: not true } plate &&
                    ForeverCorpses.TryPlace(bars, plate, out Vector2 near, out Vector2 far))
                    seen[(plate.GuidHash1, plate.GuidHash2)] = new(plate, Vector2.Lerp(near, far, 0.5f), now);
            }
        }
        if (bars.LastDeathEvent is { } death && death.Sequence != deathSequence)
        {
            seen.Remove((death.GuidHash1, death.GuidHash2));
            deathSequence = death.Sequence;
        }
        double ticksPerMs = Frequency / 1000.0;
        List<(int, int)>? stale = null;
        foreach (((int, int) key, ForeverSeenMob mob) in seen)
        {
            if ((now - mob.SeenAt) / ticksPerMs > RememberMs)
                (stale ??= []).Add(key);
        }
        if (stale is not null)
            foreach ((int, int) key in stale)
                seen.Remove(key);
    }

    /// <summary>
    /// A look round from <paramref name="here"/>, begun at <paramref name="since"/>,
    /// would have shown every mob within <paramref name="yards"/>: one not seen
    /// since is no longer where it stood, and is forgotten.
    /// </summary>
    public void Forget(Vector2 here, float yards, long since)
    {
        List<(int, int)>? gone = null;
        foreach (((int, int) key, ForeverSeenMob mob) in seen)
        {
            if (mob.SeenAt < since && Vector2.Distance(here, mob.At) <= yards)
                (gone ??= []).Add(key);
        }
        if (gone is not null)
            foreach ((int, int) key in gone)
                seen.Remove(key);
    }

    public void Clear() => seen.Clear();
}
