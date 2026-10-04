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
            bad = new();
            bad.Adhoc.Sequence = [new KeyAction { Name = "Holy Light", Slot = 1, MacroText = "/cast [@player] Holy Light" }];
            Check("profile: a macro, which would be typed into chat, refused",
                ForeverProfileRules.FindUnsupportedProfile(bad)?.Contains("MacroText") == true);
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
            int? unboundSlot = null, bool melee = false)
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
                SlotAuras: aurasReadable ? new(auras) : null,
                Attack: new VisualAttackState(0, 0, melee, TargetClose: target ? strikeRange : null));
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
        void Judged(ForeverRotation r)
        {
            typeof(ForeverRotation).GetField("target", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, (VisualTargetIdentity?)mob);
            var used = (HashSet<KeyAction>)typeof(ForeverRotation).GetField("usedPulls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r)!;
            used.Add(config.Pull.Sequence[0]);
        }

        ExpectFight("Judgement range, Seal up: open with Judgement", Frame(judgementRange: true), ForeverFightStep.Pull, "Judgement");
        ExpectFight("Judgement range, Seal off: no opener, close in", Frame(judgementRange: true, sealAura: false), ForeverFightStep.NeedRange);
        ExpectFight("combat, auras blocked, Judgement range only: close in", Frame(combat: true, judgementRange: true, aurasReadable: false), ForeverFightStep.NeedRange);
        ExpectFight("melee, Judgement on cooldown, melee off: start attack", Frame(judgementRange: true, strikeRange: true, judgementCooldown: 0.4),
            ForeverFightStep.StartAttack, "AutoAttack");
        ExpectFight("melee, melee on: Holy Strike", Frame(judgementRange: true, strikeRange: true, judgementCooldown: 0.4, melee: true),
            ForeverFightStep.Combat, "Holy Strike");
        ExpectFight("melee, Holy Strike on cooldown: wait, do not pursue", Frame(strikeRange: true, strikeCooldown: 0.5, melee: true),
            ForeverFightStep.Wait);
        ExpectFight("melee, attack state unreadable: no start attack, Holy Strike", Frame(strikeRange: true) with { Attack = null },
            ForeverFightStep.Combat, "Holy Strike");
        ExpectFight("out of every range: close in", Frame(), ForeverFightStep.NeedRange);
        ExpectFight("judged already, Judgement range only: close in", Frame(judgementRange: true), ForeverFightStep.NeedRange, setup: Judged);
        ExpectFight("out of combat at 50% mana: no opener", Frame(judgementRange: true, mana: 0.5), ForeverFightStep.NeedRange);
        ExpectFight("in combat at 30% mana: Holy Strike still pressed", Frame(combat: true, strikeRange: true, mana: 0.3, aurasReadable: false, melee: true),
            ForeverFightStep.Combat, "Holy Strike");
        {
            ForeverRotation close = NewRotation();
            Check("Paladin: melee range is never too close to open (Judgement covers it)",
                !close.TooCloseToOpen(Frame(judgementRange: true, strikeRange: true)));
            Check("Paladin: Judgement out of range but melee in range is too close",
                close.TooCloseToOpen(Frame(judgementRange: false, strikeRange: true)));
            Check("Paladin: out of every range is not too close", !close.TooCloseToOpen(Frame()));
            {
                // Judgement reaches a mob in melee, so a Paladin never steps back.
                VisualNameplateRoster notOnPlayer = new(
                    [new VisualNameplate(1, 1, mob.GuidHash1, mob.GuidHash2, mob.Level, true, false, false, false, true,
                        true, false, false, false, 2, true, 3, 1.0, default) { TargetsPet = false },
                     null, null, null, null, null, null, null], 1, 1, 0, true, true, 45);
                VisualBarSample inMelee = Frame(judgementRange: true, strikeRange: true, judgementCooldown: 0.4, melee: true)
                    with { Nameplates = notOnPlayer };
                current = inMelee;
                ForeverFightPlan paladinPlan = NewRotation().Plan(inMelee, mob);
                Check("Paladin: a mob in melee that is not on the player is fought there, never stepped back from",
                    paladinPlan.Step != ForeverFightStep.StepBack, $"{paladinPlan.Step}");
            }
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
            hb[ForeverBindings.PetAttack.Index - 1] = Encode(ForeverBindings.PetAttack.Index, ConsoleKey.Subtract, ModifierKey.None);
            VisualSpellBookSnapshot hbook = New<VisualSpellBookSnapshot>(
                (object)hunterSlots.Values.Select((id, i) => (VisualSpellBookEntry?)new VisualSpellBookEntry(i + 1, VisualSpellIdSource.SpellId, id)).ToArray());

            // As observed live: Raptor Strike's slot (1) reads in range out to
            // about 100 yards and the Attack slot (4) reads no range; Auto Shot
            // (2) and Serpent Sting (3) read out of range inside 8 yards, where
            // the target also turns close.
            // shotsUsable false: the client reports the shots unusable, as without ammo;
            // shotsNoMana: unusable only for lack of mana.
            VisualBarSample HFrame(bool ranged, bool? close, bool combat = true, double stingCooldown = 0,
                bool melee = false, bool autoShot = false, bool queued = false, bool attackReadable = true,
                bool shotsUsable = true, bool shotsNoMana = false, VisualPet? pet = null)
            {
                VisualActionSample?[] bar = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
                VisualActionState?[] states = new VisualActionState?[VisualActionBarSnapshot.SlotCount];
                for (int slot = 1; slot <= bar.Length; slot++)
                {
                    if (hunterSlots.TryGetValue(slot, out int id))
                    {
                        bar[slot - 1] = new(slot, VisualActionKind.Spell, id);
                        bool? range = slot switch { 1 => true, 2 or 3 => ranged, _ => null };
                        bool shot = slot is 2 or 3;
                        states[slot - 1] = new(slot, !shot || shotsUsable, shot && shotsNoMana, range);
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
                    TargetIdentity: hmob, SlotAuras: null,
                    Attack: attackReadable ? new VisualAttackState(queued ? 1 : 0, autoShot ? 1 << 1 : 0, melee, close) : null,
                    Pet: pet);
            }
            ForeverRotation HRotation() =>
                new(NullLogger<ForeverRotation>.Instance, null!, null!, new InFront(), hunterConfig, spells, new CancellationTokenSource<GoapAgent>());
            void HExpect(string name, VisualBarSample frame, ForeverFightStep step, string? action = null)
            {
                current = frame;
                ForeverFightPlan plan = HRotation().Plan(frame, hmob);
                Check("hunter: " + name, plan.Step == step && (action is null || plan.Action?.Name == action), $"{plan.Step} {Describe(plan.Action)}");
            }

            HExpect("at range: Serpent Sting first", HFrame(ranged: true, close: false, combat: false), ForeverFightStep.Combat, "Serpent Sting");
            HExpect("at range, sting cooling, no auto attack: start attack (Auto Shot)", HFrame(ranged: true, close: false, stingCooldown: 0.5),
                ForeverFightStep.StartAttack);
            HExpect("at range, Auto Shot running, sting cooling: wait; no Raptor Strike from range",
                HFrame(ranged: true, close: false, stingCooldown: 0.5, autoShot: true), ForeverFightStep.Wait);
            HExpect("close, the client switched to melee: Raptor Strike", HFrame(ranged: false, close: true, melee: true),
                ForeverFightStep.Combat, "Raptor Strike");
            HExpect("close, Auto Shot still on and melee off: start attack (melee)", HFrame(ranged: false, close: true, autoShot: true),
                ForeverFightStep.StartAttack);
            HExpect("close, Raptor Strike pending its swing: wait, no second press", HFrame(ranged: false, close: true, melee: true, queued: true),
                ForeverFightStep.Wait);
            current = HFrame(ranged: false, close: true, melee: true);
            Check("hunter: close keeps the fight in combat range", HRotation().InRange(current, hmob));
            HExpect("in combat, out of every ranged spell's range and not close: close in, Raptor Strike's slot ignored",
                HFrame(ranged: false, close: false, melee: true), ForeverFightStep.NeedRange);
            current = HFrame(ranged: false, close: false, melee: true);
            Check("hunter: far, Raptor Strike does not stop pursuit", !HRotation().InRange(current, hmob));
            HExpect("attack state unreadable: no start attack, no Raptor Strike",
                HFrame(ranged: true, close: null, stingCooldown: 0.5, attackReadable: false), ForeverFightStep.Wait);
            {
                VisualNameplate plate = new(1, 1, 5, 6, 6, true, false, false, false, false, false, false, false, false,
                    2, false, null, 1.0, new VisualSlotRanges(RosterRanges(1)));
                current = HFrame(ranged: true, close: false, combat: false);
                Check("hunter: a plate within duel distance is in Raptor Strike's reach",
                    HRotation().Reach(current, ForeverSlotRanges.Of(plate with { Close = true })).Combat);
                Check("hunter: a plate beyond it is not, whatever Raptor Strike's slot says",
                    !HRotation().Reach(current, ForeverSlotRanges.Of(plate with { Close = false })).Combat &&
                    !HRotation().Reach(current, ForeverSlotRanges.Of(plate)).Combat);
            }
            // Shots the client reports unusable, as without ammo in the ammo slot.
            HExpect("shots unusable at range: close in, no start attack (it would start melee at range)",
                HFrame(ranged: true, close: false, combat: false, shotsUsable: false), ForeverFightStep.NeedRange);
            current = HFrame(ranged: true, close: false, combat: false, shotsUsable: false);
            Check("hunter: with the shots unusable, range does not stop the pursuit", !HRotation().InRange(current, hmob));
            HExpect("shots unusable, the mob in contact: melee goes on",
                HFrame(ranged: false, close: true, melee: true, shotsUsable: false), ForeverFightStep.Combat, "Raptor Strike");
            {
                bool usable = HRotation().TryAttacksUsable(HFrame(ranged: true, close: false, combat: false,
                    shotsUsable: false), out string why);
                Check("hunter: an attack unusable but not for mana stops the run, naming it",
                    !usable && why.Contains("Serpent Sting") && why.Contains("ammo"), why);
                Check("hunter: shots short only of mana do not",
                    HRotation().TryAttacksUsable(HFrame(ranged: true, close: false, combat: false,
                        shotsUsable: false, shotsNoMana: true), out why), why);
            }

            // The pet: sent in as the fight opens, again only when it attacks something else.
            {
                VisualPet idle = new(true, false, false, 0, 0);
                VisualPet onTarget = idle with { InCombat = true, TargetHash1 = hmob.GuidHash1, TargetHash2 = hmob.GuidHash2 };
                HExpect("pet idle as the fight opens: Pet Attack before the first shot",
                    HFrame(ranged: true, close: false, combat: false, pet: idle), ForeverFightStep.PetAttack);
                // The pull: the shots wait until the mob is on the pet, then a moment longer.
                VisualNameplateRoster MobPlate(bool onPet, bool onPlayer = false) => new(
                    [new VisualNameplate(1, 1, hmob.GuidHash1, hmob.GuidHash2, hmob.Level, true, false, false, false, true,
                        true, false, false, onPlayer, 2, onPlayer, null, 1.0, new VisualSlotRanges(RosterRanges(2, 3)))
                        { TargetsPet = onPet }, null, null, null, null, null, null, null],
                    1, 1, 0, true, true, 45);
                ForeverRotation pull = HRotation();
                ForeverFightPlan Pull(VisualBarSample frame)
                {
                    current = frame;
                    return pull.Plan(frame, hmob);
                }
                bool waitsForMob = Pull(HFrame(ranged: true, close: false, pet: onTarget) with { Nameplates = MobPlate(onPet: false) })
                    .Step == ForeverFightStep.Wait;
                bool waitsOnPet = Pull(HFrame(ranged: true, close: false, pet: onTarget) with { Nameplates = MobPlate(onPet: true) })
                    .Step == ForeverFightStep.Wait;
                Thread.Sleep(ForeverRotation.PetHoldsMobMs + 100);
                ForeverFightPlan opened = Pull(HFrame(ranged: true, close: false, pet: onTarget) with { Nameplates = MobPlate(onPet: true) });
                Check("hunter: pet on the target, the mob not on it yet: the shots wait", waitsForMob, "");
                Check("hunter: the mob on the pet: the shots wait a moment longer, then Serpent Sting",
                    waitsOnPet && opened is { Step: ForeverFightStep.Combat, Action.Name: "Serpent Sting" },
                    $"{waitsOnPet} {opened.Step} {Describe(opened.Action)}");
                HExpect("the mob goes for the player: no waiting for the pet",
                    HFrame(ranged: true, close: false, pet: onTarget) with { Nameplates = MobPlate(onPet: false, onPlayer: true) },
                    ForeverFightStep.Combat, "Serpent Sting");
                HExpect("auto attack already running: no waiting for the pet",
                    HFrame(ranged: true, close: false, autoShot: true, pet: onTarget) with { Nameplates = MobPlate(onPet: false) },
                    ForeverFightStep.Combat, "Serpent Sting");
                HExpect("pet on another unit: sent again",
                    HFrame(ranged: true, close: false, pet: onTarget with { TargetHash1 = 1, TargetHash2 = 2 }),
                    ForeverFightStep.PetAttack);
                HExpect("dead pet: not sent", HFrame(ranged: true, close: false, combat: false, pet: idle with { Dead = true }),
                    ForeverFightStep.Combat, "Serpent Sting");
                HExpect("no pet: nothing to send", HFrame(ranged: true, close: false, combat: false, pet: new(false, null, null, 0, 0)),
                    ForeverFightStep.Combat, "Serpent Sting");
                HExpect("out of reach: the pet waits with the fight, so the hand-back for range is not held off",
                    HFrame(ranged: false, close: false, melee: true, pet: idle), ForeverFightStep.NeedRange);
                // Another hostile unit stands beside the mob, both within follow
                // distance: the shot pulls the mob on its own, and the pet goes
                // in once it comes, not first, where its run would draw both.
                VisualNameplateRoster Pair(int neighbourReaction) => new(
                    [new VisualNameplate(1, 1, hmob.GuidHash1, hmob.GuidHash2, hmob.Level, true, false, false, false, false,
                        true, false, false, false, 2, false, null, 1.0, new VisualSlotRanges(RosterRanges(2, 3)))
                        { Screen = new(0.5, 0.5), Close = false, WithinFollowDistance = true, TargetsPet = false },
                     new VisualNameplate(2, 1, 7, 8, hmob.Level, true, false, false, false, false,
                        false, false, false, false, neighbourReaction, false, null, 1.0, default)
                        { Screen = new(0.53, 0.5), Close = false, WithinFollowDistance = true, TargetsPet = false },
                     null, null, null, null, null, null],
                    2, 2, 0, true, true, 45);
                HExpect("a hostile unit beside the mob: the pet stays back and the shot pulls the mob on its own",
                    HFrame(ranged: true, close: false, combat: false, pet: idle) with { Nameplates = Pair(2) },
                    ForeverFightStep.Combat, "Serpent Sting");
                HExpect("the mob pulled on its own fights: now the pet goes in",
                    HFrame(ranged: true, close: false, combat: true, pet: idle) with { Nameplates = Pair(2) },
                    ForeverFightStep.PetAttack);
                HExpect("a neutral unit beside the mob does not join: the pet goes first as ever",
                    HFrame(ranged: true, close: false, combat: false, pet: idle) with { Nameplates = Pair(ForeverPullOptions.NeutralReaction) },
                    ForeverFightStep.PetAttack);
                hunterConfig.AutoPetAttack = false;
                HExpect("AutoPetAttack off: not sent", HFrame(ranged: true, close: false, combat: false, pet: idle),
                    ForeverFightStep.Combat, "Serpent Sting");
                hunterConfig.AutoPetAttack = true;
            }

            // The stance: ranged while the mob is not on the player, stepping
            // back to range when it comes too close for the shots; melee once
            // it goes for the player, or with the shots unusable (no ammo).
            {
                VisualNameplateRoster Mob(bool onPlayer) => new(
                    [new VisualNameplate(1, 1, hmob.GuidHash1, hmob.GuidHash2, hmob.Level, true, false, false, false, true,
                        true, false, false, onPlayer, 2, true, 3, 1.0, new VisualSlotRanges(RosterRanges(1)))
                        { TargetsPet = !onPlayer }, null, null, null, null, null, null, null],
                    1, 1, 0, true, true, 45);
                VisualBarSample closeOnPet = HFrame(ranged: false, close: true, melee: true) with { Nameplates = Mob(onPlayer: false) };
                VisualBarSample closeOnPlayer = HFrame(ranged: false, close: true, melee: true) with { Nameplates = Mob(onPlayer: true) };
                current = closeOnPet;
                Check("hunter: the mob on the pet is the ranged stance; on the player, melee",
                    HRotation().Stance(closeOnPet) == ForeverStance.Ranged &&
                    HRotation().Stance(closeOnPlayer) == ForeverStance.Melee, "");
                HExpect("the mob on the pet and too close for the shots: step back to range, not melee",
                    closeOnPet, ForeverFightStep.StepBack);
                HExpect("the mob on the player and close: melee, Raptor Strike",
                    closeOnPlayer, ForeverFightStep.Combat, "Raptor Strike");
                HExpect("the mob on the pet with the shots unusable (no ammo): melee where it stands",
                    HFrame(ranged: false, close: true, melee: true, shotsUsable: false) with { Nameplates = Mob(onPlayer: false) },
                    ForeverFightStep.Combat, "Raptor Strike");
                HExpect("the mob on the pet and in the shots' range: shoot, no step back",
                    HFrame(ranged: true, close: false, stingCooldown: 0.5) with { Nameplates = Mob(onPlayer: false) },
                    ForeverFightStep.StartAttack);
                HExpect("who the mob is on unknown: no step back, the fight as it stands",
                    HFrame(ranged: false, close: true, melee: true), ForeverFightStep.Combat, "Raptor Strike");
                ForeverRotation stepping = HRotation();
                current = closeOnPet;
                stepping.Plan(closeOnPet, hmob);
                typeof(ForeverRotation).GetField("stepBackSince", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(stepping, System.Diagnostics.Stopwatch.GetTimestamp() - (long)((ForeverRotation.StepBackMs + 100) / 1000.0 * System.Diagnostics.Stopwatch.Frequency));
                ForeverFightPlan rested = stepping.Plan(closeOnPet, hmob);
                Check("hunter: steps back that do not bring the range back give way to melee for a while",
                    rested is { Step: ForeverFightStep.Combat, Action.Name: "Raptor Strike" }, $"{rested.Step} {Describe(rested.Action)}");
            }

            // The game's error messages: what a refused press does.
            Check("refusal: out of sight before the fight gives the unit up, in it walks to see it again",
                ForeverRotation.ReactionTo(VisualGameError.LineOfSight, inCombat: false) == ForeverRefusal.GiveUp &&
                ForeverRotation.ReactionTo(VisualGameError.LineOfSight, inCombat: true) == ForeverRefusal.RegainSight);
            // Walking to see the target again: the next instant attack is pressed on the move.
            {
                VisualBarSample OnScreen(VisualBarSample frame) => frame with { TargetScreenPosition = new(0.5, 0.6) };
                current = OnScreen(HFrame(ranged: true, close: false));
                KeyAction? shot = HRotation().NextShotOnTheMove(current);
                Check("hunter: on the move, the next instant attack in priority order (Serpent Sting)",
                    shot?.Name == "Serpent Sting", Describe(shot));
                current = OnScreen(HFrame(ranged: true, close: false, stingCooldown: 0.5));
                shot = HRotation().NextShotOnTheMove(current);
                Check("hunter: on the move, never Raptor Strike nor the auto attack", shot is null, Describe(shot));
                current = HFrame(ranged: true, close: false);
                Check("hunter: with the target off screen, nothing is pressed on the move",
                    HRotation().NextShotOnTheMove(current) is null, "");
            }
            Check("refusal: out of range or melee reach steps in; anything else is tried again",
                ForeverRotation.ReactionTo(VisualGameError.OutOfRange, false) == ForeverRefusal.StepIn &&
                ForeverRotation.ReactionTo(VisualGameError.TooFarAway, true) == ForeverRefusal.StepIn &&
                new[] { VisualGameError.NotReady, VisualGameError.TooClose, VisualGameError.Facing, VisualGameError.Moving,
                    VisualGameError.InProgress, VisualGameError.Prevented, VisualGameError.BadTarget }
                    .All(e => ForeverRotation.ReactionTo(e, true) == ForeverRefusal.Retry));
            {
                // Close, melee on, Raptor Strike pending its swing: nothing to
                // press, so Update never reaches the caster.
                ForeverRotation rotation = HRotation();
                VisualBarSample At(float x, int sequence, VisualGameError error = VisualGameError.TooFarAway) =>
                    HFrame(ranged: false, close: true, melee: true, queued: true) with
                    {
                        WorldPosition = new(x, 200, 0, 1),
                        LastError = new(sequence, error)
                    };
                ForeverCombatResult Step(VisualBarSample frame)
                {
                    current = frame;
                    return rotation.Update(frame, hmob, out _);
                }
                Check("too far: a message from before the target is history", Step(At(100, 3)) == ForeverCombatResult.Waiting);
                Check("too far: melee on and the client says too far away: step in", Step(At(100, 4)) == ForeverCombatResult.StepIn);
                current = At(101, 4);
                Check("too far: the fight holds back for range until the character stepped in",
                    rotation.Plan(current, hmob).Step == ForeverFightStep.NeedRange && !rotation.InRange(current, hmob));
                current = At(103.5f, 4);
                Check("too far: three yards on, the fight goes on",
                    rotation.Plan(current, hmob).Step == ForeverFightStep.Wait && rotation.InRange(current, hmob));
                Check("too far: the same message is not taken twice", Step(At(103.5f, 4)) == ForeverCombatResult.Waiting);
                Check("too far: another message is not about melee reach",
                    Step(At(103.5f, 5, VisualGameError.OutOfRange)) == ForeverCombatResult.Waiting);
                ForeverCombatResult second = Step(At(103.5f, 6));
                ForeverCombatResult renewed = Step(At(104, 7));
                ForeverCombatResult third = Step(At(107.5f, 8));
                ForeverCombatResult fourth = Step(At(111, 9));
                Check("too far: a refusal while one is open renews it; the fourth in a row gives the unit up",
                    second == ForeverCombatResult.StepIn && renewed == ForeverCombatResult.StepIn &&
                    third == ForeverCombatResult.StepIn && fourth == ForeverCombatResult.Unreachable,
                    $"{second} {renewed} {third} {fourth}");
            }

            hunterConfig.NPCMaxLevels_Above = 0;
            HExpect("level cap from the profile: a level 6 mob is refused at +0? no, equal level passes", HFrame(ranged: true, close: false, combat: false), ForeverFightStep.Combat, "Serpent Sting");
            VisualTargetIdentity higher = new(555, 666, 7, false);
            current = HFrame(ranged: true, close: false, combat: false) with { TargetIdentity = higher };
            Check("level cap from the profile: +1 mob refused when NPCMaxLevels_Above is 0",
                HRotation().Plan(current, higher).Step != ForeverFightStep.Combat);
            // The cap keeps the run from opening on such a unit; one that
            // already fights the player or the pet is fought all the same.
            current = HFrame(ranged: true, close: false) with
            {
                TargetIdentity = higher,
                Nameplates = new([new VisualNameplate(1, 1, higher.GuidHash1, higher.GuidHash2, higher.Level, true, false, false,
                    false, true, true, false, false, true, 2, true, 3, 1.0, new VisualSlotRanges(RosterRanges(2, 3))),
                    null, null, null, null, null, null, null], 1, 1, 0, true, true, 45)
            };
            Check("level cap from the profile: a +1 mob that fights the player is fought at NPCMaxLevels_Above 0",
                HRotation().Plan(current, higher).Step == ForeverFightStep.Combat);
            current = HFrame(ranged: true, close: false, combat: false) with { TargetIdentity = higher };
            hunterConfig.NPCMaxLevels_Above = 1;
            Check("level cap from the profile: +1 mob accepted when NPCMaxLevels_Above is 1",
                HRotation().Plan(current, higher).Step == ForeverFightStep.Combat);

            // Two-bit range codes for main-bar slots: 1 in range.
            static int RosterRanges(params int[] inRange) => inRange.Sum(slot => 1 << ((slot - 1) * 2));
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
            VisualPet pet = new(true, false, false, 0, 0);
            Check("preflight: a pet with Pet Attack unbound is refused, naming the binding",
                !rotation.TryPreflight(Frame(target: false) with { Pet = pet }, out reason) && reason.Contains("Pet Attack"), reason);
            int?[] petBound = (int?[])bindings.Clone();
            petBound[ForeverBindings.PetAttack.Index - 1] = Encode(ForeverBindings.PetAttack.Index, ConsoleKey.Subtract, ModifierKey.None);
            Check("preflight: a pet with Pet Attack bound passes",
                rotation.TryPreflight(Frame(target: false) with { Pet = pet, Bindings = New<VisualBindingSnapshot>((object)petBound) },
                    out reason), reason);
            Check("preflight: a dead pet needs no binding",
                rotation.TryPreflight(Frame(target: false) with { Pet = pet with { Dead = true } }, out reason), reason);

            // The bar decides the rank: Holy Light rank 2 (639) where the profile names rank 1 (635).
            VisualBarSample WithSlot1(int spellId)
            {
                VisualActionSample?[] bar = new VisualActionSample?[VisualActionBarSnapshot.SlotCount];
                for (int s = 1; s <= bar.Length; s++)
                    bar[s - 1] = frame.ActionBar!.GetSlot(s);
                bar[0] = new(1, VisualActionKind.Spell, spellId);
                return frame with { ActionBar = New<VisualActionBarSnapshot>((object)bar) };
            }
            bool HolyLightIs(int spellId) =>
                config.Adhoc.Sequence.Concat(config.Combat.Sequence).Where(a => a.Name == "Holy Light").All(a => a.SpellId == spellId);
            Check("preflight takes another rank of the spell on the slot, as the bar holds after training",
                rotation.TryPreflight(WithSlot1(639), out reason) && HolyLightIs(639), reason);
            Check("preflight still refuses another spell on the slot, naming the action",
                !rotation.TryPreflight(WithSlot1(678), out reason) && reason.Contains("Holy Light") && HolyLightIs(639), reason);
            Check("preflight follows the bar back to the rank the profile names",
                rotation.TryPreflight(frame, out reason) && HolyLightIs(635), reason);
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
                ForeverTargetSelector selector = new(null!, null!, cfg);
                KeyAction? k = (KeyAction?)keyFor.Invoke(selector, [frame]);
                return k?.ConsoleKey ?? ConsoleKey.NoName;
            }
            Check("clear target: the profile's key when the client binds it to a main-bar macro",
                ClearKey("7", KeyFrame()) == ConsoleKey.D7);
            Check("clear target: the profile's key is refused when its slot holds a spell",
                ClearKey("7", KeyFrame(slot7: VisualActionKind.Spell)) == ConsoleKey.NoName);
            Check("clear target: the profile's key is refused when the client rebinds the slot",
                ClearKey("7", KeyFrame(slot7Key: ConsoleKey.D8)) == ConsoleKey.NoName);
            Check("clear target: the clear-target binding when the client has one, as ForeverProbe binds",
                ClearKey("", KeyFrame(customClear: ConsoleKey.Insert)) == ConsoleKey.Insert);
            ClassConfiguration none = new();
            Check("clear target: never Escape; with neither there is no key and nothing is pressed",
                ClearKey("", KeyFrame()) == ConsoleKey.NoName &&
                !new ForeverTargetSelector(null!, null!, none).CanClearTarget(KeyFrame()) &&
                !new ForeverTargetSelector(null!, null!, none).ClearTarget(KeyFrame()));
            Check("clear target: nothing pressed without a target",
                !new ForeverTargetSelector(null!, null!, none).ClearTarget(KeyFrame(target: false)));
            Check("forward: the live binding, not a fixed W",
                ForeverBindings.MoveForward.IsBoundTo(KeyFrame(forward: ConsoleKey.UpArrow), ConsoleKey.UpArrow) &&
                !ForeverBindings.MoveForward.IsBoundTo(KeyFrame(forward: ConsoleKey.UpArrow), ConsoleKey.W));
        }

        TargetingChecks(Check, config, (target, inCombat) => Frame(target: target, combat: inCombat), NewRotation());
        LootChecks(Check);
        HopChecks(Check);
        PhaseChecks(Check);
        DangerChecks(Check);
        TravelChecks(Check);
        RoamChecks(Check);
        PatrolChecks(Check);
        OnTheWayChecks(Check);
        RouteChecks(Check);
        TrackingChecks(Check);

        Console.WriteLine(failures == 0 ? "All logic checks passed." : $"{failures} logic checks failed.");
        return failures == 0 ? 0 : 1;
    }
}
