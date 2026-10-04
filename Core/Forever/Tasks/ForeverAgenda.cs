using System.Collections.Generic;

namespace Core.Forever;

/// <summary>What the current task has done so far.</summary>
public sealed class ForeverTaskProgress
{
    public int Kills { get; set; }

    public override string ToString() => $"{Kills} kills";
}

/// <summary>
/// The run's tasks in order. The current one runs whenever nothing more
/// urgent does, and the next begins once it is done. Kills count toward the
/// task that was current when they were verified. Code adds tasks with
/// <see cref="Add"/>; the run finishes when the last task is done.
/// </summary>
public sealed class ForeverAgenda
{
    private readonly List<ForeverTask> tasks;
    private int index;

    public ForeverAgenda(IEnumerable<ForeverTask> tasks)
    {
        this.tasks = [.. tasks];
    }

    public ForeverTask? Current => index < tasks.Count ? tasks[index] : null;

    public ForeverTaskProgress Progress { get; private set; } = new();

    public bool Finished => Current is null;

    /// <summary>Appends <paramref name="task"/> after the tasks already planned.</summary>
    public void Add(ForeverTask task) => tasks.Add(task);

    /// <summary>Ends the current task; the next one starts with fresh progress.</summary>
    public void Advance()
    {
        if (index < tasks.Count)
            index++;
        Progress = new();
    }

    public void CountKill() => Progress.Kills++;

    /// <summary>
    /// The agenda the bounded trials ask for: hunt here until the given
    /// number of kills, or until a full look-around finds nothing, or,
    /// with neither, until the search runs out of time.
    /// </summary>
    public static ForeverAgenda For(ForeverRunOptions options) =>
        new([ForeverTasks.HuntHere(new ForeverUntil(options.MaxCompletedCycles, options.StopWhenNoTargets))]);
}
