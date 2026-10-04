using Microsoft.Extensions.Logging;

using SharedLib;

using System;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// Carries out one <see cref="ForeverTask"/> over many frames. The task
/// activity enters it whenever nothing more urgent runs and exits it when
/// something does, so a runner resumes from where it was.
/// </summary>
public interface IForeverTaskRunner
{
    string Status { get; }
    /// <summary>What the runner's current step allows besides its work.</summary>
    ForeverAllowed Allowed { get; }
    void Enter(VisualBarSample bars, in ForeverFacts facts);
    /// <summary>Done once the runner's own part of the task is over, such as nothing left in view.</summary>
    ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress progress);
    /// <summary>Releases whatever the runner holds.</summary>
    void Exit();
}

/// <summary>
/// Runs the agenda's current task whenever nothing more urgent does, as
/// upstream's FollowRouteGoal walks its route when no other goal can run.
/// A task ends when its runner says so or its <see cref="ForeverUntil"/>
/// holds, and the next one starts; Done once the agenda is finished.
/// </summary>
public sealed class ForeverTaskActivity : IForeverActivity
{
    /// <summary>Upstream's FollowRouteGoal cost: the task runs when nothing else wants to.</summary>
    public const float Priority = 20f;

    private readonly ILogger<ForeverTaskActivity> logger;
    private readonly ForeverAgenda agenda;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly WorldMapAreaDB maps;
    private readonly ForeverMobMemory memory;
    private ForeverTask? runnerTask;
    private IForeverTaskRunner? runner;
    private bool preflightPassed;

    public ForeverTaskActivity(ILogger<ForeverTaskActivity> logger, ForeverAgenda agenda,
        ForeverEngageActivity engage, ForeverNavigator navigator, IForeverCombatRoutine combat,
        ClassConfiguration config, WorldMapAreaDB maps, ForeverMobMemory memory)
    {
        this.logger = logger;
        this.agenda = agenda;
        this.engage = engage;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        this.maps = maps;
        this.memory = memory;
    }

    public string Name => "Task";
    public float Cost => Priority;
    public ForeverAllowed Allowed => runner?.Allowed ?? ForeverAllowed.None;
    public string Status => runner is { } current ? $"{Name}/{current.Status}" : Name;

    public bool Wants(in ForeverFacts facts) => true;

    /// <summary>
    /// Every task the agenda names can begin from this frame: travel points on
    /// verified maps of the player's continent and on the baked mesh, roam
    /// areas with walkable ground inside. Checked until it passes once; the
    /// agenda's tasks do not change.
    /// </summary>
    public bool TryPreflight(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (preflightPassed)
            return true;
        foreach (ForeverTask task in agenda.Tasks)
        {
            bool ready = task switch
            {
                ForeverTravelTask travel => ForeverTravel.TryResolve(travel, bars, navigator, maps, out _, out reason),
                ForeverRoamAreaTask roam => ForeverRoamArea.TryCheck(roam, bars, navigator, maps, out reason),
                _ => true,
            };
            if (!ready)
            {
                reason = $"{task.Name}: {reason}";
                return false;
            }
        }
        preflightPassed = true;
        return true;
    }

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (runner is not null && runnerTask == agenda.Current)
            runner.Enter(bars, facts);
        else
            StartCurrent(bars, facts);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts)
    {
        if (runnerTask != agenda.Current)
            StartCurrent(bars, facts);
        // A task already done by a condition the agenda tracks is passed by.
        while (agenda.Current is { } done && done.Until.IsMet(agenda.Progress, bars))
            Finish(done, bars, facts);
        if (runner is null)
            return ForeverActivityResult.Done;

        ForeverActivityResult result = runner.Update(bars, facts, agenda.Progress);
        if (result.Status == ForeverActivityStatus.Failed)
            return result;
        if (agenda.Current is { } task &&
            (result.Status == ForeverActivityStatus.Done || task.Until.IsMet(agenda.Progress, bars)))
            Finish(task, bars, facts);
        return runner is null ? ForeverActivityResult.Done : ForeverActivityResult.Running;
    }

    public void Exit() => runner?.Exit();

    /// <summary>
    /// The task failed: it is given up and the agenda goes on with the next
    /// one, the next bigger goal (<see cref="ForeverAgenda.GiveUp"/>), whose
    /// runner starts on the next frame.
    /// </summary>
    public string FallBack(string reason)
    {
        runner?.Exit();
        runner = null;
        runnerTask = null;
        string failed = agenda.Current?.Name ?? "the task";
        agenda.GiveUp();
        return $"{failed} is given up; next: {agenda.Current?.Name ?? "nothing"}";
    }

    // Ends the current task, says what it did and starts the next one.
    private void Finish(ForeverTask task, VisualBarSample bars, in ForeverFacts facts)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever task done: {Task} ({Progress})", task.Name, agenda.Progress);
        int laps = agenda.Laps;
        agenda.Complete();
        if (agenda.Laps != laps && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever agenda lap {Lap} done; starting over", agenda.Laps);
        StartCurrent(bars, facts);
    }

    private void StartCurrent(VisualBarSample bars, in ForeverFacts facts)
    {
        runner?.Exit();
        runnerTask = agenda.Current;
        runner = runnerTask switch
        {
            null => null,
            ForeverHuntHereTask hunt => new ForeverHuntHere(logger, hunt, engage, navigator, combat, config),
            ForeverTravelTask travel => new ForeverTravel(logger, travel, engage, navigator, combat, config, maps),
            ForeverRoamAreaTask roam => new ForeverRoamArea(logger, roam, engage, navigator, combat, config, maps, memory),
            _ => throw new NotSupportedException($"Forever has no runner for the task '{runnerTask.Name}'"),
        };
        if (runner is null)
            return;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever task: {Task}", runnerTask!.Name);
        agenda.Progress.Start(GetTimestamp());
        runner.Enter(bars, facts);
    }
}
