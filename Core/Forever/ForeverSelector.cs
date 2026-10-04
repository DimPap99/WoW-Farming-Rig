using Microsoft.Extensions.Logging;

using System.Linq;

namespace Core.Forever;

public enum ForeverActivityStatus { Running, Done, Failed }

/// <summary>How an activity's frame ended; <see cref="ForeverActivityStatus.Failed"/> stops the run with the reason.</summary>
public readonly record struct ForeverActivityResult(ForeverActivityStatus Status, string Reason = "")
{
    public static ForeverActivityResult Running { get; } = new(ForeverActivityStatus.Running);
    public static ForeverActivityResult Done { get; } = new(ForeverActivityStatus.Done);
    public static ForeverActivityResult Failed(string reason) => new(ForeverActivityStatus.Failed, reason);
}

/// <summary>
/// One thing the run can be doing: fighting a unit, looting, resting, the
/// agenda's task. Each owns a small <see cref="ForeverStateMachine{TState}"/>
/// for its own steps, with their time bounds, release hooks and allowances.
/// </summary>
public interface IForeverActivity
{
    string Name { get; }
    /// <summary>Upstream's goal cost: of the activities that want to run, the cheapest does.</summary>
    float Cost { get; }
    /// <summary>Upstream's preconditions and CanRun, read from this frame's facts.</summary>
    bool Wants(in ForeverFacts facts);
    /// <summary>Runs when the selector switches to this activity, before its first update.</summary>
    void Enter(VisualBarSample bars, in ForeverFacts facts);
    ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts);
    /// <summary>Runs when the selector switches away; releases whatever the activity holds.</summary>
    void Exit();
    /// <summary>What the activity's current step allows besides its work.</summary>
    ForeverAllowed Allowed { get; }
    /// <summary>The activity and its current step, for logs and the trial runner.</summary>
    string Status { get; }
}

/// <summary>
/// Picks what the run does on each frame, as upstream's GoapAgent picks a
/// goal: the cheapest activity that wants to run, re-checked on every frame.
/// Nobody writes "attacked while walking, fight": fighting simply costs less
/// than the task. A switch exits the old activity first, so its hooks let go
/// of keys and the mouse, then enters the new one.
/// </summary>
public sealed class ForeverSelector
{
    private readonly ILogger logger;
    private readonly IForeverActivity[] activities;

    public ForeverSelector(ILogger logger, params IForeverActivity[] activities)
    {
        this.logger = logger;
        this.activities = [.. activities.OrderBy(activity => activity.Cost)];
    }

    /// <summary>The activity that ran on the last frame.</summary>
    public IForeverActivity? Current { get; private set; }

    /// <summary>The cheapest activity that wants to run on <paramref name="facts"/>; it switches nothing.</summary>
    public IForeverActivity? Pick(in ForeverFacts facts)
    {
        foreach (IForeverActivity activity in activities)
        {
            if (activity.Wants(facts))
                return activity;
        }
        return null;
    }

    /// <summary>Switches to the activity <see cref="Pick"/> names, exiting the one before it.</summary>
    public IForeverActivity? Switch(VisualBarSample bars, in ForeverFacts facts)
    {
        IForeverActivity? next = Pick(facts);
        if (next == Current)
            return Current;
        Current?.Exit();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever activity: {From} -> {To} ({Facts})",
                Current?.Name ?? "none", next?.Name ?? "none", facts.Describe());
        Current = next;
        next?.Enter(bars, facts);
        return next;
    }
}
