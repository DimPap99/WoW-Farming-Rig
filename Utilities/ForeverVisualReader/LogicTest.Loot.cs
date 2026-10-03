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
        ForeverLoot Round(ForeverCorpses corpses)
        {
            ForeverLoot round = new(NullLogger<ForeverLoot>.Instance, corpses, null!, null!, null!,
                new CancellationTokenSource<GoapAgent>());
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
        check("loot: combat ends the round for now", loot.Plan(Frame(combat: true)).Step == ForeverLootStep.InCombat, "");
        check("loot: a selected unit is cleared, so the key acts on the soft target",
            loot.Plan(Frame(target: true)).Step == ForeverLootStep.ClearTarget, "");
        ForeverLootPlan plan = loot.Plan(Frame(soft: body));
        check("loot: a lootable body under the Interact Key is looted", plan is { Step: ForeverLootStep.Interact, Body: (11, 22) },
            $"{plan.Step}");
        plan = loot.Plan(Frame(soft: body with { Lootable = false }));
        check("loot: otherwise walk to the near end of the body's line",
            plan.Step == ForeverLootStep.Walk && plan.Point == new Vector2(108, 200), $"{plan.Step} {plan.Point}");
        plan = loot.Plan(Frame(x: 108));
        check("loot: at the near end, on to the far end", plan.Step == ForeverLootStep.Walk && plan.Point == new Vector2(145, 200),
            $"{plan.Step} {plan.Point}");
        loot.Plan(Frame(x: 145));
        plan = loot.Plan(Frame(x: 145));
        check("loot: the far end reached with nothing found, the body is given up and the round is done",
            plan.Step == ForeverLootStep.Done && !one.Any, $"{plan.Step} pending={one.Pending.Count}");

        // After a press: the window opens, Auto Loot empties it, it closes.
        ForeverCorpses two = new();
        two.Observe(Seen(mob, close: true), At(0));
        two.Observe(Seen(death: new(6, 11, 22)), At(0.1));
        ForeverLoot pressed = Round(two);
        void Pressed(ForeverLoot round)
        {
            Type type = typeof(ForeverLoot);
            type.GetField("interacting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, ((int, int)?)(11, 22));
            type.GetField("lootBefore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, lootIdle);
            type.GetField("interacts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, 1);
            type.GetField("lastInteract", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(round, Stopwatch.GetTimestamp());
        }
        Pressed(pressed);
        check("loot: no second press while the first settles", pressed.Plan(Frame(soft: body)).Step == ForeverLootStep.Wait, "");
        check("loot: an open window is waited for",
            pressed.Plan(Frame(soft: body, loot: lootIdle with { OpenSequence = 5, IsOpen = true })).Step == ForeverLootStep.Wait, "");
        plan = pressed.Plan(Frame(soft: body, loot: lootIdle with { OpenSequence = 5, CloseSequence = 9, ClearedSequence = 7 }));
        check("loot: the window emptied and closed: collected, and the body leaves the ledger",
            plan is { Step: ForeverLootStep.Collected, Body: (11, 22) } && !two.Any, $"{plan.Step} pending={two.Pending.Count}");
        check("loot: a looted body is not pressed again",
            pressed.Plan(Frame(soft: body)).Step == ForeverLootStep.Done, $"{pressed.Plan(Frame(soft: body)).Step}");

        ForeverLoot full = Round(new ForeverCorpses());
        Pressed(full);
        plan = full.Plan(Frame(loot: lootIdle with { OpenSequence = 5, CloseSequence = 9, RemainingSlots = 1 }));
        check("loot: a window closed with items left stops the run", plan.Step == ForeverLootStep.Failed, $"{plan.Step} {plan.Reason}");
    }
}
