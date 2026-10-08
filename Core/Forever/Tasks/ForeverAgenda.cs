using SharedLib.Forever;

using System;
using System.Collections.Generic;

namespace Core.Forever;

/// <summary>What the current task has done so far.</summary>
public sealed class ForeverTaskProgress
{
    public int Kills { get; set; }

    /// <summary>When the task's runner first started, a <see cref="System.Diagnostics.Stopwatch"/> timestamp; 0 before.</summary>
    public long StartedAt { get; private set; }

    /// <summary>Marks the task begun at <paramref name="now"/>, once.</summary>
    public void Start(long now)
    {
        if (StartedAt == 0)
            StartedAt = now;
    }

    public override string ToString() => StartedAt == 0
        ? $"{Kills} kills"
        : $"{Kills} kills in {System.Diagnostics.Stopwatch.GetElapsedTime(StartedAt).TotalMinutes:0.#} minutes";
}

/// <summary>
/// The run's tasks in order. The current one runs whenever nothing more
/// urgent does, and the next begins once it is done. Kills count toward the
/// task that was current when they were verified. Code adds tasks with
/// <see cref="Add"/>; the run finishes when the last task is done, or, for an
/// agenda that repeats, starts over with the first, as a farming loop does.
/// A task that fails is given up and the next one starts (<see cref="GiveUp"/>).
/// </summary>
public sealed class ForeverAgenda
{
    /// <summary>
    /// How long the run hunts where it stands before it tries again the last
    /// task of an agenda that it gave up.
    /// </summary>
    public const double FallbackMinutes = 3;

    private readonly List<ForeverTask> tasks;
    private readonly bool repeat;
    private int index;
    private bool ended;

    public ForeverAgenda(IEnumerable<ForeverTask> tasks, bool repeat = false)
    {
        this.tasks = [.. tasks];
        this.repeat = repeat;
    }

    public IReadOnlyList<ForeverTask> Tasks => tasks;

    public ForeverTask? Current => !ended && index < tasks.Count ? tasks[index] : null;

    public ForeverTaskProgress Progress { get; private set; } = new();

    public bool Finished => Current is null;

    /// <summary>Times a repeating agenda has started over.</summary>
    public int Laps { get; private set; }

    /// <summary>Tasks done, and tasks given up, in this run.</summary>
    public int Completed { get; private set; }
    public int GivenUp { get; private set; }

    /// <summary>Appends <paramref name="task"/> after the tasks already planned.</summary>
    public void Add(ForeverTask task) => tasks.Add(task);

    /// <summary>The current task is done; the next one starts with fresh progress.</summary>
    public void Complete()
    {
        Completed++;
        Advance();
    }

    /// <summary>
    /// The current task failed and is given up: the next one starts, the next
    /// bigger goal, as a person who cannot get to one place goes on to the
    /// next. When it was the last task of an agenda that does not repeat,
    /// nothing is left above it but itself: the run hunts where it stands for
    /// <see cref="FallbackMinutes"/>, then tries it again, and is never left
    /// with nothing to do.
    /// </summary>
    public void GiveUp()
    {
        if (Current is not { } failed)
            return;
        GivenUp++;
        if (!repeat && index == tasks.Count - 1)
        {
            tasks.Add(ForeverTasks.HuntHere(ForeverUntil.AfterMinutes(FallbackMinutes)));
            tasks.Add(failed);
        }
        Advance();
    }

    /// <summary>Ends the current task; the next one starts with fresh progress.</summary>
    public void Advance()
    {
        if (index < tasks.Count)
            index++;
        if (repeat && !ended && index == tasks.Count && tasks.Count > 0)
        {
            index = 0;
            Laps++;
        }
        Progress = new();
    }

    /// <summary>Ends the agenda now, a repeating one too: nothing is current from here on.</summary>
    public void End()
    {
        ended = true;
        Progress = new();
    }

    /// <summary>
    /// Completes the tasks already done, as <paramref name="isDone"/> says of the current one, once
    /// round the agenda at most, telling <paramref name="passing"/> of each first. A repeating agenda
    /// whose every task is done ends instead of going round again, which finds the same: a level the
    /// character has is met again at once (known issue 69). True when it ended so.
    /// </summary>
    public bool PassDone(Func<ForeverTask, bool> isDone, Action<ForeverTask, ForeverTaskProgress> passing)
    {
        int passed = 0;
        while (Current is { } task && isDone(task))
        {
            if (passed++ == tasks.Count)
            {
                End();
                return true;
            }

            passing(task, Progress);
            Complete();
        }

        return false;
    }

    public void CountKill() => Progress.Kills++;

    /// <summary>Which task runs and how far it got, for the trial runner.</summary>
    public string Describe() => Current is not { } task
        ? "agenda done"
        : $"task {index + 1} of {tasks.Count}{(Laps > 0 ? $", lap {Laps + 1}" : string.Empty)}" +
          $"{(GivenUp > 0 ? $", {GivenUp} given up" : string.Empty)}: {task.Name} ({Progress})";

    /// <summary>
    /// The run's tasks when the options name them. Otherwise what the bounded
    /// trials ask for: hunt here until the given number of kills, or until a
    /// full look-around finds nothing, or, with neither, until the search
    /// runs out of time.
    /// </summary>
    public static ForeverAgenda For(ForeverRunOptions options) => options.Tasks.Count > 0
        ? new(options.Tasks, options.RepeatTasks)
        : new([ForeverTasks.HuntHere(new ForeverUntil(options.MaxCompletedCycles, options.StopWhenNoTargets))]);
}
