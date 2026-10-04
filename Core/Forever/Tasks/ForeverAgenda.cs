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
/// </summary>
public sealed class ForeverAgenda
{
    private readonly List<ForeverTask> tasks;
    private readonly bool repeat;
    private int index;

    public ForeverAgenda(IEnumerable<ForeverTask> tasks, bool repeat = false)
    {
        this.tasks = [.. tasks];
        this.repeat = repeat;
    }

    public IReadOnlyList<ForeverTask> Tasks => tasks;

    public ForeverTask? Current => index < tasks.Count ? tasks[index] : null;

    public ForeverTaskProgress Progress { get; private set; } = new();

    public bool Finished => Current is null;

    /// <summary>Times a repeating agenda has started over.</summary>
    public int Laps { get; private set; }

    /// <summary>Appends <paramref name="task"/> after the tasks already planned.</summary>
    public void Add(ForeverTask task) => tasks.Add(task);

    /// <summary>Ends the current task; the next one starts with fresh progress.</summary>
    public void Advance()
    {
        if (index < tasks.Count)
            index++;
        if (repeat && index == tasks.Count && tasks.Count > 0)
        {
            index = 0;
            Laps++;
        }
        Progress = new();
    }

    public void CountKill() => Progress.Kills++;

    /// <summary>Which task runs and how far it got, for the trial runner.</summary>
    public string Describe() => Current is not { } task
        ? "agenda done"
        : $"task {index + 1} of {tasks.Count}{(Laps > 0 ? $", lap {Laps + 1}" : string.Empty)}: {task.Name} ({Progress})";

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
