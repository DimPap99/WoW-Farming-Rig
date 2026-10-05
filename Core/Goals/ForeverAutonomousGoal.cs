using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>
/// Forever's visual-only loop. On every fresh frame it reads the facts and
/// runs the cheapest activity that wants to run (<see cref="ForeverSelector"/>):
/// the fight with a unit, finding what fights the player, looting, resting,
/// and otherwise the agenda's current task. The profile's Adhoc heals and
/// buffs run before them. The run completes once the agenda is done. A
/// failure falls back to the goal above it (<see cref="ForeverSetbacks"/>);
/// only what no fallback can make good stops the run.
/// </summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    /// <summary>Where the run is: <see cref="Complete"/> and <see cref="Failed"/> end it.</summary>
    public enum RunState { Start, Running, Complete, Failed }

    // Without a readable frame the run pauses, its keys released, and says
    // why after this long, then again at this interval while it lasts.
    private const int FramePauseMs = 5000;
    private const int FramePauseLogMs = 60000;
    private const int PreflightMs = 5000;

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ForeverCorpses corpses;
    private readonly ForeverMobMemory memory;
    private readonly ClassConfiguration config;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverLootActivity looting;
    private readonly ForeverRestActivity resting;
    private readonly ForeverTaskActivity tasking;
    private readonly ForeverAgenda agenda;
    private readonly ForeverTargetSelector targets;
    private readonly ForeverSelector selector;
    private readonly CancellationToken token;
    private readonly ForeverStateMachine<RunState> machine;
    // Something fights the player, as opposed to combat that merely lingers.
    private readonly ForeverDanger danger = new();
    private readonly ForeverSetbacks setbacks = new();
    private volatile bool stopped;
    private volatile int completedKills;
    private volatile int observedDeaths;
    private long lastActionFrame;
    private long invalidFrameSince;
    private bool framePaused;
    private long framePauseSaidAt;
    // Bodies looted and tasks done when last seen: either is progress.
    private int lootsSeen;
    private int tasksSeen;

    public override float Cost => 1;
    public override bool CanRun() => machine.State != RunState.Failed && !stopped;

    /// <summary>The run's state as a type: what <see cref="Status"/> names when the run is not under way.</summary>
    public RunState State => machine.State;

    /// <summary>Start, Complete or Failed; while running, the current activity and its step.</summary>
    public string Status => machine.State == RunState.Running
        ? selector.Current?.Status ?? nameof(RunState.Running)
        : machine.State.ToString();

    public string LastFailure => machine.LastFailure;

    /// <summary>Which task of the agenda runs and how far it got.</summary>
    public string AgendaStatus => agenda.Describe();

    public int CompletedKills => completedKills;
    public int ObservedDeaths => observedDeaths;
    public int CompletedLoots => looting.Collected;

    /// <summary>The pulls the run would weigh on the latest frame, the best first. Read-only.</summary>
    public IReadOnlyList<ForeverPullOption> PullOptions()
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars))
            return [];
        List<ForeverPullOption> options = ForeverPullOptions.Of(bars, config, combat, engage.Exclusions);
        options.Sort(ForeverPullOptions.Compare);
        return options;
    }

    public ForeverAutonomousGoal(ILogger<ForeverAutonomousGoal> logger,
        ForeverVisualBarTracker tracker, ForeverNavigator navigator,
        IForeverCombatRoutine combat, ForeverCorpses corpses, ForeverMobMemory memory, ClassConfiguration config,
        ForeverEngageActivity engage, ForeverThreatActivity threat, ForeverLootActivity looting,
        ForeverRestActivity resting, ForeverTaskActivity tasking, ForeverAgenda agenda,
        ForeverTargetSelector targets, CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverAutonomousGoal))
    {
        this.logger = logger;
        this.tracker = tracker;
        this.navigator = navigator;
        this.combat = combat;
        this.corpses = corpses;
        this.memory = memory;
        this.config = config;
        this.engage = engage;
        this.looting = looting;
        this.resting = resting;
        this.tasking = tasking;
        this.targets = targets;
        this.agenda = agenda;
        token = cts.Token;

        selector = new ForeverSelector(logger, engage, threat, looting, resting, tasking);
        engage.Killed += Killed;
        looting.RoundEnded += () => resting.Pending = true;

        machine = new ForeverStateMachine<RunState>(logger, "Forever controller",
                RunState.Start, RunState.Failed, RunState.Complete)
            .On(RunState.Start, Start, exits: [RunState.Running])
            .On(RunState.Running, Run, exits: [RunState.Complete])
            .On(RunState.Complete, enter: navigator.StopPursuit)
            .On(RunState.Failed, enter: navigator.StopPursuit);
    }

    public bool TryReadOnlyPreflight(out string reason)
    {
        if (!tracker.TryGetLatest(out VisualBarSample bars) ||
            !ForeverFrame.IsVerifiedSetup(bars) || bars.TargetFlags is not { } flags)
        {
            reason = "no complete visual frame from a verified class";
            return false;
        }
        if (flags.PlayerCombat)
        {
            reason = "the player is in combat";
            return false;
        }
        if (!ForeverFrame.CanOpenFight(bars, inCombat: false))
        {
            reason = $"health {bars.PlayerHealth:P0} or mana {bars.PlayerPower:P0} is below what opening a fight needs";
            return false;
        }
        return TrySetup(bars, out reason) &&
            navigator.TryReadOnlyPreflight(bars, out reason) &&
            tasking.TryPreflight(bars, out reason);
    }

    /// <summary>
    /// What the run needs from the client that no fallback can make good:
    /// the add-on's telemetry, the client settings looting relies on, a
    /// clear-target key, the profile's actions on their slots and usable (an
    /// attack without its ammo stops the run), and walking on a baked map.
    /// Read-only; asked before the run starts and whenever something fails.
    /// </summary>
    private bool TrySetup(VisualBarSample bars, out string reason)
    {
        if (bars.LastDeathEvent is null ||
            config.Loot && (bars.Loot is null || !ForeverBindings.InteractTarget.IsBoundIn(bars)))
        {
            reason = "the death or loot telemetry, or the interact binding, is incomplete";
            return false;
        }
        if (bars.Nameplates is not { } roster)
        {
            reason = "the nameplate roster is unreadable; reload the ForeverProbe add-on that ships with this build";
            return false;
        }
        if (!roster.PlatesShown)
        {
            reason = "enemy nameplates are not always shown; turn on enemy nameplates and Always Show Nameplates in the client";
            return false;
        }
        if (bars.Attack is null)
        {
            reason = "the attack state is unreadable; reload the ForeverProbe add-on that ships with this build";
            return false;
        }
        // Bodies are found with the client's Interact Key and emptied by its Auto Loot.
        if (config.Loot)
        {
            if (bars.SoftInteract is not { } soft)
            {
                reason = "the soft-interact state is unreadable; reload the ForeverProbe add-on that ships with this build";
                return false;
            }
            if (!soft.InteractKeyEnabled)
            {
                reason = "the Interact Key is off; turn on Enable Interact Key in the client's settings";
                return false;
            }
            if (soft.AutoLoot != true)
            {
                reason = "Auto Loot is off; turn on Auto Loot in the client's settings";
                return false;
            }
        }
        // Looting and resting deselect the dead with this key, never with Escape.
        if (!targets.CanClearTarget(bars))
        {
            reason = "no clear-target key; reload the ForeverProbe add-on that ships with this build, which binds one, " +
                "or name the key of a /cleartarget macro on the main bar in the profile's ClearTarget";
            return false;
        }
        // The bar does not change in a fight, and some of what its check reads
        // (the slots' auras) does not read in one: in combat only whether the
        // attacks can be used is asked, as ammo can run out mid-fight.
        bool fighting = bars.TargetFlags is { PlayerCombat: true };
        return (fighting ? combat.TryAttacksUsable(bars, out reason) : combat.TryPreflight(bars, out reason)) &&
            navigator.TrySetup(bars, out reason);
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is AbortEvent)
        {
            // This runs on the thread that stopped the controller, possibly
            // while Update is still running. Halting the navigator stops that
            // update from pressing movement again, so a stopped controller
            // never resumes; load the profile again for a new run.
            stopped = true;
            navigator.Halt();
            if (CompletedKills == 0)
                machine.Fail("Controller stopped");
        }
    }

    public override void Update()
    {
        if (!CanRun() || machine.IsTerminal || token.IsCancellationRequested)
            return;
        try
        {
            bool latest = tracker.TryGetLatest(out VisualBarSample bars);
            if (!latest || !ForeverFrame.IsVerifiedSetup(bars))
            {
                // Nothing is pressed without a frame: the run pauses with its
                // keys released, and resumes where it was once one reads.
                navigator.StopPursuit();
                if (invalidFrameSince == 0)
                    invalidFrameSince = GetTimestamp();
                double missingMs = GetElapsedTime(invalidFrameSince).TotalMilliseconds;
                string why = !latest ? tracker.Problem
                    : ForeverFrame.MissingTrialInput(bars) is { } missing ? $"{missing} did not read"
                    : "the class is not a verified one";
                // Paused as long as it takes, saying so now and then.
                if (missingMs > FramePauseMs && GetElapsedTime(framePauseSaidAt).TotalMilliseconds > FramePauseLogMs)
                {
                    framePaused = true;
                    framePauseSaidAt = GetTimestamp();
                    logger.LogWarning("Forever paused: no readable frame for {Seconds:0} seconds ({Why}); waiting for one",
                        missingMs / 1000, why);
                }
                return;
            }
            if (framePaused)
            {
                framePaused = false;
                framePauseSaidAt = 0;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever resumed after {Seconds:0} seconds without a readable frame",
                        GetElapsedTime(invalidFrameSince).TotalSeconds);
            }
            invalidFrameSince = 0;
            if (bars.FrameId == lastActionFrame)
                return;
            lastActionFrame = bars.FrameId;
            if (bars.TargetFlags!.Value.PlayerDead)
            {
                machine.Fail("player died; corpse recovery has not been verified");
                return;
            }
            // Where every unit in view is, so a body can be found once the fight is over.
            corpses.Observe(bars);
            // Every mob in view, kept a while, so a task can go back to it out of view.
            memory.Observe(bars);
            danger.Observe(bars, GetTimestamp());
            machine.Tick(bars);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forever autonomous controller stopped");
            machine.Fail("controller update threw an exception");
        }
    }

    private void Start(VisualBarSample bars)
    {
        if (!TryReadOnlyPreflight(out string reason))
        {
            if (machine.ElapsedMs > PreflightMs)
                machine.Fail(reason);
            return;
        }
        machine.Go(RunState.Running);
    }

    private void Run(VisualBarSample bars)
    {
        // A kill is verified before anything else acts on the frame.
        if (engage.TryVerifyKill(bars))
            return;
        if (looting.Collected != lootsSeen || agenda.Completed != tasksSeen)
        {
            lootsSeen = looting.Collected;
            tasksSeen = agenda.Completed;
            setbacks.Progress();
        }
        // The profile's Adhoc self heals and buffs, whatever the run does,
        // except while looting out of combat. A corpse selected out of combat
        // is looted or cleared first. A failed cast latches the caster closed;
        // say so here rather than at the next action it refuses.
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (!(selector.Current == looting && !flags.PlayerCombat) &&
            !(flags.Present && flags.Dead && !flags.PlayerCombat) &&
            !combat.RunAdhoc(bars, navigator.StopPursuit, out string adhocReason))
        {
            navigator.StopPursuit();
            if (!string.IsNullOrEmpty(adhocReason))
                FallBack(bars, null, adhocReason);
            return;
        }

        ForeverFacts facts = ForeverFacts.From(bars, danger, corpses, engage, GetTimestamp());
        if (selector.Switch(bars, facts) is not { } activity)
            return;
        // Shared routines such as walking do only what this activity's step allows.
        navigator.Allowed = activity.Allowed;
        ForeverActivityResult result = activity.Update(bars, facts);
        if (result.Status == ForeverActivityStatus.Failed)
            FallBack(bars, activity, result.Reason);
        else if (result.Status == ForeverActivityStatus.Done && activity == tasking)
            machine.Go(RunState.Complete);
    }

    // A failure never ends the run: only what no fallback can change does,
    // the player taking the game back (it lost focus) or the client losing
    // something the run needs. Otherwise the failed activity falls back as
    // it knows how and starts afresh, a failed task is given up for the
    // next, and the selector picks again on the next frame. Every tenth
    // failure in a row without progress gives the current task up as well.
    private void FallBack(VisualBarSample bars, IForeverActivity? activity, string reason)
    {
        if (!navigator.GameHasFocus)
        {
            machine.Fail($"{reason}; the game lost focus, so the player has it now");
            return;
        }
        if (!TrySetup(bars, out string broken))
        {
            machine.Fail($"{reason}; {broken}");
            return;
        }
        // A fresh start: a press whose outcome could not be proved does not
        // keep casting closed.
        combat.ReleaseCasting();
        bool escalate = setbacks.Absorb(reason);
        string fallback = "the run goes on";
        if (activity is not null)
        {
            fallback = activity.FallBack(reason);
            selector.Restart();
        }
        logger.LogWarning("Forever {Activity} failed: {Reason}; falling back: {Fallback} ({Count} in a row without progress)",
            activity?.Name ?? "Adhoc", reason, fallback, setbacks.InARow);
        if (!escalate || activity == tasking)
            return;
        string bigger = tasking.FallBack(reason);
        selector.Restart();
        logger.LogWarning("Forever: {Count} failures in a row without progress, the first: {First}; falling back to a bigger goal: {Bigger}",
            setbacks.InARow, setbacks.First, bigger);
    }

    // A verified kill counts for the run and for the agenda's task. Out of
    // combat the run loots the bodies and rests before the next pull; while
    // something else still fights the player, the fight goes on first.
    private void Killed(VisualBarSample bars)
    {
        observedDeaths++;
        completedKills++;
        agenda.CountKill();
        setbacks.Progress();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever autonomous kill {Count} verified", CompletedKills);
        if (bars.TargetFlags is not { PlayerCombat: true })
            resting.Pending = true;
    }
}
