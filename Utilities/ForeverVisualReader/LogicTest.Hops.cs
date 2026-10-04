using Core.Forever;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // When a walking character hops: once per stall, and in rows of one to
    // three now and then where the state allows idle hops.
    private static void HopChecks(Action<string, bool, string> check)
    {
        long t0 = Stopwatch.GetTimestamp();
        long At(double seconds) => t0 + (long)(seconds * Stopwatch.Frequency);

        ForeverHops snag = new(new Random(1));
        snag.Started(At(0));
        check("hops: no snag hop before the stall lasted 1.5 s", !snag.Snag(1.4), "");
        check("hops: one snag hop once it did, and one per stall", snag.Snag(1.6) && !snag.Snag(2.5), "");
        snag.Progressed();
        check("hops: after progress the next stall gets its own hop", snag.Snag(1.6), "");

        // Ten minutes of walking, sampled every 50 ms, with a fixed seed.
        ForeverHops idle = new(new Random(7));
        idle.Started(At(0));
        List<double> hops = [];
        for (int i = 0; i < 12000; i++)
        {
            double s = i * 0.05;
            if (idle.Idle(At(s)))
                hops.Add(s);
        }
        // Hops less than a second apart are one row.
        List<List<double>> rows = [];
        foreach (double hop in hops)
        {
            if (rows.Count > 0 && hop - rows[^1][^1] < 1.0)
                rows[^1].Add(hop);
            else
                rows.Add([hop]);
        }
        bool landing = rows.All(row => row.Zip(row.Skip(1), (a, b) => b - a)
            .All(gap => gap >= 0.65 - 0.051 && gap <= 0.9 + 0.051));
        check("hops: idle rows of one to three, each hop as the last one lands, the first after 4 s",
            hops.Count > 0 && hops[0] >= 4 - 0.051 && landing &&
            rows.All(row => row.Count is >= 1 and <= 3) &&
            rows.Any(row => row.Count == 1) && rows.Any(row => row.Count > 1),
            $"rows={rows.Count} sizes={string.Join("", rows.Select(row => row.Count))}");
        check("hops: about one roll in four, every 4 s, starts a row", rows.Count is >= 20 and <= 60,
            $"rows={rows.Count} in 600 s");

        ForeverHops stopped = new(new Random(7));
        stopped.Started(At(0));
        stopped.Stopped();
        bool any = false;
        for (int i = 0; i < 1200; i++)
            any |= stopped.Idle(At(i * 0.05));
        check("hops: none once walking stopped", !any, "");
    }
}
