using Core;
using Core.Database;
using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

using static Newtonsoft.Json.JsonConvert;

namespace ForeverVisualReader;

/// <summary>
/// Offline checks of the Forever runtime's decisions on synthetic frames: the
/// state machine, the class profile's rotation, preflight, GOAP facts and the
/// keys pressed. Nothing here reads the screen or sends input. Run from the
/// repository root, as --self-test is, so Json resolves.
/// </summary>
internal static partial class LogicTest
{
    public static int Run()
    {
        // ForeverRotation's decisions are checked against the verified Auto
        // profiles, with RequirementFactory's Forever requirements mimicked here.
        string json = Path.GetFullPath("Json");
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
            if (!ok) failures++;
        }

        StateMachineChecks(Check);

        DataConfig data = new() { Root = json, Exp = "forever" };
        SpellDB spells = new(data);
        ClassConfiguration config = DeserializeObject<ClassConfiguration>(
            File.ReadAllText(Path.Combine(json, "class", ForeverVerifiedSetup.PaladinAutoProfile)))!;
        config.FileName = ForeverVerifiedSetup.PaladinAutoProfile;

        // The frame the requirements read, like PlayerReader's latest Forever sample.
        VisualBarSample current = default;

        int Percent(double fraction) =>
            (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero);

        Func<bool> Parse(string requirement)
        {
            if (requirement.StartsWith("Spell:"))
            {
                int id = int.Parse(requirement["Spell:".Length..]);
                return () => current.SpellBook is { } book &&
                    Enumerable.Range(1, book.Count).Any(i => book.GetSlot(i) is { Id: var x } && x == id);
            }
            Match m = Regex.Match(requirement, @"^(Health%|Mana%|TargetHealth%)\s*(<=|>=|<|>|==)\s*(\d+)$");
            if (!m.Success)
                throw new NotSupportedException(requirement);
            int value = int.Parse(m.Groups[3].Value);
            Func<int> read = m.Groups[1].Value switch
            {
                "Health%" => () => Percent(current.PlayerHealth),
                "Mana%" => () => current.PlayerPowerType == 0 ? Percent(current.PlayerPower)
                    : throw new InvalidOperationException("not mana"),
                _ => () => current.TargetPresent == true ? Percent(current.TargetHealth)
                    : throw new InvalidOperationException("no target"),
            };
            return m.Groups[2].Value switch
            {
                "<" => () => read() < value,
                "<=" => () => read() <= value,
                ">" => () => read() > value,
                ">=" => () => read() >= value,
                _ => () => read() == value,
            };
        }

        // What ClassConfiguration.Initialise and RequirementFactory do on Forever:
        // slot and key from "Key", AutoAttack as a base action, then the game
        // cooldown (IsActionReady), the profile requirements and the key cooldown.
        foreach (KeyActions list in new[] { config.Pull, config.Combat, config.Adhoc })
        {
            foreach (KeyAction action in list.Sequence)
            {
                if (action.Name == config.AutoAttack.Name)
                {
                    action.BaseAction = true;
                    continue;
                }
                action.Slot = int.Parse(action.Key);
                action.ConsoleKey = ConsoleKey.D0 + action.Slot;
                List<Requirement> runtime =
                [
                    new() { HasRequirement = () => Core.Goals.ForeverCastGate.IsActionReady(current, action, spells) },
                    .. action.Requirements.Select(r => new Requirement { HasRequirement = Parse(r) }),
                    new() { HasRequirement = () => !action.OnCooldown() },
                ];
                action.RequirementsRuntime = [.. runtime];
            }
        }

        Check("Auto profile passes the Forever profile rules",
            ForeverProfileRules.FindUnsupportedProfile(config) is null,
            ForeverProfileRules.FindUnsupportedProfile(config) ?? "");

        {
            ClassConfiguration hunter = DeserializeObject<ClassConfiguration>(
                File.ReadAllText(Path.Combine(json, "class", ForeverVerifiedSetup.HunterAutoProfile)))!;
            foreach (KeyAction a in hunter.Combat.Sequence) if (a.Name == hunter.AutoAttack.Name) a.BaseAction = true; else a.Slot = int.Parse(a.Key);
            foreach (KeyAction a in hunter.Pull.Sequence) a.Slot = int.Parse(a.Key);
            Check("Hunter profile passes the Forever profile rules", ForeverProfileRules.FindUnsupportedProfile(hunter) is null,
                ForeverProfileRules.FindUnsupportedProfile(hunter) ?? "");
            Check("verified maps: Durotar on Kalimdor", ForeverVerifiedSetup.IsVerifiedMap(1411, 1) && !ForeverVerifiedSetup.IsVerifiedMap(1411, 0));
            Check("verified classes: Hunter and Paladin only", ForeverVerifiedSetup.AutoProfileFor((int)UnitClass.Hunter) == ForeverVerifiedSetup.HunterAutoProfile && ForeverVerifiedSetup.AutoProfileFor((int)UnitClass.Warrior) is null);
        }

        // Profile rules on their own.
        Check("allowlist: supported comparisons", ForeverProfileRules.FindUnbackedTerm("Health% < 70 && Mana% >= 12") is null);
        Check("allowlist: negated spell and parentheses", ForeverProfileRules.FindUnbackedTerm("!Spell:635 || (TargetHealth% > 20)") is null);
        Check("allowlist: Classic bit refused", ForeverProfileRules.FindUnbackedTerm("Health% < 50 && InMeleeRange") == "InMeleeRange");
        Check("allowlist: cooldown ms refused", ForeverProfileRules.FindUnbackedTerm("CD_Judgement <= GCD") == "CD_Judgement");
        Check("allowlist: absolute rage refused", ForeverProfileRules.FindUnbackedTerm("Rage > 10") == "Rage");
        Check("allowlist: user variable refused", ForeverProfileRules.FindUnbackedTerm("Health% < MIN_HP") == "MIN_HP");
        {
            ClassConfiguration bad = new();
            bad.Parallel.Sequence = [new KeyAction { Name = "Food" }];
            Check("profile: Parallel list refused", ForeverProfileRules.FindUnsupportedProfile(bad)?.Contains("Parallel") == true);
            bad = new();
            bad.Pull.Sequence = [new KeyAction { Name = "AutoAttack", BaseAction = true }];
            Check("profile: base action in Pull refused", ForeverProfileRules.FindUnsupportedProfile(bad)?.Contains("base action") == true);
            bad = new();
            KeyAction slot13 = new() { Name = "Far", Slot = 13 };
            bad.Combat.Sequence = [slot13];
            Check("profile: slot beyond 12 refused", ForeverProfileRules.FindUnsupportedProfile(bad)?.Contains("slots 1 to 12") == true);
            bad = new();
            KeyAction classic = new() { Name = "Strike", Slot = 3 };
            classic.Requirements.Add("InCombat");
            bad.Combat.Sequence = [classic];
            Check("profile: Classic requirement refused", ForeverProfileRules.FindUnsupportedProfile(bad)?.Contains("'InCombat'") == true);
            Check("action: aura expected beyond slot 12 refused",
                ForeverProfileRules.FindUnsupportedAction(new KeyAction { Name = "Buff", Slot = 30, AfterCastAuraExpected = true }) is not null);
            Check("action: aura expected on slot 5 allowed",
                ForeverProfileRules.FindUnsupportedAction(new KeyAction { Name = "Buff", Slot = 5, AfterCastAuraExpected = true }) is null);
        }

        // ---- Synthetic frames ----
        static T New<T>(params object[] args) => (T)Activator.CreateInstance(typeof(T),
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, args, null)!;

        int Encode(int index, ConsoleKey key, ModifierKey modifier)
        {
            for (int mod = 0; mod < 4; mod++)
                for (int id = 1; id < 128; id++)
                {
                    int code = (mod << 22) | (index << 14) | (id << 7);
                    if (KeyReader.DecodeBinding(code) is { } d && d.key1 == key && d.mod1 == modifier)
                        return code;
                }
            throw new InvalidOperationException($"no code for {index} {key} {modifier}");
        }

        Dictionary<int, int> spellOnSlot = new() { [1] = 635, [2] = 20271, [3] = 678, [5] = 20287 };
        int?[] bindings = new int?[VisualBindingSnapshot.Count];
        for (int i = 0; i < bindings.Length; i++) bindings[i] = 0;
        foreach (int slot in spellOnSlot.Keys)
            bindings[20 + slot - 1] = Encode(20 + slot, ConsoleKey.D0 + slot, ModifierKey.None);
        bindings[ForeverBindings.StartAttack.Index - 1] = Encode(ForeverBindings.StartAttack.Index, ConsoleKey.D2, ModifierKey.Shift);
        VisualBindingSnapshot bindingSnapshot = New<VisualBindingSnapshot>(bindings);
        Check("synthetic bindings decode as the runtime expects",
            ForeverBindings.StartAttack.IsBoundIn(new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320, Bindings: bindingSnapshot)) &&
            ForeverBindings.ActionButton(5).IsBoundTo(new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320, Bindings: bindingSnapshot), ConsoleKey.D5));

        VisualSpellBookSnapshot book = New<VisualSpellBookSnapshot>(
            (object)spellOnSlot.Values.Select((id, i) => (VisualSpellBookEntry?)new VisualSpellBookEntry(i + 1, VisualSpellIdSource.SpellId, id)).ToArray());
        VisualTargetIdentity mob = new(111, 222, 12, false);

        VisualBarSample Frame(double health = 1, double mana = 1, bool target = true, bool combat = false,
            bool? sealAura = true, bool aurasReadable = true, bool judgementRange = false, bool strikeRange = false,
            double judgementCooldown = 0, double strikeCooldown = 0, int powerType = 0, int classId = 2,
            int? unboundSlot = null)
        {
            VisualActionSample?[] bar = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
            VisualActionState?[] states = new VisualActionState?[VisualActionBarSnapshot.SlotCount];
            for (int slot = 1; slot <= bar.Length; slot++)
            {
                if (spellOnSlot.TryGetValue(slot, out int id))
                {
                    bar[slot - 1] = new(slot, VisualActionKind.Spell, id);
                    bool? range = slot switch
                    {
                        2 => target ? judgementRange : null,
                        3 => target ? strikeRange : null,
                        _ => null,   // self spells have no range to a hostile target
                    };
                    states[slot - 1] = new(slot, true, false, range);
                }
                else
                {
                    bar[slot - 1] = new(slot, VisualActionKind.Empty, 0);
                    states[slot - 1] = new(slot, false, false, null);
                }
            }
            double?[] cooldowns = new double?[VisualCooldownGridSnapshot.SlotCount];
            for (int i = 0; i < cooldowns.Length; i++) cooldowns[i] = 0;
            cooldowns[1] = judgementCooldown;
            cooldowns[2] = strikeCooldown;
            int?[] frameBindings = (int?[])bindings.Clone();
            if (unboundSlot is { } u) frameBindings[20 + u - 1] = 0;

            bool?[] auras = new bool?[VisualSlotAuraSnapshot.SlotCount];
            auras[0] = false; auras[1] = false; auras[2] = false;   // spells with no aura
            auras[4] = sealAura;
            return new VisualBarSample(health, mana, target ? 1 : 0, 0.5, 0, 0, 320,
                Position: new(ForeverVerifiedSetup.TirisfalGlades, 0.35, 0.79, 1.2),
                TargetPresent: target,
                ActionBar: New<VisualActionBarSnapshot>((object)bar),
                PlayerHealthMax: 742, PlayerPowerMax: 1000,
                WorldPosition: new(100, 200, 0, 0),
                SpellBook: book,
                Bindings: New<VisualBindingSnapshot>((object)frameBindings),
                ActionStates: New<VisualActionStateSnapshot>((object)states),
                Cooldowns: New<VisualCooldownGridSnapshot>((object)cooldowns),
                PlayerPowerType: powerType,
                PlayerIdentity: new(classId, 5, 12),
                TargetFlags: new(target, target, false, combat, false),
                LastCastEvent: new(0, 0),
                TargetIdentity: target ? mob : new(0, 0, 0, false),
                SlotAuras: aurasReadable ? new(auras) : null);
        }

        ForeverRotation NewRotation() => new(NullLogger<ForeverRotation>.Instance, null!, null!,
            new InFront(), config, spells, new CancellationTokenSource<GoapAgent>());

        string Describe(KeyAction? a) => a is null ? "none" : $"{a.Name} [{string.Join(", ", a.Requirements)}]";

        ForeverAdhocPlan Adhoc(VisualBarSample frame)
        {
            current = frame;
            return NewRotation().PlanAdhoc(frame);
        }

        void ExpectAdhoc(string name, VisualBarSample frame, ForeverAdhocStep step, string? action = null, string? requirement = null)
        {
            ForeverAdhocPlan plan = Adhoc(frame);
            bool ok = plan.Step == step &&
                (action is null || plan.Action?.Name == action) &&
                (requirement is null || plan.Action?.Requirements.Contains(requirement) == true);
            Check(name, ok, $"{plan.Step} {Describe(plan.Action)}");
        }

        // ---- Adhoc: self heals and buffs ----
        ExpectAdhoc("idle, Seal off: cast Seal", Frame(target: false, sealAura: false),
            ForeverAdhocStep.Cast, "Seal of Righteousness");
        ExpectAdhoc("idle, Seal on, full: nothing to do", Frame(target: false), ForeverAdhocStep.Continue);
        ExpectAdhoc("idle, auras unreadable: hold for aura state", Frame(target: false, aurasReadable: false),
            ForeverAdhocStep.AuraUnreadable, "Seal of Righteousness");
        ExpectAdhoc("idle, 80% health: recovery Holy Light", Frame(target: false, health: 0.80),
            ForeverAdhocStep.Cast, "Holy Light", "Health% < 90");
        ExpectAdhoc("combat, 60% health, auras blocked: emergency Holy Light", Frame(combat: true, health: 0.60, aurasReadable: false),
            ForeverAdhocStep.Cast, "Holy Light", "Health% < 70");
        ExpectAdhoc("combat, 80% health, auras blocked: no recovery heal, Seal skipped", Frame(combat: true, health: 0.80, aurasReadable: false),
            ForeverAdhocStep.Continue);
        ExpectAdhoc("combat, Seal readable off: refresh Seal", Frame(combat: true, health: 0.80, mana: 0.5, sealAura: false),
            ForeverAdhocStep.Cast, "Seal of Righteousness");
        ExpectAdhoc("combat, Seal off, 20% mana: best effort, keep fighting", Frame(combat: true, mana: 0.20, sealAura: false),
            ForeverAdhocStep.Continue);
        ExpectAdhoc("idle, Seal off, 20% mana: hold until castable", Frame(target: false, mana: 0.20, sealAura: false),
            ForeverAdhocStep.BuffMissing, "Seal of Righteousness");
        ExpectAdhoc("idle, 80% health, 5% mana: heal waits, Seal up", Frame(target: false, health: 0.80, mana: 0.05),
            ForeverAdhocStep.Continue);
        ExpectAdhoc("idle, unverified class: nothing pressable, Seal held", Frame(target: false, sealAura: false, classId: 1),
            ForeverAdhocStep.BuffMissing, "Seal of Righteousness");

        // ---- Fight ----
        ForeverFightPlan Fight(VisualBarSample frame, Action<ForeverRotation>? setup = null)
        {
            current = frame;
            ForeverRotation rotation = NewRotation();
            setup?.Invoke(rotation);
            return rotation.Plan(frame, mob);
        }
        void ExpectFight(string name, VisualBarSample frame, ForeverFightStep step, string? action = null,
            Action<ForeverRotation>? setup = null)
        {
            ForeverFightPlan plan = Fight(frame, setup);
            Check(name, plan.Step == step && (action is null || plan.Action?.Name == action),
                $"{plan.Step} {Describe(plan.Action)}");
        }
        void Started(ForeverRotation r)
        {
            typeof(ForeverRotation).GetField("target", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, (VisualTargetIdentity?)mob);
            typeof(ForeverRotation).GetField("attackStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, true);
        }
        void Judged(ForeverRotation r)
        {
            Started(r);
            var used = (HashSet<KeyAction>)typeof(ForeverRotation).GetField("usedPulls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r)!;
            used.Add(config.Pull.Sequence[0]);
        }

        ExpectFight("Judgement range, Seal up: open with Judgement", Frame(judgementRange: true), ForeverFightStep.Pull, "Judgement");
        ExpectFight("Judgement range, Seal off: no opener, close in", Frame(judgementRange: true, sealAura: false), ForeverFightStep.NeedRange);
        ExpectFight("combat, auras blocked, Judgement range only: close in", Frame(combat: true, judgementRange: true, aurasReadable: false), ForeverFightStep.NeedRange);
        ExpectFight("melee, Judgement on cooldown, new target: start attack", Frame(judgementRange: true, strikeRange: true, judgementCooldown: 0.4),
            ForeverFightStep.StartAttack, "AutoAttack");
        ExpectFight("melee, attack started: Holy Strike", Frame(judgementRange: true, strikeRange: true, judgementCooldown: 0.4),
            ForeverFightStep.Combat, "Holy Strike", Started);
        ExpectFight("melee, Holy Strike on cooldown: wait, do not pursue", Frame(strikeRange: true, strikeCooldown: 0.5),
            ForeverFightStep.Wait, setup: Started);
        ExpectFight("out of every range: close in", Frame(), ForeverFightStep.NeedRange);
        ExpectFight("judged already, Judgement range only: close in", Frame(judgementRange: true), ForeverFightStep.NeedRange, setup: Judged);
        ExpectFight("out of combat at 50% mana: no opener", Frame(judgementRange: true, mana: 0.5), ForeverFightStep.NeedRange);
        ExpectFight("in combat at 30% mana: Holy Strike still pressed", Frame(combat: true, strikeRange: true, mana: 0.3, aurasReadable: false),
            ForeverFightStep.Combat, "Holy Strike", Started);
        {
            ForeverRotation close = NewRotation();
            Check("Paladin: melee range is never too close to open (Judgement covers it)",
                !close.TooCloseToOpen(Frame(judgementRange: true, strikeRange: true)));
            Check("Paladin: Judgement out of range but melee in range is too close",
                close.TooCloseToOpen(Frame(judgementRange: false, strikeRange: true)));
            Check("Paladin: out of every range is not too close", !close.TooCloseToOpen(Frame()));
        }
        {
            ForeverRotation rotation = NewRotation();
            VisualBarSample frame = Frame(judgementRange: true);
            current = frame;
            Check("InRange counts a pressable opener", rotation.InRange(frame, mob));
            frame = Frame(judgementRange: true, sealAura: false);
            current = frame;
            Check("InRange ignores an opener that is withheld", !rotation.InRange(frame, mob));
        }

        // ---- Hunter with an Attack button (melee reach) ----
        {
            VisualTargetIdentity hmob = new(333, 444, 6, false);
            ClassConfiguration hunterConfig = DeserializeObject<ClassConfiguration>(
                File.ReadAllText(Path.Combine(json, "class", ForeverVerifiedSetup.HunterAutoProfile)))!;
            hunterConfig.FileName = ForeverVerifiedSetup.HunterAutoProfile;
            foreach (KeyAction action in hunterConfig.Combat.Sequence)
            {
                if (action.Name == hunterConfig.AutoAttack.Name) { action.BaseAction = true; continue; }
                action.Slot = int.Parse(action.Key);
                action.ConsoleKey = ConsoleKey.D0 + action.Slot;
                KeyAction a = action;
                action.RequirementsRuntime =
                [
                    new() { HasRequirement = () => Core.Goals.ForeverCastGate.IsActionReady(current, a, spells) },
                    .. a.Requirements.Select(r => new Requirement { HasRequirement = Parse(r) }),
                    new() { HasRequirement = () => !a.OnCooldown() },
                ];
            }
            Dictionary<int, int> hunterSlots = new() { [1] = 2973, [2] = 75, [3] = 1978, [4] = 6603 };
            int?[] hb = new int?[VisualBindingSnapshot.Count];
            for (int i = 0; i < hb.Length; i++) hb[i] = 0;
            foreach (int slot in hunterSlots.Keys) hb[20 + slot - 1] = Encode(20 + slot, ConsoleKey.D0 + slot, ModifierKey.None);
            hb[ForeverBindings.StartAttack.Index - 1] = Encode(ForeverBindings.StartAttack.Index, ConsoleKey.D2, ModifierKey.Shift);
            VisualSpellBookSnapshot hbook = New<VisualSpellBookSnapshot>(
                (object)hunterSlots.Values.Select((id, i) => (VisualSpellBookEntry?)new VisualSpellBookEntry(i + 1, VisualSpellIdSource.SpellId, id)).ToArray());

            VisualBarSample HFrame(bool ranged, bool? melee, bool combat = true, double stingCooldown = 0, bool attackButton = true)
            {
                VisualActionSample?[] bar = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
                VisualActionState?[] states = new VisualActionState?[VisualActionBarSnapshot.SlotCount];
                for (int slot = 1; slot <= bar.Length; slot++)
                {
                    if (hunterSlots.TryGetValue(slot, out int id) && (attackButton || slot != 4))
                    {
                        bar[slot - 1] = new(slot, VisualActionKind.Spell, id);
                        bool? range = slot switch { 1 => true, 2 or 3 => ranged, _ => melee };
                        states[slot - 1] = new(slot, true, false, range);
                    }
                    else
                    {
                        bar[slot - 1] = new(slot, VisualActionKind.Empty, 0);
                        states[slot - 1] = new(slot, false, false, null);
                    }
                }
                double?[] cd = new double?[VisualCooldownGridSnapshot.SlotCount];
                for (int i = 0; i < cd.Length; i++) cd[i] = 0;
                cd[2] = stingCooldown;
                return new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320,
                    Position: new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 1.2), TargetPresent: true,
                    ActionBar: New<VisualActionBarSnapshot>((object)bar), PlayerHealthMax: 141, PlayerPowerMax: 149,
                    WorldPosition: new(100, 200, 0, 1), SpellBook: hbook, Bindings: New<VisualBindingSnapshot>((object)hb),
                    ActionStates: New<VisualActionStateSnapshot>((object)states), Cooldowns: New<VisualCooldownGridSnapshot>((object)cd),
                    PlayerPowerType: 0, PlayerIdentity: new((int)UnitClass.Hunter, 8, 6),
                    TargetFlags: new(true, true, false, combat, false), LastCastEvent: new(0, 0),
                    TargetIdentity: hmob, SlotAuras: null);
            }
            ForeverRotation HRotation(bool? startedInMelee)
            {
                ForeverRotation r = new(NullLogger<ForeverRotation>.Instance, null!, null!, new InFront(), hunterConfig, spells, new CancellationTokenSource<GoapAgent>());
                if (startedInMelee is { } s)
                {
                    typeof(ForeverRotation).GetField("target", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, (VisualTargetIdentity?)hmob);
                    typeof(ForeverRotation).GetField("attackStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, s);
                }
                return r;
            }
            void HExpect(string name, VisualBarSample frame, bool? started, ForeverFightStep step, string? action = null)
            {
                current = frame;
                ForeverFightPlan plan = HRotation(started).Plan(frame, hmob);
                Check("hunter: " + name, plan.Step == step && (action is null || plan.Action?.Name == action), $"{plan.Step} {Describe(plan.Action)}");
            }

            HExpect("at range: Serpent Sting first", HFrame(ranged: true, melee: false, combat: false), null, ForeverFightStep.Combat, "Serpent Sting");
            HExpect("at range, sting cooling: start attack (Auto Shot)", HFrame(ranged: true, melee: false, stingCooldown: 0.5), null, ForeverFightStep.StartAttack);
            // As observed live: Raptor Strike's slot reports in range at any distance and
            // the Attack slot reports no range, so Raptor Strike follows the start.
            HExpect("at range, attack started: Raptor Strike (its slot always reports in range)", HFrame(ranged: true, melee: null, stingCooldown: 0.5), true, ForeverFightStep.Combat, "Raptor Strike");
            HExpect("inside ranged minimum: still fighting with Raptor Strike", HFrame(ranged: false, melee: null), true, ForeverFightStep.Combat, "Raptor Strike");
            current = HFrame(ranged: false, melee: null);
            Check("hunter: Raptor Strike's slot keeps the fight in combat range", HRotation(true).InRange(current, hmob));
            HExpect("out of combat, ranged spells out of reach: close in (Raptor Strike's range ignored)", HFrame(ranged: false, melee: null, combat: false), null, ForeverFightStep.NeedRange);
            current = HFrame(ranged: false, melee: null, combat: false);
            Check("hunter: out of combat, Raptor Strike does not stop pursuit", !HRotation(null).InRange(current, hmob));
            HExpect("out of combat, attack started, sting cooling: wait, no Raptor Strike", HFrame(ranged: true, melee: null, combat: false, stingCooldown: 0.5), true, ForeverFightStep.Wait);
            hunterConfig.NPCMaxLevels_Above = 0;
            HExpect("level cap from the profile: a level 6 mob is refused at +0? no, equal level passes", HFrame(ranged: true, melee: false, combat: false), null, ForeverFightStep.Combat, "Serpent Sting");
            VisualTargetIdentity higher = new(555, 666, 7, false);
            current = HFrame(ranged: true, melee: false, combat: false) with { TargetIdentity = higher };
            Check("level cap from the profile: +1 mob refused when NPCMaxLevels_Above is 0",
                HRotation(null).Plan(current, higher).Step != ForeverFightStep.Combat);
            hunterConfig.NPCMaxLevels_Above = 1;
            Check("level cap from the profile: +1 mob accepted when NPCMaxLevels_Above is 1",
                HRotation(null).Plan(current, higher).Step == ForeverFightStep.Combat);
        }

        // ---- Preflight and GOAP facts ----
        {
            ForeverRotation rotation = NewRotation();
            VisualBarSample frame = Frame(target: false);
            Check("preflight passes on the full setup", rotation.TryPreflight(frame, out string reason), reason);
            Check("preflight names an unbound action", !rotation.TryPreflight(Frame(target: false, unboundSlot: 5), out reason) &&
                reason.Contains("Seal of Righteousness"), reason);
            Check("preflight refuses unreadable buff state", !rotation.TryPreflight(Frame(target: false, aurasReadable: false), out reason) &&
                reason.Contains("aura"), reason);
            Check("GOAP: Judgement range is pull range",
                ForeverGoapWorldState.TryBuild(Frame(judgementRange: true), config, out BitVector32 state) &&
                state[1 << (int)GoapKey.withinpullrange] && !state[1 << (int)GoapKey.incombatrange]);
            Check("GOAP: Holy Strike range is combat range",
                ForeverGoapWorldState.TryBuild(Frame(strikeRange: true), config, out state) &&
                state[1 << (int)GoapKey.incombatrange] && !state[1 << (int)GoapKey.withinpullrange]);
        }

        // ---- Resource gates for other power types ----
        {
            VisualBarSample rage = Frame(target: false, mana: 0, powerType: 1);
            Check("rage at 0 can open a fight", ForeverFrame.CanOpenFight(rage, inCombat: false) && ForeverFrame.IsRested(rage));
            VisualBarSample lowMana = Frame(target: false, mana: 0.5);
            Check("mana at 50% cannot open a fight", !ForeverFrame.CanOpenFight(lowMana, inCombat: false));
        }

        // ---- Keys come from the client's bindings; clear target from the profile ----
        {
            VisualBarSample KeyFrame(bool target = true, VisualActionKind slot7 = VisualActionKind.Macro,
                ConsoleKey slot7Key = ConsoleKey.D7, ConsoleKey? customClear = null, ConsoleKey forward = ConsoleKey.W)
            {
                VisualBarSample frame = Frame(target: target);
                int?[] b = (int?[])bindings.Clone();
                b[20 + 7 - 1] = Encode(20 + 7, slot7Key, ModifierKey.None);
                b[ForeverBindings.MoveForward.Index - 1] = Encode(ForeverBindings.MoveForward.Index, forward, ModifierKey.None);
                if (customClear is { } c)
                    b[ForeverBindings.ClearTarget.Index - 1] = Encode(ForeverBindings.ClearTarget.Index, c, ModifierKey.None);
                VisualActionSample?[] bar = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
                for (int s = 1; s <= bar.Length; s++)
                    bar[s - 1] = frame.ActionBar!.GetSlot(s);
                bar[6] = new(7, slot7, slot7 == VisualActionKind.Empty ? 0 : 99);
                return frame with { Bindings = New<VisualBindingSnapshot>((object)b), ActionBar = New<VisualActionBarSnapshot>((object)bar) };
            }
            MethodInfo keyFor = typeof(ForeverTargetSelector).GetMethod("ClearTargetKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ConsoleKey ClearKey(string profileKey, VisualBarSample frame)
            {
                ClassConfiguration cfg = new();
                cfg.ClearTarget.Key = profileKey;
                ForeverTargetSelector selector = new(null!, null!, null!, cfg);
                KeyAction k = (KeyAction)keyFor.Invoke(selector, [frame])!;
                return k.ConsoleKey;
            }
            Check("clear target: the profile's key when the client binds it to a main-bar macro",
                ClearKey("7", KeyFrame()) == ConsoleKey.D7);
            Check("clear target: the profile's key is refused when its slot holds a spell",
                ClearKey("7", KeyFrame(slot7: VisualActionKind.Spell)) == ConsoleKey.Escape);
            Check("clear target: the profile's key is refused when the client rebinds the slot",
                ClearKey("7", KeyFrame(slot7Key: ConsoleKey.D8)) == ConsoleKey.Escape);
            Check("clear target: upstream's clear-target binding when the client has one",
                ClearKey("", KeyFrame(customClear: ConsoleKey.Insert)) == ConsoleKey.Insert);
            Check("clear target: Escape with neither",
                ClearKey("", KeyFrame()) == ConsoleKey.Escape);
            ClassConfiguration none = new();
            Check("clear target: nothing pressed without a target",
                !new ForeverTargetSelector(null!, null!, null!, none).ClearTarget(KeyFrame(target: false)));
            Check("forward: the live binding, not a fixed W",
                ForeverBindings.MoveForward.IsBoundTo(KeyFrame(forward: ConsoleKey.UpArrow), ConsoleKey.UpArrow) &&
                !ForeverBindings.MoveForward.IsBoundTo(KeyFrame(forward: ConsoleKey.UpArrow), ConsoleKey.W));
        }

        TargetingChecks(Check, config, (target, inCombat) => Frame(target: target, combat: inCombat), NewRotation());

        Console.WriteLine(failures == 0 ? "All logic checks passed." : $"{failures} logic checks failed.");
        return failures == 0 ? 0 : 1;
    }
}
