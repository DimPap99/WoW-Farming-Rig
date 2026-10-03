using System;
using System.Collections.Generic;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// Units the run set aside for a while after it could not select or reach
/// them. A unit is known by its GUID hashes; while the client protects the
/// GUID, by its roster slot and incarnation, which last as long as its plate.
/// </summary>
public sealed class ForeverExclusions
{
    private readonly Dictionary<(int, int), long> until = [];

    public void Exclude(VisualNameplate plate, TimeSpan duration) =>
        until[Key(plate)] = GetTimestamp() + Ticks(duration);

    public void Exclude(VisualTargetIdentity identity, TimeSpan duration)
    {
        if (identity.Present)
            until[(identity.GuidHash1, identity.GuidHash2)] = GetTimestamp() + Ticks(duration);
    }

    public bool IsExcluded(VisualNameplate plate)
    {
        (int, int) key = Key(plate);
        if (!until.TryGetValue(key, out long end))
            return false;
        if (GetTimestamp() < end)
            return true;
        until.Remove(key);
        return false;
    }

    // Slot and incarnation are negated so they never meet a GUID hash pair.
    private static (int, int) Key(VisualNameplate plate) => plate.HasIdentity
        ? (plate.GuidHash1, plate.GuidHash2)
        : (-plate.Slot, -plate.Incarnation);

    private static long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * Frequency);
}
