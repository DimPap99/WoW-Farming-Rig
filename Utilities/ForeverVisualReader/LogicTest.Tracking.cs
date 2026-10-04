using Core;
using Core.Forever;

using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // The minimap's tracked dots: found in the capture and placed in the
    // world, matched to a nameplate's bearing, and a body placed by them.
    private static void TrackingChecks(Action<string, bool, string> check)
    {
        // The default UI's minimap at 1080p: 94 pixels in radius, top right,
        // 233 yards across its radius at the widest zoom.
        const int frameWidth = 1920, frameHeight = 1080;
        VisualMinimap northUp = new(1792.0 / frameWidth, 156.0 / frameHeight, 94.0 / frameWidth, 233.33, false);
        (int left, int top, int size) = VisualMinimapReader.Region(northUp, frameWidth, frameHeight);
        double yardsPerPixel = 233.33 / 94;
        Vector2 player = new(100, 200);

        // Terrain grey, with dots at pixel offsets (right, down): gold for a
        // neutral unit, a dark red core for a hostile one, green for a friend.
        Bgra32 gold = new(230, 180, 10, 255), red = new(122, 0, 0, 255), green = new(27, 130, 0, 255);
        Bgra32[] Minimap(params (int Right, int Down, int Diameter)[] dots) => Painted(dots.Select(d => (d.Right, d.Down, d.Diameter, gold)).ToArray());
        Bgra32[] Painted(params (int Right, int Down, int Diameter, Bgra32 Colour)[] dots)
        {
            Bgra32[] region = new Bgra32[size * size];
            Array.Fill(region, new Bgra32(90, 110, 140, 255));
            double centerX = 1792 - left, centerY = 156 - top;
            foreach ((int right, int down, int diameter, Bgra32 colour) in dots)
            {
                double radius = diameter / 2.0;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x - (centerX + right), dy = y - (centerY + down);
                        if (dx * dx + dy * dy <= radius * radius)
                            region[y * size + x] = colour;
                    }
            }
            return region;
        }

        // North up: screen up is world +X (north), screen right is world -Y (east).
        List<Vector2> found = VisualMinimapReader.FindBlips(Minimap((20, -30, 8), (-40, 10, 8), (0, 0, 40), (100, 0, 8)),
            left, top, size, northUp, frameWidth, frameHeight, player, facing: 1.0);
        Vector2 ahead = player + new Vector2((float)(30 * yardsPerPixel), (float)(-20 * yardsPerPixel));
        Vector2 behindLeft = player + new Vector2((float)(-10 * yardsPerPixel), (float)(40 * yardsPerPixel));
        bool both = found.Count == 2 &&
            found.Any(blip => Vector2.Distance(blip, ahead) < 1.5f) &&
            found.Any(blip => Vector2.Distance(blip, behindLeft) < 1.5f);
        check("tracking: two dots become world points north up; a gold blob and gold beyond the rim are not dots",
            both, string.Join(" ", found.Select(blip => FormattableString.Invariant($"({blip.X:0.0},{blip.Y:0.0})"))));

        // A hostile unit's red dot counts, a friendly unit's green one does not,
        // and a red dot touching a gold one stays its own.
        found = VisualMinimapReader.FindBlips(Painted((20, -30, 5, red), (-40, 10, 8, green), (26, -30, 6, gold)),
            left, top, size, northUp, frameWidth, frameHeight, player, facing: 1.0);
        check("tracking: a hostile unit's red dot counts and a friendly unit's green one does not, each dot its own",
            found.Count == 2 && found.Any(blip => Vector2.Distance(blip, ahead) < 1.5f),
            string.Join(" ", found.Select(blip => FormattableString.Invariant($"({blip.X:0.0},{blip.Y:0.0})"))));

        // Rotating with the player facing west (+Y): screen up is west, screen right is north.
        VisualMinimap rotating = northUp with { Rotates = true };
        found = VisualMinimapReader.FindBlips(Minimap((20, -30, 8)), left, top, size, rotating,
            frameWidth, frameHeight, player, facing: Math.PI / 2);
        Vector2 turned = player + new Vector2((float)(20 * yardsPerPixel), (float)(30 * yardsPerPixel));
        check("tracking: on a rotating minimap a dot turns with the player's facing",
            found is [{ } one] && Vector2.Distance(one, turned) < 1.5f,
            string.Join(" ", found.Select(blip => FormattableString.Invariant($"({blip.X:0.0},{blip.Y:0.0})"))));

        // Matching a nameplate's bearing: north (0) from the player.
        Vector2[] dots = [new(130, 201), new(100, 230), new(103, 200)];
        check("tracking: the dot along the bearing and within the nameplate's distance is the unit's",
            ForeverCorpses.TrackedAlong(dots, player, 0, ForeverCorpses.CloseYards, ForeverCorpses.PlateYards) == new Vector2(130, 201) &&
            ForeverCorpses.TrackedAlong([new(100, 230)], player, 0, ForeverCorpses.CloseYards, ForeverCorpses.PlateYards) is null &&
            ForeverCorpses.TrackedAlong([new(103, 200)], player, 0, ForeverCorpses.CloseYards, ForeverCorpses.PlateYards) is null &&
            ForeverCorpses.TrackedAlong(null, player, 0, 0, ForeverCorpses.PlateYards) is null, "");

        // A tracked sighting places the unit, and its body, at one point.
        long t0 = Stopwatch.GetTimestamp();
        VisualTargetIdentity mob = new(11, 22, 7, false);
        VisualBarSample Seen(VisualDeathEvent death, bool target) =>
            new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320,
                Position: new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 0),
                WorldPosition: new(player.X, player.Y, 0, 1),
                TargetFlags: new(target, target, false, false, false),
                TargetIdentity: target ? mob : new(0, 0, 0, false),
                TargetScreenPosition: target ? new VisualScreenPointSample(0.5, 0.5) : null,
                Attack: new VisualAttackState(0, 0, false, false),
                LastDeathEvent: death) with { TrackedBlips = dots };
        ForeverCorpses ledger = new();
        ledger.Observe(Seen(new(5, 1, 2), target: true), t0);
        bool placed = ledger.TryLocate(mob.GuidHash1, mob.GuidHash2, out Vector2 near, out Vector2 far);
        ledger.Observe(Seen(new(6, 11, 22), target: false), t0 + Stopwatch.Frequency / 5);
        check("tracking: the target is placed at its dot, and its body lies there as it dies",
            placed && near == new Vector2(130, 201) && far == near &&
            ledger.Pending is [{ Tracked: true } body] && body.Near == near,
            FormattableString.Invariant($"{placed} ({near.X:0.0},{near.Y:0.0})-({far.X:0.0},{far.Y:0.0}), {ledger.Pending.Count} bodies"));
    }
}
