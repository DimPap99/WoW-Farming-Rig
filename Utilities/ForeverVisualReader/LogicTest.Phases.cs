using Core;
using Core.Forever;
using Core.Goals;
using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;
using SharedLib.Forever;

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // The controller's activities, built for real: what each step allows,
    // the moves that used to leave the declaration or type into chat, which
    // activity the facts pick, and the agendas of the bounded trials.
    private static void PhaseChecks(Action<string, bool, string> check)
    {
        // Nothing here presses: the camera has no process, and stopping a
        // camera that holds nothing sends nothing. A step that tried to type
        // a command or click would meet the null collaborators and throw.
        static ForeverNavigator NewNavigator() =>
            new(NullLogger<ForeverNavigator>.Instance, null!, new MouseCameraInput(null!), null!, null!);
        static ForeverEngageActivity NewEngage(ForeverNavigator navigator, ClassConfiguration config) =>
            new(NullLogger<ForeverEngageActivity>.Instance, null!, navigator, null!, config, new ForeverCorpses());
        static ForeverFacts Facts(bool inCombat = false, bool danger = false, int bodies = 0, bool canOpen = true) =>
            new(Stopwatch.GetTimestamp(), inCombat, danger, TakingDamage: false, Engaged: false,
                bodies, canOpen, LastKillAt: 0);
        ClassConfiguration config = new();
        VisualBarSample vanished = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            TargetFlags = new(Present: false, Attackable: false, Dead: false, PlayerCombat: true, PlayerDead: false)
        };

        // Walking hops when snagged wherever the run walks, only hunting
        // hops for no reason, and nothing else hops.
        ForeverNavigator navigator = NewNavigator();
        check("navigator: nothing is allowed until the goal says so", navigator.Allowed == ForeverAllowed.None, "");
        ForeverEngageActivity engage = NewEngage(navigator, config);
        ForeverLootActivity looting = new(NullLogger<ForeverLootActivity>.Instance, null!, new ForeverCorpses(), navigator, config);
        ForeverRestActivity resting = new(NullLogger<ForeverRestActivity>.Instance, null!, navigator);
        ForeverThreatActivity threat = new(NullLogger<ForeverThreatActivity>.Instance, engage, navigator, null!, config, new ForeverCorpses());
        ForeverHuntHere hunt = new(NullLogger.Instance, new ForeverHuntHereTask(ForeverUntil.Never), engage, navigator, null!, config);
        check("steps: snag hops when approaching and looting, idle hops only when hunting, none in Select, Fight, Threat or Rest",
            engage.AllowedIn(ForeverEngageState.Approach) == ForeverAllowed.SnagHop &&
            looting.AllowedIn(ForeverLootState.Round) == ForeverAllowed.SnagHop &&
            hunt.AllowedIn(ForeverHuntHereState.Search) == (ForeverAllowed.SnagHop | ForeverAllowed.IdleHops) &&
            new[] { ForeverEngageState.Idle, ForeverEngageState.Select, ForeverEngageState.Fight, ForeverEngageState.Failed }
                .All(state => engage.AllowedIn(state) == ForeverAllowed.None) &&
            threat.AllowedIn(ForeverThreatState.Watch) == ForeverAllowed.None &&
            resting.AllowedIn(ForeverRestState.Rest) == ForeverAllowed.None,
            $"approach={engage.AllowedIn(ForeverEngageState.Approach)}, loot={looting.AllowedIn(ForeverLootState.Round)}, " +
            $"hunt={hunt.AllowedIn(ForeverHuntHereState.Search)}, fight={engage.AllowedIn(ForeverEngageState.Fight)}");

        // A target that vanished in the fight: the death latch gets a moment,
        // then the engagement ends and the selector moves on. Nothing restores
        // it through chat.
        ForeverFacts fighting = Facts(inCombat: true);
        engage.Adopt(vanished, new VisualTargetIdentity(10, 11, 5, false));
        engage.Enter(vanished, fighting);
        engage.Update(vanished, fighting);
        bool waited = engage.State == ForeverEngageState.Fight && engage.Engaged;
        Thread.Sleep(1600);
        engage.Update(vanished, fighting);
        check("engage: a vanished target waits for its death latch, then the engagement ends",
            waited && engage.State == ForeverEngageState.Idle && !engage.Engaged,
            $"{engage.Status} engaged={engage.Engaged} '{engage.LastFailure}'");

        // Combat that lingers with nothing hitting the player and no attacker
        // in view: Threat stands still. A turn would press the camera, which
        // has no process here, and throw.
        ForeverNavigator waitingNavigator = NewNavigator();
        ForeverThreatActivity waiting = new(NullLogger<ForeverThreatActivity>.Instance,
            NewEngage(waitingNavigator, config), waitingNavigator, null!, config, new ForeverCorpses());
        waiting.Enter(vanished, fighting);
        waiting.Update(vanished, fighting);
        waiting.Update(vanished, fighting);
        check("threat: combat that lingers without damage or an attacker stands still, no spinning",
            waiting.State == ForeverThreatState.Watch, $"{waiting.Status} '{waiting.LastFailure}'");

        // Select without a candidate stops the run with a reason instead of an undeclared move.
        ForeverEngageActivity selecting = NewEngage(NewNavigator(), config);
        typeof(ForeverEngageActivity).GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(selecting, [new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320)]);
        check("engage: Select without a chosen unit stops the run with that reason",
            selecting.State == ForeverEngageState.Failed && selecting.LastFailure == "selecting without a chosen unit",
            $"{selecting.Status} '{selecting.LastFailure}'");

        // Which activity runs, from the facts alone: the precedence the old
        // Search phase kept in the order of its branches. The activities are
        // listed out of order; the selector sorts them by cost.
        ForeverNavigator chooser = NewNavigator();
        ForeverEngageActivity engaged = NewEngage(chooser, config);
        ForeverRestActivity rest = new(NullLogger<ForeverRestActivity>.Instance, null!, chooser);
        ForeverSelector selector = new(NullLogger.Instance,
            new ForeverTaskActivity(NullLogger<ForeverTaskActivity>.Instance,
                ForeverAgenda.For(new ForeverRunOptions()), engaged, chooser, null!, config, null!, new ForeverMobMemory(), null!),
            rest,
            new ForeverThreatActivity(NullLogger<ForeverThreatActivity>.Instance, engaged, chooser, null!, config, new ForeverCorpses()),
            new ForeverLootActivity(NullLogger<ForeverLootActivity>.Instance, null!, new ForeverCorpses(), chooser, config),
            engaged);
        string Picked(ForeverFacts facts) => selector.Pick(facts)?.Name ?? "none";
        check("selector: nothing pressing runs the agenda's task", Picked(Facts()) == "Task", Picked(Facts()));
        check("selector: in combat with nothing engaged, Threat finds what fights",
            Picked(Facts(inCombat: true)) == "Threat", Picked(Facts(inCombat: true)));
        check("selector: in danger the bodies wait and Threat goes first",
            Picked(Facts(inCombat: true, danger: true, bodies: 1)) == "Threat",
            Picked(Facts(inCombat: true, danger: true, bodies: 1)));
        check("selector: combat that only lingers loots the bodies",
            Picked(Facts(inCombat: true, bodies: 1)) == "Loot", Picked(Facts(inCombat: true, bodies: 1)));
        check("selector: too low to open a fight, Rest", Picked(Facts(canOpen: false)) == "Rest", Picked(Facts(canOpen: false)));
        rest.Pending = true;
        check("selector: after a fight the bodies are looted before the rest",
            Picked(Facts(bodies: 2)) == "Loot", Picked(Facts(bodies: 2)));
        check("selector: after a fight with no bodies left, Rest", Picked(Facts()) == "Rest", Picked(Facts()));
        check("selector: attacked while resting, Threat", Picked(Facts(inCombat: true)) == "Threat", Picked(Facts(inCombat: true)));
        engaged.Adopt(vanished, new VisualTargetIdentity(20, 21, 5, false));
        check("selector: a unit engaged comes before everything",
            Picked(Facts(inCombat: true, danger: true, bodies: 1, canOpen: false)) == "Engage",
            Picked(Facts(inCombat: true, danger: true, bodies: 1, canOpen: false)));
        ForeverLootActivity lootOff = new(NullLogger<ForeverLootActivity>.Instance, null!, new ForeverCorpses(), chooser,
            new ClassConfiguration { Loot = false });
        check("selector: with Loot off, bodies do not hold the run", !lootOff.Wants(Facts(bodies: 1)), "");

        // A round that collected its last body still gets the next frame to
        // end itself, and the rest that follows a round comes due; danger
        // ends it at once.
        ScriptedLoot script = new();
        ForeverLootActivity round = new(NullLogger<ForeverLootActivity>.Instance, script, new ForeverCorpses(),
            NewNavigator(), config);
        bool restDue = false;
        round.RoundEnded += () => restDue = true;
        VisualBarSample lootFrame = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            Loot = new VisualLootState(1, 1, 1, 0, 0, false, null, 0, 0)
        };
        round.Enter(lootFrame, Facts(bodies: 1));
        script.Next = ForeverLootResult.Collected;
        round.Update(lootFrame, Facts(bodies: 1));
        bool stillWants = round.Wants(Facts(bodies: 0)) && !round.Wants(Facts(inCombat: true, danger: true));
        script.Next = ForeverLootResult.Done;
        round.Update(lootFrame, Facts(bodies: 0));
        check("loot: after its last body the round ends itself, and a rest comes due",
            stillWants && restDue && round.State == ForeverLootState.Idle && !round.Wants(Facts(bodies: 0)) &&
            round.Collected == 1, $"stillWants={stillWants}, restDue={restDue}, {round.State}, collected={round.Collected}");

        // The bounded trials as agendas, and kills counting toward the current task.
        ForeverAgenda autoRun = ForeverAgenda.For(new ForeverRunOptions { MaxCompletedCycles = 1 });
        ForeverAgenda sweep = ForeverAgenda.For(new ForeverRunOptions { StopWhenNoTargets = true });
        check("agenda: --auto-run hunts here until one kill, --sweep until nothing is in view",
            autoRun.Current is ForeverHuntHereTask { Until: { Kills: 1, NothingInView: false } } &&
            sweep.Current is ForeverHuntHereTask { Until: { Kills: null, NothingInView: true } },
            $"{autoRun.Current} / {sweep.Current}");
        bool before = autoRun.Current!.Until.IsMet(autoRun.Progress, default);
        autoRun.CountKill();
        check("until: a kill counts toward the current task, which then is done",
            !before && autoRun.Current!.Until.IsMet(autoRun.Progress, default), autoRun.Progress.ToString());
        ForeverAgenda two = new([ForeverTasks.HuntHere(ForeverUntil.AfterKills(1)), ForeverTasks.HuntHere(ForeverUntil.Never)]);
        two.CountKill();
        two.Advance();
        bool fresh = two.Current?.Until == ForeverUntil.Never && two.Progress.Kills == 0;
        two.Advance();
        check("agenda: the next task starts with fresh progress, and the agenda finishes after the last",
            fresh && two.Finished, $"fresh={fresh}, finished={two.Finished}");

        // The fallback ladder: what fails falls back to the goal above it.
        ForeverTask first = ForeverTasks.HuntHere(ForeverUntil.AfterKills(1));
        ForeverTask last = ForeverTasks.HuntHere(ForeverUntil.AfterKills(2));
        ForeverAgenda route = new([first, last]);
        route.GiveUp();
        bool movedOn = route.Current == last && route.GivenUp == 1;
        route.GiveUp();
        bool huntsHere = route.Current is ForeverHuntHereTask { Until.Minutes: ForeverAgenda.FallbackMinutes } && !route.Finished;
        route.Complete();
        check("agenda: a task given up makes way for the next; the last, given up, is tried again after hunting here a while",
            movedOn && huntsHere && route.Current == last && route.Completed == 1 && route.GivenUp == 2, route.Describe());
        ForeverAgenda lap = new([first, last], repeat: true);
        lap.Advance();
        lap.GiveUp();
        check("agenda: a repeating agenda that gives its last task up starts its next lap",
            lap.Current == first && lap.Laps == 1, lap.Describe());

        ForeverTaskActivity tasks = new(NullLogger<ForeverTaskActivity>.Instance, new ForeverAgenda([first, last]),
            NewEngage(NewNavigator(), config), NewNavigator(), null!, config, null!, new ForeverMobMemory(), null!);
        string gaveUp = tasks.FallBack("no spot could be reached");
        check("task: a task that fails is given up and the agenda goes on with the next",
            gaveUp.Contains("given up") && gaveUp.EndsWith($"next: {last.Name}"), gaveUp);

        ForeverNavigator fallbackNavigator = NewNavigator();
        VisualTargetIdentity unit = new(30, 31, 5, false);
        ForeverEngageActivity calm = NewEngage(fallbackNavigator, config);
        calm.Adopt(vanished, unit);
        calm.Enter(vanished, Facts());
        calm.FallBack("the fight did not end within the combat bound");
        ForeverEngageActivity pressed = NewEngage(fallbackNavigator, config);
        pressed.Adopt(vanished, unit);
        pressed.Enter(vanished, Facts(inCombat: true));
        pressed.FallBack("target identity was lost during combat");
        check("engage: a fight that fails starts afresh; out of combat its unit is set aside, in combat it stays a threat",
            calm.State == ForeverEngageState.Idle && !calm.Engaged && calm.Exclusions.IsExcluded(Plate(1, hash: 30)) &&
            pressed.State == ForeverEngageState.Idle && !pressed.Exclusions.IsExcluded(Plate(1, hash: 30)),
            $"{calm.State} {pressed.State}");

        // A body seen within duel distance, then its death.
        ForeverCorpses leftBodies = new();
        VisualBarSample sighted = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            Position = new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 0),
            WorldPosition = new(100, 200, 0, 1),
            TargetFlags = new(true, true, false, false, false),
            TargetIdentity = new(11, 22, 7, false),
            TargetScreenPosition = new VisualScreenPointSample(0.5, 0.5),
            Attack = new VisualAttackState(0, 0, false, true),
            LastDeathEvent = new(5, 1, 2),
        };
        leftBodies.Observe(sighted, Stopwatch.GetTimestamp());
        leftBodies.Observe(sighted with { LastDeathEvent = new(6, 11, 22) }, Stopwatch.GetTimestamp());
        bool hadBody = leftBodies.Any;
        ForeverLootActivity failedRound = new(NullLogger<ForeverLootActivity>.Instance, new ScriptedLoot(), leftBodies,
            NewNavigator(), config);
        failedRound.Enter(lootFrame, Facts(bodies: 1));
        failedRound.FallBack("walking failed");
        ForeverRestActivity failedRest = new(NullLogger<ForeverRestActivity>.Instance, null!, NewNavigator()) { Pending = true };
        failedRest.Enter(vanished, Facts());
        failedRest.FallBack("health did not recover in time");
        check("loot and rest: a failed round leaves its bodies, a failed rest is over; both start afresh",
            hadBody && failedRound.State == ForeverLootState.Idle && !leftBodies.Any &&
            failedRest.State == ForeverRestState.Idle && !failedRest.Pending, $"{failedRound.State} {failedRest.State}");

        // The selector enters an activity that fell back afresh, even when it picks the same one.
        ForeverNavigator watchNavigator = NewNavigator();
        ForeverThreatActivity watcher = new(NullLogger<ForeverThreatActivity>.Instance,
            NewEngage(watchNavigator, config), watchNavigator, null!, config, new ForeverCorpses());
        ForeverSelector restarting = new(NullLogger.Instance, watcher);
        restarting.Switch(vanished, fighting);
        bool watching = watcher.State == ForeverThreatState.Watch;
        watcher.FallBack("no safe target found within acquisition time");
        restarting.Switch(vanished, fighting);
        bool idleWithoutRestart = watcher.State == ForeverThreatState.Idle;
        restarting.Restart();
        restarting.Switch(vanished, fighting);
        check("selector: an activity that fell back is entered afresh when it is picked again",
            watching && idleWithoutRestart && watcher.State == ForeverThreatState.Watch, $"{watcher.State}");

        ForeverSetbacks setbacks = new();
        bool quiet = Enumerable.Range(1, ForeverSetbacks.MaxInARow - 1).All(i => !setbacks.Absorb($"failure {i}"));
        setbacks.Progress();
        bool cleared = setbacks.InARow == 0 && setbacks.First.Length == 0;
        bool[] escalations = [.. Enumerable.Range(1, 3 * ForeverSetbacks.MaxInARow).Select(i => setbacks.Absorb($"again {i}"))];
        check("setbacks: failures never stop the run; every tenth in a row without progress gives the task up as well, the first named",
            quiet && cleared && escalations.Count(up => up) == 3 && escalations[ForeverSetbacks.MaxInARow - 1] &&
            setbacks.First == "again 1" && setbacks.InARow == 3 * ForeverSetbacks.MaxInARow,
            $"{escalations.Count(up => up)} escalations, first '{setbacks.First}'");

        // A frame that misses an input names it, for the pause's log line.
        check("frame: an unreadable frame names what it misses",
            ForeverFrame.MissingTrialInput(new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320)) == "the map position" &&
            ForeverFrame.MissingTrialInput(vanished with
            {
                Position = new(ForeverVerifiedSetup.Durotar, 0.5, 0.5, 0),
                WorldPosition = new(1, 2, 0, 1),
            }) == "the player's identity", "");
    }

    /// <summary>A loot round whose next result the check sets; it presses nothing.</summary>
    private sealed class ScriptedLoot : IForeverLootRoutine
    {
        public ForeverLootResult Next { get; set; }

        public void Begin() { }

        public ForeverLootResult Update(VisualBarSample bars, bool inDanger, out string reason)
        {
            reason = string.Empty;
            return Next;
        }
    }

    // Danger: losing health or an attacker in view, while in combat. Combat
    // that only lingers after the last kill is not danger.
    private static void DangerChecks(Action<string, bool, string> check)
    {
        long t0 = Stopwatch.GetTimestamp();
        long At(double seconds) => t0 + (long)(seconds * Stopwatch.Frequency);
        VisualBarSample Health(double health, bool combat = true, VisualNameplateRoster? plates = null) =>
            new VisualBarSample(health, 1, 0, 0.5, 0, 0, 320) with
            {
                TargetFlags = new(false, false, false, combat, false),
                Nameplates = plates
            };

        ForeverDanger hit = new();
        hit.Observe(Health(1.0), At(0));
        hit.Observe(Health(0.995), At(0.1));
        check("danger: bar noise is no damage", !hit.TakingDamage(At(0.2)), "");
        hit.Observe(Health(0.98), At(0.3));
        check("danger: a fall of health is damage, and in combat danger",
            hit.TakingDamage(At(0.4)) && hit.InDanger(Health(0.98), At(0.4)), "");
        check("danger: out of combat there is none", !hit.InDanger(Health(0.98, combat: false), At(0.4)), "");
        check("danger: damage passes 3 s after the last fall", !hit.TakingDamage(At(3.4)), "");

        ForeverDanger bleed = new();
        bleed.Observe(Health(1.0), At(0));
        bleed.Observe(Health(0.996), At(1));
        bleed.Observe(Health(0.992), At(2));
        bleed.Observe(Health(0.988), At(3));
        check("danger: a slow bleed adds up to damage", bleed.TakingDamage(At(3.1)), "");

        ForeverDanger healed = new();
        healed.Observe(Health(0.5), At(0));
        healed.Observe(Health(0.9), At(1));
        healed.Observe(Health(0.895), At(2));
        check("danger: healing is no damage", !healed.TakingDamage(At(2.1)), "");

        ForeverDanger quiet = new();
        quiet.Observe(Health(0.8), At(0));
        check("danger: combat that lingers without damage or an attacker is none",
            !quiet.InDanger(Health(0.8), At(1)), "");
        VisualNameplate attacker = Plate(1, hash: 10, targetsPlayer: true);
        check("danger: an attacker in view is danger", quiet.InDanger(Health(0.8, plates: Roster(attacker)), At(1)), "");
        check("danger: a dead or just-killed attacker is not",
            !quiet.InDanger(Health(0.8, plates: Roster(attacker with { Dead = true })), At(1)) &&
            !quiet.InDanger(Health(0.8, plates: Roster(attacker)) with { LastDeathEvent = new(3, 10, 11) }, At(1)), "");

        ForeverDanger resting = new();
        resting.Observe(Health(1.0), At(0));
        resting.Observe(Health(0.9), At(1));
        check("danger: damage from before a rest began does not count against it",
            !resting.DamagedSince(At(2)) && resting.DamagedSince(At(0.5)), "");
    }
}
