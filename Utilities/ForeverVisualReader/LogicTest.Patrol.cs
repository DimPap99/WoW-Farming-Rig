using Core;
using Core.Forever;

using SharedLib.Forever;

using System;
using System.Diagnostics;
using System.Numerics;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // Patrolling an area: the mobs seen are remembered and forgotten, and
    // with nothing in view the patrol leads to a remembered mob first, then a
    // tracked dot, then the part of the area seen least. The mesh is a
    // stand-in where every point is walkable and reachable.
    private static void PatrolChecks(Action<string, bool, string> check)
    {
        ForeverWorldArea square = new([new(0, 0), new(100, 0), new(100, 100), new(0, 100)]);
        static Vector2? Anywhere(Vector2 hint) => hint;
        static bool Reachable(Vector2 spot) => true;
        ClassConfiguration config = new();
        ForeverExclusions none = new();
        long now = Stopwatch.GetTimestamp();
        long Later(double seconds) => now + (long)(seconds * Stopwatch.Frequency);

        // The player at (10, 10) facing +X; a plate at screen centre lies
        // along +X, and beyond follow distance its tracked dot at 35 yards
        // places it exactly.
        VisualBarSample At(Vector2 here, VisualNameplateRoster? roster = null, Vector2[]? dots = null) =>
            new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
            {
                WorldPosition = new(here.X, here.Y, 0, 1),
                Position = new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 0),
                PlayerIdentity = new(3, 8, 12),
                TargetFlags = new(false, false, false, false, false),
                Nameplates = roster ?? Roster(),
                TrackedBlips = dots ?? [],
            };
        Vector2 start = new(10, 10);
        VisualNameplate mob = Plate(1, hash: 50) with { Close = false, WithinFollowDistance = false };
        Vector2 mobAt = new(45, 10);

        ForeverMobMemory memory = new();
        memory.Observe(At(start, Roster(mob), [mobAt]), now);
        bool remembered = memory.All.Count == 1 && Vector2.Distance(Single(memory).At, mobAt) < 0.01f;
        ForeverPatrol patrol = new(square, memory);
        bool led = patrol.TryRemembered(At(start), config, none, now, out ForeverSeenMob back) && back.Plate.GuidHash1 == 50;
        bool notWhileShown = !patrol.TryRemembered(At(start, Roster(mob)), config, none, now, out _);
        check("memory: a plate placed as it shows is remembered, and the patrol leads back to it once out of view",
            remembered && led && notWhileShown, $"remembered={remembered}, led={led}, notWhileShown={notWhileShown}");

        ForeverMobMemory looked = new();
        looked.Observe(At(start, Roster(mob), [mobAt]), now);
        looked.Forget(new(40, 10), ForeverPatrol.LookYards, since: Later(1));
        ForeverMobMemory kept = new();
        kept.Observe(At(start, Roster(mob), [mobAt]), now);
        kept.Forget(new(100, 100), ForeverPatrol.LookYards, since: Later(1));
        ForeverMobMemory died = new();
        died.Observe(At(start, Roster(mob), [mobAt]) with { LastDeathEvent = new(1, 0, 0) }, now);
        died.Observe(At(start) with { LastDeathEvent = new(2, 50, 51) }, Later(1));
        ForeverMobMemory aged = new();
        aged.Observe(At(start, Roster(mob), [mobAt]), now);
        aged.Observe(At(start), Later(ForeverMobMemory.RememberMs / 1000.0 + 1));
        check("memory: a mob is forgotten when a look round near it did not see it, when it died, or after a while; not by a look far off",
            looked.All.Count == 0 && kept.All.Count == 1 && died.All.Count == 0 && aged.All.Count == 0,
            $"looked={looked.All.Count}, kept={kept.All.Count}, died={died.All.Count}, aged={aged.All.Count}");

        // Tracked dots: one in sight already, one far inside, one outside.
        Vector2 near = new(30, 10), far = new(80, 80), beyond = new(150, 50);
        ForeverPatrol dots = new(square, new ForeverMobMemory());
        bool tracked = dots.TryTracked(At(start, dots: [near, far, beyond]), now, out Vector2 dot) && dot == far;
        dots.Visited(far, now);
        bool passedOver = !dots.TryTracked(At(start, dots: [near, far, beyond]), Later(1), out _);
        bool again = dots.TryTracked(At(start, dots: [near, far, beyond]), Later(ForeverPatrol.VisitedMs / 1000.0 + 1), out _);
        check("patrol: a tracked dot inside the area beyond sight leads; one in sight or outside does not, nor one just visited for a while",
            tracked && passedOver && again, $"tracked={tracked} ({dot}), passedOver={passedOver}, again={again}");
        Vector2 edge = new(100 + ForeverPatrol.EdgeYards - 5, 50);
        check("patrol: a dot just past the area's edge still leads; a patrol covers what stands around its area",
            new ForeverPatrol(square, new ForeverMobMemory()).TryTracked(At(start, dots: [edge]), now, out Vector2 past) &&
            past == edge, "");

        // The cells seen least: none seen yet leads to the nearest; a look
        // round covers 40 yards about it; once all are seen, the oldest.
        ForeverPatrol cells = new(square, new ForeverMobMemory());
        bool nearest = cells.TryLeastCovered(start, Anywhere, Reachable, out Vector2 first) &&
            Vector2.Distance(first, new(12.5f, 12.5f)) < 0.01f;
        cells.Cover(start, ForeverPatrol.LookYards, now);
        bool onward = cells.TryLeastCovered(start, Anywhere, Reachable, out Vector2 second) &&
            Math.Abs(Vector2.Distance(start, second) - Vector2.Distance(start, new(62.5f, 12.5f))) < 0.01f;
        cells.Cover(new(50, 50), 200, Later(10));
        cells.Cover(new(87.5f, 87.5f), 1, Later(5));
        bool oldest = cells.TryLeastCovered(start, Anywhere, Reachable, out Vector2 stale) &&
            Vector2.Distance(stale, new(87.5f, 87.5f)) < 0.01f;
        check("patrol: the nearest cell not seen yet, then onward past what a look round covered, then the one seen longest ago",
            nearest && onward && oldest && cells.Coverage == (16, 16),
            $"first={first}, second={second}, stale={stale}, coverage={cells.Coverage}");

        // The order with nothing in view: a remembered mob, a tracked dot, the cell seen least.
        bool rememberedFirst = patrol.TryNext(At(start, dots: [far]), config, none, Anywhere, Reachable, now,
            out ForeverPatrolGoal toMob) && toMob.Lead == ForeverPatrolLead.Remembered;
        ForeverPatrol unremembered = new(square, new ForeverMobMemory());
        bool trackedNext = unremembered.TryNext(At(start, dots: [far]), config, none, Anywhere, Reachable, now,
            out ForeverPatrolGoal toDot) && toDot.Lead == ForeverPatrolLead.Tracked && toDot.Where == far;
        bool coverageLast = unremembered.TryNext(At(start), config, none, Anywhere, Reachable, now,
            out ForeverPatrolGoal toCell) && toCell.Lead == ForeverPatrolLead.Uncovered;
        bool unreachableSkipped = new ForeverPatrol(square, memory).TryNext(At(start, dots: [far]), config, none,
            Anywhere, spot => Vector2.Distance(spot, mobAt) > 1, now, out ForeverPatrolGoal around) &&
            around.Lead == ForeverPatrolLead.Tracked;
        check("patrol: with nothing in view a remembered mob leads first, then a tracked dot, then the cell seen least; one out of reach is passed over",
            rememberedFirst && trackedNext && coverageLast && unreachableSkipped,
            $"{toMob.Lead} {toDot.Lead} {toCell.Lead} {around.Lead}");
    }

    // A travel that pulls OnTheWay takes a unit near the way still to walk,
    // a little ahead; not one aside, behind, or far down the way.
    private static void OnTheWayChecks(Action<string, bool, string> check)
    {
        Vector2[] way = [new(50, 0), new(100, 0), new(150, 0)];
        Vector2 here = new(0, 0);
        float From(Vector2 point) => ForeverWalker.FromTheWayAhead(way, 0, here, point, ForeverTravel.AheadYards);
        float beside = From(new(60, 10)), aside = From(new(60, 40)), behind = From(new(-30, 0)), farDown = From(new(140, 5));
        check("travel: on the way is a unit beside the way ahead; not one well aside, behind, or past the next 80 yards",
            beside <= ForeverTravel.OnTheWayYards && aside > ForeverTravel.OnTheWayYards &&
            behind > ForeverTravel.OnTheWayYards && farDown > ForeverTravel.OnTheWayYards,
            $"beside={beside:F1} aside={aside:F1} behind={behind:F1} farDown={farDown:F1}");
    }

    private static ForeverSeenMob Single(ForeverMobMemory memory)
    {
        foreach (ForeverSeenMob mob in memory.All)
            return mob;
        return default;
    }
}
