using Core;
using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using System;
using System.Collections.Generic;
using System.Threading;

namespace ForeverVisualReader;

internal static partial class LogicTest
{
    private enum Step { Start, Walk, Fight, Done, Failed }

    private static void StateMachineChecks(Action<string, bool, string> check)
    {
        VisualBarSample frame = new(1, 1, 1, 0.5, 0, 0, 320) { FrameId = 42 };

        // A small run: Start -> Walk -> Fight -> Done, with hooks recorded.
        List<string> log = [];
        long seenFrame = 0;
        ForeverStateMachine<Step> Machine(int walkTimeoutMs = 0) =>
            new ForeverStateMachine<Step>(NullLogger.Instance, "test", Step.Start, Step.Failed, Step.Done)
                .On(Step.Start, bars => seenFrame = bars.FrameId, exits: [Step.Walk],
                    exit: () => log.Add("exit Start"))
                .On(Step.Walk, _ => log.Add("tick Walk"), exits: [Step.Fight],
                    timeoutMs: walkTimeoutMs, onTimeout: walkTimeoutMs > 0 ? "walk took too long" : null,
                    enter: () => log.Add("enter Walk"), exit: () => log.Add("exit Walk"))
                .On(Step.Fight, _ => { }, exits: [Step.Done])
                .On(Step.Failed, enter: () => log.Add("enter Failed"));

        ForeverStateMachine<Step> machine = Machine();
        machine.Tick(frame);
        check("machine: the state's work gets the frame", machine.State == Step.Start && seenFrame == 42, $"{machine.State} {seenFrame}");

        machine.Go(Step.Walk);
        check("machine: a declared exit moves, exit hook before enter hook",
            machine.State == Step.Walk && string.Join(",", log) == "exit Start,enter Walk", string.Join(",", log));
        check("machine: entering resets the time in state", machine.ElapsedMs < 1000, $"{machine.ElapsedMs}");

        log.Clear();
        machine.Go(Step.Done);
        check("machine: an undeclared exit fails the run instead of moving",
            machine.State == Step.Failed && machine.LastFailure.Contains("may not move to Done") &&
            string.Join(",", log) == "exit Walk,enter Failed", $"{machine.State} '{machine.LastFailure}' {string.Join(",", log)}");

        machine.Go(Step.Fight);
        machine.Fail("later");
        machine.Tick(frame);
        check("machine: failed stays failed and keeps the first reason",
            machine.State == Step.Failed && machine.IsTerminal && machine.LastFailure.Contains("may not move"),
            $"{machine.State} '{machine.LastFailure}'");

        machine = Machine();
        machine.Go(Step.Walk);
        machine.Go(Step.Fight);
        machine.Go(Step.Done);
        machine.Fail("too late");
        check("machine: a finished run is terminal and ignores a later failure",
            machine.State == Step.Done && machine.IsTerminal && machine.LastFailure.Length == 0,
            $"{machine.State} '{machine.LastFailure}'");

        log.Clear();
        machine = Machine(walkTimeoutMs: 1);
        machine.Go(Step.Walk);
        Thread.Sleep(20);
        log.Clear();
        machine.Tick(frame);
        check("machine: past its bound a state fails before doing its work",
            machine.State == Step.Failed && machine.LastFailure == "walk took too long" &&
            !log.Contains("tick Walk"), $"{machine.State} '{machine.LastFailure}' {string.Join(",", log)}");

        // A bound may hand over instead of failing, and fails if nobody moves.
        ForeverStateMachine<Step> handing = null!;
        handing = new ForeverStateMachine<Step>(NullLogger.Instance, "test", Step.Start, Step.Failed, Step.Done)
            .On(Step.Start, _ => { }, exits: [Step.Walk], timeoutMs: 1, onTimeout: "slow start",
                timedOut: reason => handing.Go(Step.Walk))
            .On(Step.Walk, _ => { }, exits: [Step.Fight], timeoutMs: 1, onTimeout: "slow walk",
                timedOut: _ => { });
        Thread.Sleep(20);
        handing.Tick(frame);
        check("machine: a bound can hand over to a declared exit", handing.State == Step.Walk, $"{handing.State}");
        Thread.Sleep(20);
        handing.Tick(frame);
        check("machine: a handler that does not move still fails the run",
            handing.State == Step.Failed && handing.LastFailure == "slow walk", $"{handing.State} '{handing.LastFailure}'");

        // A goal that falls back from a failure starts its machine afresh.
        ForeverStateMachine<Step> again = Machine();
        again.Go(Step.Walk);
        again.Fail("stuck");
        again.Reset();
        bool restarted = again.State == Step.Start && !again.IsTerminal && again.LastFailure == "stuck";
        again.Go(Step.Walk);
        log.Clear();
        again.Reset();
        check("machine: a reset starts afresh from a failed state, the failure kept on record, and from a live one after its exit hook",
            restarted && again.State == Step.Start && string.Join(",", log) == "exit Walk",
            $"{again.State} '{again.LastFailure}' {string.Join(",", log)}");

        // What a state allows besides its work is declared with it.
        ForeverStateMachine<Step> allowing = new ForeverStateMachine<Step>(NullLogger.Instance, "test",
                Step.Start, Step.Failed, Step.Done)
            .On(Step.Start, _ => { }, exits: [Step.Walk])
            .On(Step.Walk, _ => { }, exits: [Step.Fight], allows: ForeverAllowed.SnagHop | ForeverAllowed.IdleHops)
            .On(Step.Fight, _ => { }, exits: [Step.Done]);
        bool startAllowsNothing = allowing.Allowed == ForeverAllowed.None;
        allowing.Go(Step.Walk);
        bool walkAllows = allowing.Allowed == (ForeverAllowed.SnagHop | ForeverAllowed.IdleHops);
        allowing.Go(Step.Fight);
        check("machine: a state allows what it declares, and nothing once left",
            startAllowsNothing && walkAllows && allowing.Allowed == ForeverAllowed.None &&
            allowing.AllowedIn(Step.Walk) == (ForeverAllowed.SnagHop | ForeverAllowed.IdleHops) &&
            allowing.AllowedIn(Step.Done) == ForeverAllowed.None, $"{allowing.Allowed}");

        bool threw = false;
        try
        {
            new ForeverStateMachine<Step>(NullLogger.Instance, "test", Step.Start, Step.Failed)
                .On(Step.Start, _ => { }, timeoutMs: 100);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        check("machine: a bound needs a reason", threw, "");

        // An abort arrives on another thread while the controller ticks and moves.
        int entries = 0, exits = 0;
        ForeverStateMachine<Step> racing = new ForeverStateMachine<Step>(NullLogger.Instance, "test",
                Step.Start, Step.Failed, Step.Done)
            .On(Step.Start, _ => { }, exits: [Step.Walk])
            .On(Step.Walk, _ => { }, exits: [Step.Start],
                enter: () => Interlocked.Increment(ref entries),
                exit: () => Interlocked.Increment(ref exits))
            .On(Step.Failed);
        using ManualResetEventSlim go = new();
        Thread abort = new(() =>
        {
            go.Wait();
            racing.Fail("stopped");
        });
        abort.Start();
        for (int i = 0; i < 20000; i++)
        {
            if (i == 100)
                go.Set();
            racing.Tick(frame);
            racing.Go(racing.State == Step.Start ? Step.Walk : Step.Start);
        }
        abort.Join();
        check("machine: a failure from another thread ends the run once, every entry exited once",
            racing.State == Step.Failed && racing.LastFailure == "stopped" &&
            entries > 0 && exits == entries, $"{racing.State} '{racing.LastFailure}' entries={entries} exits={exits}");
    }
}
