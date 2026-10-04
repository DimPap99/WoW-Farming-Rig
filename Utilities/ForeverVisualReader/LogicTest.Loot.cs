using Core;
using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;

using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // Bodies remembered during a fight, and the round that loots them after it.
    private static void LootChecks(Action<string, bool, string> check)
    {
        long t0 = Stopwatch.GetTimestamp();
        long At(double seconds) => t0 + (long)(seconds * Stopwatch.Frequency);
        VisualTargetIdentity mob = new(11, 22, 7, false);
        VisualLootState lootIdle = new(4, 8, 6, 2, 0, false, null, 0, 0);

        // The player stands at (100, 200) facing 0, along world +X.
        VisualBarSample Seen(VisualTargetIdentity? target = null, double markerX = 0.5, bool? close = null,
            VisualDeathEvent? death = null, VisualNameplateRoster? plates = null, float x = 100, float y = 200) =>
            new(1, 1, 1, 0.5, 0, 0, 320,
                Position: new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 0),
                WorldPosition: new(x, y, 0, 1),
                TargetFlags: new(target is not null, target is not null, false, false, false),
                TargetIdentity: target ?? new(0, 0, 0, false),
                TargetScreenPosition: target is not null ? new VisualScreenPointSample(markerX, 0.5) : null,
                Attack: new VisualAttackState(0, 0, false, close),
                LastDeathEvent: death ?? new(5, 1, 2),
                Loot: lootIdle,
                Nameplates: plates);

        string Points(ForeverCorpse c) => FormattableString.Invariant(
            $"({c.Near.X:F1},{c.Near.Y:F1})-({c.Far.X:F1},{c.Far.Y:F1})");

        // ---- The ledger ----
        ForeverCorpses ledger = new();
        ledger.Observe(Seen(mob, close: true), At(0));
        check("corpses: the death latch read on the first frame is history", !ledger.Any, "");
        ledger.Observe(Seen(mob, close: true, death: new(6, 11, 22)), At(0.1));
        check("corpses: a kill within duel distance lies within 8 yards ahead",
            ledger.Pending is [{ } melee] && Points(melee) == "(100.0,200.0)-(108.0,200.0)",
            ledger.Pending.Count == 1 ? Points(ledger.Pending[0]) : $"{ledger.Pending.Count}");

        ForeverCorpses ranged = new();
        ranged.Observe(Seen(mob, close: false, plates: Roster()), At(0));
        ranged.Observe(Seen(death: new(6, 11, 22), plates: Roster()), At(0.2));
        check("corpses: a ranged kill lies beyond duel distance, out to the plate distance, from its last sighting",
            ranged.Pending is [{ } far] && Points(far) == "(108.0,200.0)-(145.0,200.0)",
            ranged.Pending.Count == 1 ? Points(ranged.Pending[0]) : $"{ranged.Pending.Count}");

        // A living unit is placed the same way, for a walk toward it.
        ForeverCorpses live = new();
        live.Observe(Seen(mob, close: false, plates: Roster()), At(0));
        bool located = live.TryLocate(mob.GuidHash1, mob.GuidHash2, out Vector2 liveNear, out Vector2 liveFar);
        check("sightings: a living unit lies along its bearing, from duel distance out to the plate distance",
            located && liveNear == new Vector2(108, 200) && liveFar == new Vector2(145, 200) &&
            !live.TryLocate(1, 2, out _, out _),
            FormattableString.Invariant($"{located} ({liveNear.X:F1},{liveNear.Y:F1})-({liveFar.X:F1},{liveFar.Y:F1})"));

        ForeverCorpses banded = new();
        VisualNameplate mid = Plate(2, hash: 44, screenX: 0.5) with { Close = false, WithinFollowDistance = true };
        VisualNameplate distant = Plate(3, hash: 66, screenX: 0.5) with { Close = false, WithinFollowDistance = false };
        banded.Observe(Seen(plates: Roster(mid, distant)), At(0));
        banded.Observe(Seen(death: new(6, 44, 45), plates: Roster(distant)), At(0.1));
        banded.Observe(Seen(death: new(7, 66, 67), plates: Roster()), At(0.2));
        check("corpses: within follow distance a body lies 8 to 28 yards ahead, beyond it 28 to the plate distance",
            banded.Pending is [{ } within, { } beyond] &&
            Points(within) == "(108.0,200.0)-(128.0,200.0)" && Points(beyond) == "(128.0,200.0)-(145.0,200.0)",
            string.Join(" ", System.Linq.Enumerable.Select(banded.Pending, Points)));

        ForeverCorpses aside = new();
        aside.Observe(Seen(mob, markerX: 1.0, close: true), At(0));
        aside.Observe(Seen(mob, markerX: 1.0, close: true, death: new(6, 11, 22)), At(0.1));
        check("corpses: a marker at the right edge lies 45 degrees to the right",
            aside.Pending is [{ } right] && Points(right) == "(100.0,200.0)-(105.7,194.3)",
            aside.Pending.Count == 1 ? Points(aside.Pending[0]) : $"{aside.Pending.Count}");

        ForeverCorpses attackers = new();
        VisualNameplate other = Plate(2, hash: 33, screenX: 0.5) with { Close = false };
        attackers.Observe(Seen(mob, close: true, plates: Roster(other)), At(0));
        attackers.Observe(Seen(mob, close: true, death: new(6, 33, 34), plates: Roster(other)), At(0.1));
        attackers.Observe(Seen(death: new(7, 99, 98), plates: Roster()), At(0.2));
        check("corpses: a plate's unit is a body too; a death never seen is not",
            attackers.Pending is [{ GuidHash1: 33, GuidHash2: 34 }], $"{attackers.Pending.Count}");
        attackers.Observe(Seen(mob, close: true, death: new(8, 11, 22), plates: Roster()), At(0.3));
        check("corpses: the nearest body first",
            attackers.Nearest(new Vector2(101, 200)) is { GuidHash1: 11 } &&
            attackers.Nearest(new Vector2(130, 200)) is { GuidHash1: 33 }, "");
        attackers.Observe(Seen(), At(200));
        check("corpses: bodies are given up after three minutes", !attackers.Any, $"{attackers.Pending.Count}");

        // ---- The round ----
        ForeverLoot Round(ForeverCorpses corpses, bool skin = false)
        {
            ForeverLoot round = new(NullLogger<ForeverLoot>.Instance, corpses, null!, null!, null!,
                new ClassConfiguration { Skin = skin }, new CancellationTokenSource<GoapAgent>());
            round.Begin();
            return round;
        }
        VisualBarSample Frame(float x = 100, bool target = false, bool combat = false,
            VisualSoftInteract? soft = null, VisualLootState? loot = null) =>
            Seen(x: x) with
            {
                TargetFlags = new(target, target, target, combat, false),
                SoftInteract = soft ?? new(0, 0, null, null, 3, true),
                Loot = loot ?? lootIdle,
            };
        VisualSoftInteract body = new(11, 22, true, true, 3, true);

        ForeverCorpses one = new();
        one.Observe(Seen(mob, close: false), At(0));
        one.Observe(Seen(death: new(6, 11, 22)), At(0.1));
        ForeverLoot loot = Round(one);
        check("loot: an attack ends the round for now", loot.Plan(Frame(combat: true), inDanger: true).Step == ForeverLootStep.Attacked, "");
        check("loot: combat that only lingers goes on looting",
            loot.Plan(Frame(combat: true), inDanger: false).Step == ForeverLootStep.Walk, $"{loot.Plan(Frame(combat: true), inDanger: false).Step}");
        check("loot: a selected unit is cleared, so the key acts on the soft target",
            loot.Plan(Frame(target: true), inDanger: false).Step == ForeverLootStep.ClearTarget, "");
        ForeverLootPlan plan = loot.Plan(Frame(soft: body), inDanger: false);
        check("loot: a body under the Interact Key that reads lootable is pressed at once", plan is { Step: ForeverLootStep.Interact, Body: (11, 22) },
            $"{plan.Step}");
        ForeverLoot unread = Round(one);
        ForeverLootPlan stepFirst = unread.Plan(Frame(soft: body with { Lootable = false }), inDanger: false);
        typeof(ForeverLoot).GetField("closer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(unread, false);
        ForeverLootPlan thenPress = unread.Plan(Frame(soft: body with { Lootable = false }), inDanger: false);
        check("loot: a body of the fight under the Interact Key that does not read lootable (out of reach, or empty) gets a step closer, then the press",
            stepFirst is { Step: ForeverLootStep.Approach, Body: (11, 22) } && thenPress is { Step: ForeverLootStep.Interact, Body: (11, 22) },
            $"{stepFirst.Step} {thenPress.Step}");
        plan = loot.Plan(Frame(soft: new VisualSoftInteract(33, 44, true, false, 3, true)), inDanger: false);
        check("loot: otherwise, as for another's body showing no loot, walk to the near end of the body's line",
            plan.Step == ForeverLootStep.Walk && plan.Point == new Vector2(108, 200), $"{plan.Step} {plan.Point}");
        plan = loot.Plan(Frame(x: 108), inDanger: false);
        check("loot: at the near end, on to the far end", plan.Step == ForeverLootStep.Walk && plan.Point == new Vector2(145, 200),
            $"{plan.Step} {plan.Point}");
        // At the far end with nothing found it looks round there (the Interact
        // Key's soft target takes only what is in front), then gives up.
        VisualBarSample Facing(double angle) =>
            Frame(x: 145) with { Position = new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, angle) };
        ForeverLootStep atEnd = loot.Plan(Frame(x: 145), inDanger: false).Step;
        ForeverLootStep turning = loot.Plan(Facing(Math.PI / 2), inDanger: false).Step;
        loot.Plan(Facing(Math.PI), inDanger: false);
        loot.Plan(Facing(3 * Math.PI / 2), inDanger: false);
        loot.Plan(Facing(0.01), inDanger: false);
        plan = loot.Plan(Facing(0.01), inDanger: false);
        check("loot: at the far end with nothing found it looks round there, then gives the body up and the round is done",
            atEnd == ForeverLootStep.Search && turning == ForeverLootStep.Search &&
            plan.Step == ForeverLootStep.Done && !one.Any, $"{atEnd} {turning} {plan.Step} pending={one.Pending.Count}");


        // After a press: the window opens, Auto Loot empties it, it closes.
        ForeverCorpses two = new();
        two.Observe(Seen(mob, close: true), At(0));
        two.Observe(Seen(death: new(6, 11, 22)), At(0.1));
        ForeverLoot pressed = Round(two);
        void Pressed(ForeverLoot round, int attempts = 1, double secondsAgo = 0)
        {
            Type type = typeof(ForeverLoot);
            type.GetField("interacting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, ((int, int)?)(11, 22));
            type.GetField("lootBefore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, lootIdle);
            type.GetField("attempts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, attempts);
            type.GetField("awaiting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, true);
            type.GetField("steppedToward", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, ((int, int)?)(11, 22));
            type.GetField("lastInteract", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(round, Stopwatch.GetTimestamp() - (long)(secondsAgo * Stopwatch.Frequency));
        }
        Pressed(pressed);
        check("loot: no second press while the first settles", pressed.Plan(Frame(soft: body), inDanger: false).Step == ForeverLootStep.Wait, "");
        check("loot: an open window is waited for",
            pressed.Plan(Frame(soft: body, loot: lootIdle with { OpenSequence = 5, IsOpen = true }), inDanger: false).Step == ForeverLootStep.Wait, "");
        plan = pressed.Plan(Frame(soft: body, loot: lootIdle with { OpenSequence = 5, CloseSequence = 9, ClearedSequence = 7 }), inDanger: false);
        check("loot: the window emptied and closed: collected, and the body leaves the ledger",
            plan is { Step: ForeverLootStep.Collected, Body: (11, 22) } && !two.Any, $"{plan.Step} pending={two.Pending.Count}");
        check("loot: a looted body is not pressed again",
            pressed.Plan(Frame(soft: body), inDanger: false).Step == ForeverLootStep.Done,
            $"{pressed.Plan(Frame(soft: body), inDanger: false).Step}");

        // With the profile's "Skin": the emptied body is skinned before the
        // round moves on. The press is what Update sends; here it is set.
        VisualLootState looted = lootIdle with { OpenSequence = 5, CloseSequence = 9, ClearedSequence = 7 };
        ForeverLoot Skinner()
        {
            ForeverCorpses killed = new();
            killed.Observe(Seen(mob, close: true), At(0));
            killed.Observe(Seen(death: new(6, 11, 22)), At(0.1));
            ForeverLoot round = Round(killed, skin: true);
            Pressed(round);
            return round;
        }
        void SkinPressed(ForeverLoot round, double secondsAgo = 0)
        {
            Type type = typeof(ForeverLoot);
            type.GetField("skinPressed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, true);
            type.GetField("skinSince", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(round, Stopwatch.GetTimestamp() - (long)(secondsAgo * Stopwatch.Frequency));
        }
        VisualSoftInteract emptied = body with { Lootable = false };
        ForeverLoot skinner = Skinner();
        ForeverLootStep collected = skinner.Plan(Frame(soft: body, loot: looted), inDanger: false).Step;
        ForeverLootPlan press = skinner.Plan(Frame(soft: emptied, loot: looted), inDanger: false);
        SkinPressed(skinner);
        ForeverLootStep casting = skinner.Plan(Frame(soft: emptied, loot: looted), inDanger: false).Step;
        ForeverLootStep window = skinner.Plan(Frame(soft: emptied, loot: looted with { OpenSequence = 6, IsOpen = true }), inDanger: false).Step;
        ForeverLootStep after = skinner.Plan(Frame(soft: emptied,
            loot: looted with { OpenSequence = 6, CloseSequence = 10, ClearedSequence = 8 }), inDanger: false).Step;
        check("skin: with Skin the emptied body under the Interact Key is pressed again, the cast and its window waited for, then the round goes on",
            collected == ForeverLootStep.Collected && press is { Step: ForeverLootStep.Skin, Body: (11, 22) } &&
            casting == ForeverLootStep.Wait && window == ForeverLootStep.Wait && after == ForeverLootStep.Done,
            $"{collected} {press.Step} {casting} {window} {after}");

        ForeverLoot unskinnable = Skinner();
        unskinnable.Plan(Frame(soft: body, loot: looted), inDanger: false);
        ForeverLootStep settling = unskinnable.Plan(Frame(loot: looted), inDanger: false).Step;
        typeof(ForeverLoot).GetField("skinSince", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(unskinnable, Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency);
        ForeverLootStep movedOn = unskinnable.Plan(Frame(loot: looted), inDanger: false).Step;
        ForeverLoot unskilled = Skinner();
        unskilled.Plan(Frame(soft: body, loot: looted), inDanger: false);
        SkinPressed(unskilled, secondsAgo: 7);
        ForeverLootStep leftAlone = unskilled.Plan(Frame(soft: emptied, loot: looted), inDanger: false).Step;
        check("skin: a body the Interact Key does not settle on, or a press that starts no skinning, is left and the round goes on",
            settling == ForeverLootStep.Wait && movedOn == ForeverLootStep.Done && leftAlone == ForeverLootStep.Done,
            $"{settling} {movedOn} {leftAlone}");
        check("skin: without the profile's Skin the round is done once the loot is taken",
            pressed.Plan(Frame(soft: emptied), inDanger: false).Step == ForeverLootStep.Done, "");

        // Trusting the key: a press is answered by a window or a message
        // within 3 s. "Too far away", or nothing at all, is tried again a
        // step closer, three presses in all; then the body is given up.
        VisualSoftInteract outOfReach = body with { Lootable = false };
        void Set(ForeverLoot round, string field, object value) =>
            typeof(ForeverLoot).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, value);
        ForeverLoot farRound = Skinner();
        ForeverLootStep step = farRound.Plan(Frame(soft: outOfReach) with { LastError = new(1, VisualGameError.TooFarAway) },
            inDanger: false).Step;
        Set(farRound, "closer", false);
        ForeverLootStep pressedAgain = farRound.Plan(Frame(soft: outOfReach) with { LastError = new(1, VisualGameError.TooFarAway) },
            inDanger: false).Step;
        check("loot: a press that draws \"too far away\" steps closer, then presses again",
            step == ForeverLootStep.Approach && pressedAgain == ForeverLootStep.Interact, $"{step} {pressedAgain}");
        ForeverLoot silent = Skinner();
        Pressed(silent, secondsAgo: 2);
        ForeverLootStep stillWaiting = silent.Plan(Frame(soft: outOfReach), inDanger: false).Step;
        Pressed(silent, secondsAgo: 4);
        ForeverLootStep retried = silent.Plan(Frame(soft: outOfReach), inDanger: false).Step;
        check("loot: a press nothing answers waits for a Skinning cast's window, then is tried again a step closer",
            stillWaiting == ForeverLootStep.Wait && retried == ForeverLootStep.Approach, $"{stillWaiting} {retried}");
        ForeverCorpses Fallen()
        {
            ForeverCorpses killed = new();
            killed.Observe(Seen(mob, close: true), At(0));
            killed.Observe(Seen(death: new(6, 11, 22)), At(0.1));
            return killed;
        }
        ForeverCorpses spentLedger = Fallen();
        ForeverLoot spent = Round(spentLedger, skin: true);
        Pressed(spent, attempts: ForeverLoot.MaxAttempts, secondsAgo: 4);
        spent.Plan(Frame(soft: outOfReach), inDanger: false);
        ForeverLootStep gaveUp = spent.Plan(Frame(soft: outOfReach), inDanger: false).Step;
        check("loot: after three presses, each a step closer, that nothing answered, the body is given up",
            gaveUp == ForeverLootStep.Done && !spentLedger.Any, $"{gaveUp} pending={spentLedger.Pending.Count}");

        // The game says why: full bags leave every body, by its message or
        // the bag grid; another's body, or an empty one the skill cannot
        // skin, is left at once.
        VisualBagSnapshot Bags(int backpackFree) => (VisualBagSnapshot)Activator.CreateInstance(typeof(VisualBagSnapshot),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [new VisualBagHeader?[] { new(0, 16, backpackFree, 0), new(1, 8, 2, 1), new(2, 0, 0, 0), new(3, 0, 0, 0), new(4, 0, 0, 0) },
                new VisualBagSlot?[VisualBagSnapshot.BagCount * VisualBagSnapshot.SlotsPerBag]], null)!;
        check("loot: the bags are full with no free slot in a bag that takes any item; a quiver's room does not count, an unreadable grid is no proof",
            ForeverLoot.BagsFull(Frame() with { Bags = Bags(0) }) && !ForeverLoot.BagsFull(Frame() with { Bags = Bags(3) }) &&
            !ForeverLoot.BagsFull(Frame()), "");
        ForeverCorpses messaged = Fallen();
        ForeverLoot noRoom = Round(messaged, skin: true);
        Pressed(noRoom);
        noRoom.Plan(Frame(soft: outOfReach) with { LastError = new(1, VisualGameError.InventoryFull) }, inDanger: false);
        ForeverLootStep afterMessage = noRoom.Plan(Frame(soft: outOfReach), inDanger: false).Step;
        ForeverCorpses gridded = Fallen();
        ForeverLootStep byGrid = Round(gridded, skin: true).Plan(Frame(soft: body) with { Bags = Bags(0) }, inDanger: false).Step;
        check("loot: full bags, by the game's message or the bag grid, leave every body: the one reason to leave them unlooted",
            afterMessage == ForeverLootStep.Done && !messaged.Any && byGrid == ForeverLootStep.Done && !gridded.Any,
            $"{afterMessage} {byGrid}");
        ForeverCorpses notOurs = Fallen();
        ForeverLoot refused = Round(notOurs, skin: true);
        Pressed(refused);
        refused.Plan(Frame(soft: outOfReach) with { LastError = new(1, VisualGameError.LootRefused) }, inDanger: false);
        ForeverCorpses tooHard = Fallen();
        ForeverLoot unskilledHide = Round(tooHard, skin: true);
        Pressed(unskilledHide);
        unskilledHide.Plan(Frame(soft: outOfReach) with { LastError = new(1, VisualGameError.SkillTooLow) }, inDanger: false);
        check("loot: a body the game says is not the player's to loot, or empty and beyond the skinning skill, is left at once",
            !notOurs.Any && !tooHard.Any, $"{notOurs.Pending.Count} {tooHard.Pending.Count}");
        ForeverCorpses leftOpen = Fallen();
        ForeverLoot stuckOpen = Round(leftOpen, skin: true);
        Pressed(stuckOpen);
        VisualLootState open = lootIdle with { OpenSequence = 5, IsOpen = true, RemainingSlots = 2 };
        stuckOpen.Plan(Frame(soft: outOfReach, loot: open), inDanger: false);
        Set(stuckOpen, "windowOpenedAt", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
        ForeverLootStep afterOpen = stuckOpen.Plan(Frame(soft: outOfReach, loot: open), inDanger: false).Step;
        check("loot: a window Auto Loot leaves items in gives that body up, and the round goes on",
            afterOpen == ForeverLootStep.Wait && !leftOpen.Any, $"{afterOpen} pending={leftOpen.Pending.Count}");

        // A cast between the press and the window: the press skinned a body
        // with nothing to loot; no second press.
        ForeverLoot skinnedAtOnce = Skinner();
        ForeverLootStep took = skinnedAtOnce.Plan(Frame(soft: outOfReach, loot: looted) with { LastCastEvent = new(1, 8613) },
            inDanger: false).Step;
        ForeverLootStep afterHide = skinnedAtOnce.Plan(Frame(soft: outOfReach, loot: looted) with { LastCastEvent = new(1, 8613) },
            inDanger: false).Step;
        check("loot: a window that followed a cast held the hide: the body is done, with no second press",
            took == ForeverLootStep.Collected && afterHide == ForeverLootStep.Done, $"{took} {afterHide}");

        ForeverLoot full = Round(new ForeverCorpses(), skin: true);
        Pressed(full);
        plan = full.Plan(Frame(loot: lootIdle with { OpenSequence = 5, CloseSequence = 9, RemainingSlots = 1 }), inDanger: false);
        ForeverLootStep leftItems = full.Plan(Frame(soft: emptied), inDanger: false).Step;
        check("loot: a window closed with items left: the round goes on, and the body is not skinned",
            plan.Step == ForeverLootStep.Collected && leftItems == ForeverLootStep.Done, $"{plan.Step} {leftItems}");
    }
}
