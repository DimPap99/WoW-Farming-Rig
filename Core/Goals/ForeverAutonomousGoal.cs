using Core.Forever;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>
/// Forever's visual-only loop. On every fresh frame it reads the facts and
/// runs the cheapest activity that wants to run (<see cref="ForeverSelector"/>):
/// the fight with a unit, finding what fights the player, looting, resting,
/// and otherwise the agenda's current task. The profile's Adhoc heals and
/// buffs run before them. The run completes once the agenda is done.
/// </summary>
public sealed class ForeverAutonomousGoal : GoapGoal, IGoapEventListener
{
    private enum RunState { Start, Running, Complete, Failed }

    private const int InvalidFrameMs = 5000;
    private const int PreflightMs = 5000;

    private readonly ILogger<ForeverAutonomousGoal> logger;
    private readonly ForeverVisualBarTracker tracker;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ForeverCorpses corpses;
    private readonly ClassConfiguration config;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverLootActivity looting;
    private readonly ForeverRestActivity resting;
    private readonly ForeverTaskActivity tasking;
    private readonly ForeverAgenda agenda;
    private readonly ForeverSelector selector;
    private readonly CancellationToken token;
    private readonly ForeverStateMachine<RunState> machine;
    // Something fights the player, as opposed to combat that merely lingers.
    private readonly ForeverDanger danger = new();
    private volatile bool stopped;
    private volatile int completedKills;
    private volatile int observedDeaths;
    private long lastActionFrame;
    private long invalidFrameSince;

    public override float Cost => 1;
    public override bool CanRun() => machine.State != RunState.Failed && !stopped;

    /// <summary>Start, Complete or Failed; while running, the current activity and its step.</summary>
    public string Status => machine.State == RunState.Running
        ? selector.Current?.Status ?? nameof(RunState.Running)
        : machine.State.ToString();

    public string LastFailure => machine.LastFailure;
    public int CompletedKills => completedKills;
    public int ObservedDeaths => observedDeaths;
    public int CompletedLoots => looting.Collected;

    public ForeverAutonomousGoal(ILogger<ForeverAutonomousGoal> logger,
        ForeverVisualBarTracker tracker, ForeverNavigator navigator,
        IForeverCombatRoutine combat, ForeverCorpses corpses, ClassConfiguration config,
        ForeverEngageActivity engage, ForeverThreatActivity threat, ForeverLootActivity looting,
        ForeverRestActivity resting, ForeverTaskActivity tasking, ForeverAgenda agenda,
        CancellationTokenSource<GoapAgent> cts)
        : base(nameof(ForeverAutonomousGoal))
    {
        this.logger = logger;
        this.tracker = tracker;
        this.navigator = navigator;
        this.combat = combat;
        this.corpses = corpses;
        this.config = config;
        this.engage = engage;
        this.looting = looting;
        this.resting = resting;
        this.tasking = tasking;
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
            reason = "no complete visual frame from a verified class on a verified map";
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
        return combat.TryPreflight(bars, out reason) &&
            navigator.TryReadOnlyPreflight(bars, out reason);
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
            if (!tracker.TryGetLatest(out VisualBarSample bars) ||
                !ForeverFrame.IsVerifiedSetup(bars))
            {
                navigator.StopPursuit();
                if (invalidFrameSince == 0)
                    invalidFrameSince = GetTimestamp();
                if (GetElapsedTime(invalidFrameSince).TotalMilliseconds > InvalidFrameMs)
                    machine.Fail("visual controller frame unavailable for five continuous seconds");
                return;
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
                machine.Fail(adhocReason);
            return;
        }

        ForeverFacts facts = ForeverFacts.From(bars, danger, corpses, engage, GetTimestamp());
        if (selector.Switch(bars, facts) is not { } activity)
            return;
        // Shared routines such as walking do only what this activity's step allows.
        navigator.Allowed = activity.Allowed;
        ForeverActivityResult result = activity.Update(bars, facts);
        if (result.Status == ForeverActivityStatus.Failed)
            machine.Fail(result.Reason);
        else if (result.Status == ForeverActivityStatus.Done && activity == tasking)
            machine.Go(RunState.Complete);
    }

    // A verified kill counts for the run and for the agenda's task. Out of
    // combat the run loots the bodies and rests before the next pull; while
    // something else still fights the player, the fight goes on first.
    private void Killed(VisualBarSample bars)
    {
        observedDeaths++;
        completedKills++;
        agenda.CountKill();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever autonomous kill {Count} verified", CompletedKills);
        if (bars.TargetFlags is not { PlayerCombat: true })
            resting.Pending = true;
    }
}
