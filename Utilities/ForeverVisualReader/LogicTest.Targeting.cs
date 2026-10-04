using Core;
using Core.Forever;

using Game;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    /// <summary>A target that is always in front; Plan decides without facing.</summary>
    private sealed class InFront : IForeverFacing
    {
        public bool FaceTarget(VisualBarSample bars) => true;
    }

    /// <summary>Counts pointer moves and clicks; arrives when told to.</summary>
    private sealed class FakePointer : IMousePointer
    {
        public bool Arrive { get; set; }
        public int Moves { get; private set; }
        public int Clicks { get; private set; }
        public double LastX { get; private set; }

        public bool MoveToward(double x, double y)
        {
            Moves++;
            LastX = x;
            return Arrive;
        }

        public void Click() => Clicks++;
    }

    // A plate on the Paladin frame (player level 12): Judgement is slot 2,
    // Holy Strike slot 3.
    private static VisualNameplate Plate(int slot, int incarnation = 1, int hash = 0, int level = 12,
        bool? attackable = true, bool? dead = false, bool? player = false, bool? tapDenied = false,
        bool? isTarget = false, bool? mouseover = false, bool? targetsPlayer = false,
        double? alpha = 1.0, bool? inCombat = false, bool? judgement = null, bool? strike = null,
        double? screenX = 0.5, double screenY = 0.5)
    {
        int ranges = (judgement is { } j ? (j ? 1 : 2) << 2 : 0) | (strike is { } s ? (s ? 1 : 2) << 4 : 0);
        return new VisualNameplate(slot, incarnation, hash, hash == 0 ? 0 : hash + 1, level,
            attackable, dead, player, tapDenied, inCombat, isTarget, mouseover, BehindCamera: false,
            targetsPlayer, Reaction: 2, OnThreatTable: targetsPlayer, ThreatStatus: targetsPlayer == true ? 3 : null,
            alpha, new VisualSlotRanges(ranges))
        {
            Screen = screenX is { } x ? new VisualScreenPointSample(x, screenY) : null
        };
    }

    private static VisualNameplateRoster Roster(params VisualNameplate[] plates)
    {
        VisualNameplate?[] slots = new VisualNameplate?[VisualNameplateRoster.SlotCount];
        for (int slot = 1; slot <= slots.Length; slot++)
            slots[slot - 1] = new VisualNameplate(slot, 0, 0, 0, 0, null, null, null, null, null, null,
                null, null, null, null, null, null, null, default);
        foreach (VisualNameplate plate in plates)
            slots[plate.Slot - 1] = plate;
        return new(slots, plates.Length, plates.Length, 0, true, true, 45);
    }

    private static void TargetingChecks(Action<string, bool, string> check, ClassConfiguration config,
        Func<bool, bool, VisualBarSample> frame, IForeverCombatRoutine combat)
    {
        VisualBarSample idle = frame(false, false);
        VisualBarSample fighting = frame(false, true);
        VisualPlayerIdentity player = idle.PlayerIdentity!.Value;
        ForeverExclusions none = new();

        // In combat the attacker to take; out of combat the cheapest pull worth taking.
        string Chosen(VisualBarSample bars, ForeverExclusions? excluded = null) =>
            bars.TargetFlags is { PlayerCombat: true }
                ? ForeverTargetPolicy.TryChooseAttacker(bars, config, combat, out VisualNameplate attacker)
                    ? $"slot {attacker.Slot}" : "none"
                : ForeverPullOptions.TryBest(ForeverPullOptions.Of(bars, config, combat, excluded ?? none), out ForeverPullOption pull)
                    ? $"slot {pull.Plate.Slot}" : "none";
        string Options(VisualBarSample bars) =>
            string.Join("; ", ForeverPullOptions.Of(bars, config, combat, none));

        // Eligibility, shared by plates and the selected unit.
        check("policy: a living attackable mob of the player's level is eligible",
            ForeverTargetPolicy.IsEligible(Plate(1), player, config), "");
        check("policy: above the profile's level window is not",
            !ForeverTargetPolicy.IsEligible(Plate(1, level: 12 + config.NPCMaxLevels_Above + 1), player, config), "");
        check("policy: below the profile's level window is not",
            !ForeverTargetPolicy.IsEligible(Plate(1, level: 12 - config.NPCMaxLevels_Below - 1), player, config), "");
        check("policy: tapped by someone else, a player, dead or of unknown attackability is not",
            !ForeverTargetPolicy.IsEligible(Plate(1, tapDenied: true), player, config) &&
            !ForeverTargetPolicy.IsEligible(Plate(1, player: true), player, config) &&
            !ForeverTargetPolicy.IsEligible(Plate(1, dead: true), player, config) &&
            !ForeverTargetPolicy.IsEligible(Plate(1, attackable: null), player, config), "");
        VisualNameplate elite = Plate(1) with { Classification = UnitClassification.Elite };
        check("policy: an elite is not, unless it fights the player; a rare or unknown kind is",
            !ForeverTargetPolicy.IsEligible(elite, player, config) &&
            ForeverTargetPolicy.IsEligible(elite with { TargetsPlayer = true }, player, config) &&
            ForeverTargetPolicy.IsEligible(Plate(1) with { Classification = UnitClassification.Rare }, player, config) &&
            ForeverTargetPolicy.IsEligible(Plate(1), player, config), "");
        UnitClassification mask = config.TargetMask;
        config.TargetMask |= UnitClassification.Elite;
        bool eliteAllowed = ForeverTargetPolicy.IsEligible(elite, player, config);
        config.TargetMask = mask;
        check("policy: a profile whose TargetMask includes elites takes them", eliteAllowed, "");
        VisualNameplate strong = Plate(1, level: 12 + config.NPCMaxLevels_Above + 2);
        check("policy: above the level window a unit is not pulled, but fought once it attacks the player or the pet",
            !ForeverTargetPolicy.IsEligible(strong, player, config) &&
            ForeverTargetPolicy.IsEligible(strong with { TargetsPlayer = true }, player, config) &&
            ForeverTargetPolicy.IsEligible(strong with { TargetsPet = true }, player, config) &&
            ForeverTargetPolicy.IsEligible(strong with { TargetsPet = true, TapDenied = true }, player, config) &&
            !ForeverTargetPolicy.IsEligible(strong with { TargetsPet = true, Dead = true }, player, config) &&
            !ForeverTargetPolicy.IsEligible(strong with { TargetsPet = true, IsPlayer = true }, player, config), "");
        {
            // The selected unit: the same rules, read from its own plate.
            VisualBarSample selected = frame(true, true);
            VisualBarSample Strong(bool attacks) => selected with
            {
                TargetIdentity = new(111, 222, strong.Level, false),
                Nameplates = Roster(strong with { IsTarget = true, TargetsPet = attacks })
            };
            check("policy: a selected unit above the level window is fought only while it fights the player or the pet",
                !ForeverTargetPolicy.TryEligibleTarget(Strong(attacks: false), config, out _) &&
                ForeverTargetPolicy.TryEligibleTarget(Strong(attacks: true), config, out _), "");
        }

        // Choice among the plates in view.
        VisualNameplate opener = Plate(1, hash: 10, judgement: true, screenY: 0.50);
        VisualNameplate nearer = Plate(2, hash: 20, screenY: 0.45);
        VisualNameplate hidden = Plate(3, hash: 30, judgement: true, alpha: 0.24, screenY: 0.40);
        string pick = Chosen(idle with { Nameplates = Roster(opener, nearer, hidden) });
        check("choice: a unit an opener reaches beats a nearer one and an occluded one", pick == "slot 1", pick);
        ForeverExclusions setAside = new();
        setAside.Exclude(opener, TimeSpan.FromMinutes(1));
        pick = Chosen(idle with { Nameplates = Roster(opener, nearer, hidden) }, setAside);
        check("choice: a set-aside unit is passed over; one the camera sees beats an occluded opener", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, screenY: 0.55), Plate(2, hash: 20, screenY: 0.45)) });
        check("choice: otherwise the lowest on screen, the nearest", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(opener, Plate(2, hash: 20, isTarget: true, judgement: true, screenY: 0.65)) });
        check("choice: of two alike the current target goes first: it needs no click", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, judgement: false, strike: true)) });
        check("choice: out of combat a unit too close for every opener is passed over", pick == "none", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, judgement: true, screenX: null)) });
        check("choice: a plate without a marker on screen cannot be clicked", pick == "none", pick);
        pick = Chosen(fighting with { Nameplates = Roster(opener, Plate(2, hash: 20, targetsPlayer: true, screenY: 0.75)) });
        check("choice: in combat only a unit that fights the player", pick == "slot 2", pick);
        pick = Chosen(fighting with { Nameplates = Roster(opener) });
        check("choice: in combat with no attacker in view, nothing", pick == "none", pick);
        VisualNameplate hitter = Plate(2, hash: 20, targetsPlayer: true, screenY: 0.75);
        ForeverExclusions asideAttacker = new();
        asideAttacker.Exclude(hitter, TimeSpan.FromMinutes(1));
        pick = Chosen(fighting with { Nameplates = Roster(hitter) }, asideAttacker);
        check("choice: in combat a unit set aside earlier is taken once it attacks", pick == "slot 2", pick);
        pick = Chosen(fighting with { Nameplates = Roster(hitter with { Level = strong.Level, TargetsPlayer = false, OnThreatTable = false, TargetsPet = true }) });
        check("choice: in combat a unit above the level window that went for the pet is taken", pick == "slot 2", pick);
        // Where a click reaches a plate: the middle of the screen, off the
        // edges and the interface (live, a marker on the minimap at 0.91,
        // 0.89 and one at the edge, 0.99, could not be clicked).
        VisualNameplate onMinimap = Plate(1, hash: 10, judgement: true, screenX: 0.91, screenY: 0.89);
        VisualNameplate atEdge = Plate(3, hash: 30, judgement: true, screenX: 0.99, screenY: 0.84);
        VisualNameplate middle = Plate(2, hash: 20, judgement: true, screenX: 0.5, screenY: 0.6);
        pick = Chosen(idle with { Nameplates = Roster(onMinimap, middle, atEdge) });
        bool turnFirst = ForeverPullOptions.TryBest(ForeverPullOptions.Of(idle with { Nameplates = Roster(onMinimap, atEdge) },
            config, combat, none), out ForeverPullOption sideways) && !sideways.Clickable &&
            sideways.Effort == sideways.Yards + ForeverPullOptions.ClickYards + ForeverPullOptions.TurnYards;
        check("choice: of alike units the one in the middle of the screen goes first; one only at the side is turned to first, not clicked",
            pick == "slot 2" && turnFirst, $"{pick} {sideways}");
        VisualNameplate edgeAttacker = hitter with { Screen = new(0.95, 0.6) };
        check("choice: in combat an attacker at the screen's edge is turned to, not clicked",
            Chosen(fighting with { Nameplates = Roster(edgeAttacker) }) == "none" &&
            ForeverTargetPolicy.TryAttackerOutOfView(fighting with { Nameplates = Roster(edgeAttacker) }, config,
                out VisualNameplate turnTo) && turnTo.Slot == 2, "");
        check("choice: in combat an attacker low on screen, between the clickable sides, is still taken when no other is",
            Chosen(fighting with { Nameplates = Roster(hitter with { Screen = new(0.5, 0.2) }) }) == "slot 2" &&
            !ForeverTargetPolicy.TryAttackerOutOfView(fighting with { Nameplates = Roster(hitter with { Screen = new(0.5, 0.2) }) },
                config, out _), "");
        VisualNameplate behind = hitter with { Screen = null };
        check("choice: an attacker with no marker on screen is known out of view, for Threat to turn to",
            ForeverTargetPolicy.TryAttackerOutOfView(fighting with { Nameplates = Roster(behind) }, config, out VisualNameplate unseen) &&
            unseen.Slot == 2 && Chosen(fighting with { Nameplates = Roster(behind) }) == "none" &&
            !ForeverTargetPolicy.TryAttackerOutOfView(fighting with { Nameplates = Roster(hitter) }, config, out _), "");
        {
            // Facing 1.2 at (100, 200); facing grows to the left, and a positive rate turns right.
            Vector2 here = new(100, 200);
            double? left = ForeverNavigator.TurnRateToward(idle, ForeverNavigator.Along(here, 1.2 + 1.0, 20), 0.5);
            double? right = ForeverNavigator.TurnRateToward(idle, ForeverNavigator.Along(here, 1.2 - 1.0, 20), 0.5);
            double? straight = ForeverNavigator.TurnRateToward(idle, ForeverNavigator.Along(here, 1.2 + 0.05, 20), 0.5);
            check("turning to a point: one to the left turns left, one to the right turns right, at most the rate asked; none ahead",
                left is < 0 and >= -0.5 && right is > 0 and <= 0.5 && straight is null, $"{left} {right} {straight}");
        }

        // Pulls by cost: the fewest hostile units that could join first, then
        // the least effort; nothing is refused for its cost. Plates within
        // follow distance stand 8 to 28 yards off, beyond it 28 to 45: placed
        // at the middle along their bearing. Screen X 0.5 is the facing; 0.55
        // lies 0.1 radians right of it.
        static VisualNameplate Placed(VisualNameplate plate, double x, bool follow = true, int reaction = 2) =>
            plate with
            {
                Screen = new(x, plate.Screen!.Value.Y),
                Close = false,
                WithinFollowDistance = follow,
                Reaction = reaction
            };
        VisualNameplate lone = Placed(Plate(1, hash: 10, judgement: true), 0.1, follow: false);
        VisualNameplate paired = Placed(Plate(2, hash: 20, judgement: true), 0.7);
        VisualNameplate partner = Placed(Plate(3, hash: 30), 0.75);
        VisualBarSample bars = idle with { Nameplates = Roster(lone, paired, partner) };
        pick = Chosen(bars);
        check("pull cost: a unit on its own beats a nearer one with a hostile unit beside it", pick == "slot 1", Options(bars));
        VisualNameplate neutral = Placed(Plate(1, hash: 10, judgement: true, screenY: 0.45), 0.5, reaction: ForeverPullOptions.NeutralReaction);
        VisualNameplate hostile = Placed(Plate(2, hash: 20, judgement: true, screenY: 0.50), 0.55);
        bars = idle with { Nameplates = Roster(neutral, hostile) };
        pick = Chosen(bars);
        check("pull cost: of a hostile and a neutral unit together, the hostile one first", pick == "slot 2", Options(bars));
        pick = Chosen(idle with { Nameplates = Roster(neutral) });
        check("pull cost: the neutral one, on its own after that, next", pick == "slot 1", pick);
        VisualNameplate first = Placed(Plate(1, hash: 10, judgement: true, screenY: 0.50), 0.5);
        VisualNameplate second = Placed(Plate(2, hash: 20, judgement: true, screenY: 0.45), 0.55);
        List<ForeverPullOption> pair = ForeverPullOptions.Of(idle with { Nameplates = Roster(first, second) }, config, combat, none);
        check("pull cost: of two hostile units together one is still pulled, its neighbour counted",
            ForeverPullOptions.TryBest(pair, out ForeverPullOption one) && one.Plate.Slot == 2 && one.HostileNear == 1 &&
            one.Effort == one.Yards + ForeverPullOptions.ClickYards, string.Join("; ", pair));
        VisualNameplate third = Placed(Plate(3, hash: 30, judgement: true), 0.6);
        bars = idle with { Nameplates = Roster(first, second, third) };
        pick = Chosen(bars);
        check("pull cost: with only a pack of three in view, one of them is still pulled, on its own",
            pick != "none" && ForeverPullOptions.Of(bars, config, combat, none).All(option => option.HostileNear == 2), Options(bars));
        {
            // Nothing is refused for its cost: a mob two levels above, as far
            // as plates reach, out of sight and out of reach, is pulled when it
            // is the only one, and before a pack however near.
            int above = config.NPCMaxLevels_Above;
            config.NPCMaxLevels_Above = 2;
            VisualNameplate farStrong = Placed(Plate(4, hash: 40, level: 14, alpha: 0.24), 0.12, follow: false);
            VisualBarSample alone = idle with { Nameplates = Roster(farStrong) };
            string onlyOne = Chosen(alone);
            bars = idle with { Nameplates = Roster(first, second, third, farStrong) };
            string beforePack = Chosen(bars);
            // Levels above count for little: a mob two levels above beats an
            // alike one of the player's level 18 yards farther.
            VisualNameplate nearStrong = Placed(Plate(1, hash: 10, level: 14, judgement: true), 0.5);
            VisualNameplate farEqual = Placed(Plate(2, hash: 20, judgement: true), 0.1, follow: false);
            VisualBarSample levels = idle with { Nameplates = Roster(nearStrong, farEqual) };
            string levelPick = Chosen(levels);
            string[] seen = [Options(alone), Options(bars), Options(levels)];
            config.NPCMaxLevels_Above = above;
            check("pull cost: a far mob two levels above, out of sight and reach, is pulled when it is the only one",
                onlyOne == "slot 4", seen[0]);
            check("pull cost: a unit on its own goes before a pack however near the pack", beforePack == "slot 4", seen[1]);
            check("pull cost: levels above count for little; a near mob two levels above beats a far one of the player's level",
                levelPick == "slot 1", seen[2]);
        }
        bars = idle with { Nameplates = Roster(neutral, Plate(2, hash: 20, screenX: 0.55)) };
        check("pull cost: a unit its range flags do not place is too vague to count as a neighbour",
            ForeverPullOptions.HostileNear(bars, neutral) == 0, Options(bars));
        {
            // By their band middles two plates 0.38 radians apart stand 14
            // yards apart; their tracked dots, at 29 and 46 yards, 22.
            VisualNameplate a = Placed(Plate(1, hash: 10, judgement: true), 0.5, follow: false);
            VisualNameplate b = Placed(Plate(2, hash: 20), 0.7, follow: false);
            VisualBarSample untracked = idle with { Nameplates = Roster(a, b) };
            Vector2 here = new(100, 200);
            VisualBarSample tracked = untracked with
            {
                TrackedBlips = [ForeverNavigator.Along(here, 1.2, 29), ForeverNavigator.Along(here, 1.2 - Math.Atan(0.4), 46)]
            };
            check("pull cost: the minimap's tracking places units exactly, apart where the plates alone could not tell",
                ForeverPullOptions.HostileNear(untracked, a) == 1 && ForeverPullOptions.HostileNear(tracked, a) == 0,
                $"{Options(untracked)} | {Options(tracked)}");
        }

        // Hit by another unit while the selected one does not fight: take the attacker.
        VisualNameplate passive = Plate(1, hash: 10, isTarget: true, judgement: true);
        check("attacked: in combat, the selected unit passive and another attacking",
            ForeverTargetPolicy.AttackedByAnother(fighting with { Nameplates = Roster(passive, hitter) }, config), "");
        check("attacked: not while the selected unit fights too",
            !ForeverTargetPolicy.AttackedByAnother(fighting with
                { Nameplates = Roster(passive with { TargetsPlayer = true, OnThreatTable = true }, hitter) }, config), "");
        check("attacked: not out of combat, nor while the selected unit's threat is unreadable",
            !ForeverTargetPolicy.AttackedByAnother(idle with { Nameplates = Roster(passive, hitter) }, config) &&
            !ForeverTargetPolicy.AttackedByAnother(fighting with
                { Nameplates = Roster(passive with { OnThreatTable = null }, hitter) }, config), "");
        check("attacked: by a unit outside the level window too",
            ForeverTargetPolicy.AttackedByAnother(fighting with
                { Nameplates = Roster(passive, hitter with { Level = 12 + config.NPCMaxLevels_Above + 1 }) }, config), "");

        // Reach from a plate's own ranges, as the target's action states give it.
        ForeverReach reach = combat.Reach(idle, ForeverSlotRanges.Of(Plate(1, judgement: false, strike: true)));
        check("reach: Holy Strike in range with Judgement out is too close to open",
            reach is { Opener: false, Combat: true, TooCloseToOpen: true }, $"{reach}");
        reach = combat.Reach(idle, ForeverSlotRanges.Of(Plate(1, judgement: true)));
        check("reach: Judgement in range is opener range", reach is { Opener: true, TooCloseToOpen: false }, $"{reach}");

        // A camera drag starts on empty ground: a right-button press on a plate
        // or the body under it targets that unit when the button comes up.
        string Press(params (double X, double Y)[] markers)
        {
            VisualNameplate[] plates = new VisualNameplate[markers.Length];
            for (int i = 0; i < markers.Length; i++)
                plates[i] = Plate(i + 1, hash: 10 * (i + 1), screenX: markers[i].X, screenY: markers[i].Y);
            return ForeverDragPoint.Choose(idle with { Nameplates = Roster(plates) }) is { } p
                ? FormattableString.Invariant($"({p.X:F2},{p.Y:F2})") : "none";
        }
        string press = Press();
        check("drag point: with nothing in view, above the character", press == "(0.50,0.67)", press);
        press = Press((0.52, 0.75));
        check("drag point: a plate over that spot moves the press aside", press == "(0.30,0.67)", press);
        press = Press((0.5, 0.75), (0.3, 0.75), (0.7, 0.75));
        check("drag point: bodies below three plates leave the spot above the middle one", press == "(0.50,0.82)", press);
        press = Press((0.5, 0.75), (0.3, 0.75), (0.7, 0.75), (0.5, 0.9), (0.3, 0.9), (0.7, 0.9));
        check("drag point: nowhere clear, no press", press == "none", press);
        press = ForeverDragPoint.Choose(idle with { TargetScreenPosition = new(0.5, 0.7) }) is { } t
            ? FormattableString.Invariant($"({t.X:F2},{t.Y:F2})") : "none";
        check("drag point: the target's own plate is avoided too", press == "(0.30,0.67)", press);

        // Set-aside units come back once their time is up.
        ForeverExclusions brief = new();
        VisualNameplate noGuid = Plate(4, incarnation: 7);
        brief.Exclude(noGuid, TimeSpan.FromMilliseconds(40));
        bool excluded = brief.IsExcluded(noGuid) && !brief.IsExcluded(noGuid with { Incarnation = 8 });
        Thread.Sleep(80);
        check("exclusions: by slot and incarnation without a GUID, for a while", excluded && !brief.IsExcluded(noGuid), "");
        brief.Exclude(new VisualTargetIdentity(10, 11, 12, false), TimeSpan.FromMinutes(1));
        check("exclusions: a unit set aside by its target identity is its plate too", brief.IsExcluded(opener), "");

        // Selection: glide onto the plate, click the moment the game reports it
        // under the cursor, retry a click the target flag did not follow, then give up.
        FakePointer pointer = new();
        ForeverTargetSelector selector = new(null!, pointer, config);
        selector.BeginSelect(opener);
        VisualBarSample Step(VisualNameplate plate) => idle with { Nameplates = Roster(plate) };
        ForeverClickResult result = selector.Select(Step(opener with { IsMouseover = true }), out string reason);
        check("select: the first step aims; a hover from before it is not the cursor's",
            result == ForeverClickResult.Pointing && pointer.Moves == 1 && pointer.Clicks == 0, $"{result} {reason}");
        selector.Select(Step(opener with { IsMouseover = true }), out _);
        check("select: a hover right after the stroke waits for the cursor to rest",
            pointer.Moves == 1 && pointer.Clicks == 0, $"moves={pointer.Moves} clicks={pointer.Clicks}");
        Thread.Sleep(140);
        result = selector.Select(Step(opener), out reason);
        check("select: rested and not hovered: follows the marker, no click",
            result == ForeverClickResult.Pointing && pointer.Moves == 2 && pointer.Clicks == 0, $"{result}");
        Thread.Sleep(140);
        result = selector.Select(Step(opener with { IsMouseover = true, Screen = new(0.62, 0.31) }), out reason);
        check("select: hovered while the plate moves on: clicks at once",
            result == ForeverClickResult.Pointing && pointer.Clicks == 1 && pointer.Moves == 2, $"{result} moves={pointer.Moves}");
        selector.Select(Step(opener with { IsMouseover = true }), out _);
        check("select: no second click while the first settles", pointer.Clicks == 1, $"{pointer.Clicks}");
        result = selector.Select(Step(opener with { IsMouseover = true, IsTarget = true }), out reason);
        check("select: the plate reports itself the target", result == ForeverClickResult.Done, $"{result} {reason}");

        // A plate walking right is aimed ahead of its marker.
        FakePointer leading = new();
        ForeverTargetSelector ahead = new(null!, leading, config);
        ahead.BeginSelect(opener);
        ahead.Select(Step(opener with { Screen = new(0.40, 0.3) }), out _);
        Thread.Sleep(140);
        ahead.Select(Step(opener with { Screen = new(0.45, 0.3) }), out _);
        check("select: a moving plate is aimed where the stroke will find it", leading.LastX > 0.45, $"{leading.LastX:F3}");

        selector.BeginSelect(opener);
        result = selector.Select(Step(opener with { Incarnation = 2 }), out reason);
        check("select: a new unit in the slot ends the selection", result == ForeverClickResult.Failed, $"{result} {reason}");

        // Recovery: set aside and search again out of combat, within a budget.
        ForeverRecovery recovery = new();
        bool allowed = true;
        for (int i = 0; i < ForeverRecovery.Budget; i++)
            allowed &= recovery.TryRecover(ForeverSetback.Stuck, inCombat: false, out _);
        check("recovery: out of combat, setbacks up to the budget", allowed, "");
        check("recovery: one more without a kill stops the run",
            !recovery.TryRecover(ForeverSetback.Blocked, inCombat: false, out string refusal) &&
            refusal.Contains("without a kill"), refusal);
        recovery.Killed();
        check("recovery: a kill restores the budget", recovery.TryRecover(ForeverSetback.TargetLost, false, out _), "");
        check("recovery: in combat only a selection is retried",
            recovery.TryRecover(ForeverSetback.SelectFailed, inCombat: true, out _) &&
            !recovery.TryRecover(ForeverSetback.Stuck, inCombat: true, out _), "");
        check("recovery: set aside a minute for a lost or unselectable unit, two for a bad way, never in combat",
            ForeverRecovery.SetAsideFor(ForeverSetback.SelectFailed, false) == TimeSpan.FromMinutes(1) &&
            ForeverRecovery.SetAsideFor(ForeverSetback.Blocked, false) == TimeSpan.FromMinutes(2) &&
            ForeverRecovery.SetAsideFor(ForeverSetback.SelectFailed, true) == TimeSpan.Zero, "");

        FakePointer stubborn = new() { Arrive = true };
        ForeverTargetSelector retrying = new(null!, stubborn, config);
        retrying.BeginSelect(opener);
        retrying.Select(Step(opener with { IsMouseover = true }), out _);
        Thread.Sleep(140);
        retrying.Select(Step(opener with { IsMouseover = true }), out _);
        Thread.Sleep(1050);
        retrying.Select(Step(opener with { IsMouseover = true }), out _);
        Thread.Sleep(1050);
        result = retrying.Select(Step(opener with { IsMouseover = true }), out reason);
        check("select: two unanswered clicks give up", result == ForeverClickResult.Failed && stubborn.Clicks == 2,
            $"{result} clicks={stubborn.Clicks} {reason}");
    }
}
