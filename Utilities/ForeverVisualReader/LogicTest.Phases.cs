using Core;
using Core.Forever;
using Core.Goals;
using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    // The controller's own phases, read from a real goal: what each allows,
    // and the moves that used to leave the declaration or type into chat.
    private static void PhaseChecks(Action<string, bool, string> check)
    {
        // Nothing here presses: the camera has no process, and stopping a
        // camera that holds nothing sends nothing. A phase that tried to type
        // a command or click would meet the null collaborators and throw.
        ForeverAutonomousGoal NewGoal(out ForeverNavigator navigator)
        {
            navigator = new(NullLogger<ForeverNavigator>.Instance, null!, new MouseCameraInput(null!), null!, null!);
            return new(NullLogger<ForeverAutonomousGoal>.Instance, null!, null!, navigator,
                null!, null!, new ForeverCorpses(), new ForeverRunOptions(), new ClassConfiguration(),
                new CancellationTokenSource<GoapAgent>());
        }
        Type goalType = typeof(ForeverAutonomousGoal);
        Type phase = goalType.GetNestedType("Phase", BindingFlags.NonPublic)!;
        object MachineOf(ForeverAutonomousGoal goal) =>
            goalType.GetField("machine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(goal)!;
        void Invoke(ForeverAutonomousGoal goal, string method, VisualBarSample bars) =>
            goalType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(goal, [bars]);

        // Walking hops when snagged wherever the run walks, only roaming hops
        // for no reason, and nothing else hops.
        ForeverAutonomousGoal goal = NewGoal(out ForeverNavigator navigator);
        check("navigator: nothing is allowed until the goal says so", navigator.Allowed == ForeverAllowed.None, "");
        object machine = MachineOf(goal);
        MethodInfo allowedIn = machine.GetType().GetMethod("AllowedIn")!;
        ForeverAllowed In(string name) => (ForeverAllowed)allowedIn.Invoke(machine, [Enum.Parse(phase, name)])!;
        check("phases: snag hops in Search, Approach and Loot, idle hops only in Search's roaming, none in Fight",
            In("Search") == (ForeverAllowed.SnagHop | ForeverAllowed.IdleHops) &&
            In("Approach") == ForeverAllowed.SnagHop && In("Loot") == ForeverAllowed.SnagHop &&
            new[] { "Start", "Select", "Fight", "Rest", "Recover", "Complete", "Failed" }
                .All(name => In(name) == ForeverAllowed.None),
            string.Join(", ", Enum.GetNames(phase).Select(name => $"{name}={In(name)}")));

        // A target that vanished in the fight: the death latch gets a moment,
        // then the run searches again. Nothing restores it through chat.
        MethodInfo go = machine.GetType().GetMethod("Go")!;
        go.Invoke(machine, [Enum.Parse(phase, "Search")]);
        go.Invoke(machine, [Enum.Parse(phase, "Fight")]);
        VisualBarSample vanished = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            TargetFlags = new(Present: false, Attackable: false, Dead: false, PlayerCombat: true, PlayerDead: false)
        };
        Invoke(goal, "Fight", vanished);
        bool waited = goal.Status == "Fight";
        Thread.Sleep(1600);
        Invoke(goal, "Fight", vanished);
        check("phases: a vanished target waits for its death latch, then the run searches again",
            waited && goal.Status == "Search", $"{goal.Status} '{goal.LastFailure}'");

        // Combat that lingers with nothing hitting the player and no attacker
        // in view: Search stands still. A turn would press the camera, which
        // has no process here, and throw.
        ForeverAutonomousGoal waiting = NewGoal(out _);
        object waitingMachine = MachineOf(waiting);
        waitingMachine.GetType().GetMethod("Go")!.Invoke(waitingMachine, [Enum.Parse(phase, "Search")]);
        Invoke(waiting, "Search", vanished);
        Invoke(waiting, "Search", vanished);
        check("phases: combat that lingers without damage or an attacker: Search waits, no spinning",
            waiting.Status == "Search", $"{waiting.Status} '{waiting.LastFailure}'");

        // Select without a candidate stops the run with a reason instead of an undeclared move.
        ForeverAutonomousGoal selecting = NewGoal(out _);
        Invoke(selecting, "Select", new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320));
        check("phases: Select without a chosen unit stops the run with that reason",
            selecting.Status == "Failed" && selecting.LastFailure == "selecting without a chosen unit",
            $"{selecting.Status} '{selecting.LastFailure}'");
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
