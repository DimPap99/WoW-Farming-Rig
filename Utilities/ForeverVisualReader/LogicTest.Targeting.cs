using Core;
using Core.Forever;

using Game;

using System;
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

        string Chosen(VisualBarSample bars, ForeverExclusions? excluded = null) =>
            ForeverTargetPolicy.TryChoose(bars, config, combat, excluded ?? none, out VisualNameplate plate)
                ? $"slot {plate.Slot}" : "none";

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

        // Choice among the plates in view.
        VisualNameplate opener = Plate(1, hash: 10, judgement: true, screenY: 0.3);
        VisualNameplate nearer = Plate(2, hash: 20, screenY: 0.2);
        VisualNameplate hidden = Plate(3, hash: 30, judgement: true, alpha: 0.24, screenY: 0.1);
        string pick = Chosen(idle with { Nameplates = Roster(opener, nearer, hidden) });
        check("choice: a unit an opener reaches beats a nearer one and an occluded one", pick == "slot 1", pick);
        ForeverExclusions setAside = new();
        setAside.Exclude(opener, TimeSpan.FromMinutes(1));
        pick = Chosen(idle with { Nameplates = Roster(opener, nearer, hidden) }, setAside);
        check("choice: a set-aside unit is passed over; one the camera sees beats an occluded opener", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, screenY: 0.4), Plate(2, hash: 20, screenY: 0.2)) });
        check("choice: otherwise the lowest on screen, the nearest", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(opener, Plate(2, hash: 20, isTarget: true, screenY: 0.6)) });
        check("choice: the current target needs no click and goes first", pick == "slot 2", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, judgement: false, strike: true)) });
        check("choice: out of combat a unit too close for every opener is passed over", pick == "none", pick);
        pick = Chosen(idle with { Nameplates = Roster(Plate(1, hash: 10, judgement: true, screenX: null)) });
        check("choice: a plate without a marker on screen cannot be clicked", pick == "none", pick);
        pick = Chosen(fighting with { Nameplates = Roster(opener, Plate(2, hash: 20, targetsPlayer: true, screenY: 0.8)) });
        check("choice: in combat only a unit that fights the player", pick == "slot 2", pick);
        pick = Chosen(fighting with { Nameplates = Roster(opener) });
        check("choice: in combat with no attacker in view, nothing", pick == "none", pick);

        // Reach from a plate's own ranges, as the target's action states give it.
        ForeverReach reach = combat.Reach(idle, ForeverSlotRanges.Of(Plate(1, judgement: false, strike: true)));
        check("reach: Holy Strike in range with Judgement out is too close to open",
            reach is { Opener: false, Combat: true, TooCloseToOpen: true }, $"{reach}");
        reach = combat.Reach(idle, ForeverSlotRanges.Of(Plate(1, judgement: true)));
        check("reach: Judgement in range is opener range", reach is { Opener: true, TooCloseToOpen: false }, $"{reach}");

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
        ForeverTargetSelector selector = new(null!, null!, pointer, config);
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
        ForeverTargetSelector ahead = new(null!, null!, leading, config);
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
        ForeverTargetSelector retrying = new(null!, null!, stubborn, config);
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
