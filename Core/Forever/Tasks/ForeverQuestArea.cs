using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Forever;
using SharedLib.Quests;

using System;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// Carries out <see cref="ForeverQuestAreaTask"/>: when the task begins, finds the camp nearest the
/// character of what the quest asks to kill or gather (<see cref="ForeverDestinations.TryCamp"/>),
/// then roams it as <see cref="ForeverRoamArea"/> does until the task's end holds. The camp is not
/// looked for again: a fight or a rest resumes the same roam.
/// </summary>
public sealed class ForeverQuestArea : IForeverTaskRunner
{
    private readonly ILogger logger;
    private readonly ForeverQuestAreaTask task;
    private readonly ForeverWorldData world;
    private readonly WorldMapAreaDB maps;
    private readonly Func<ForeverRoamAreaTask, ForeverRoamArea> roamIn;
    private ForeverRoamArea? roam;
    private ForeverCamp? camp;
    private string? failure;

    public ForeverQuestArea(ILogger logger, ForeverQuestAreaTask task, ForeverWorldData world, WorldMapAreaDB maps,
        Func<ForeverRoamAreaTask, ForeverRoamArea> roamIn)
    {
        this.logger = logger;
        this.task = task;
        this.world = world;
        this.maps = maps;
        this.roamIn = roamIn;
    }

    public string Status => roam is null ? task.Name : $"{task.Name}: {roam.Status}";
    public ForeverAllowed Allowed => roam?.Allowed ?? ForeverAllowed.None;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (failure is not null)
            return;
        if (roam is null)
        {
            if (!TryResolve(task, bars, world, maps, out ForeverRoamAreaTask area, out camp, out string reason))
            {
                failure = reason;
                return;
            }
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever quest {Quest}: roaming the camp of {Camp}, the {Area}", task.QuestId, camp, area.Area);
            roam = roamIn(area);
        }
        roam.Enter(bars, facts);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress progress) =>
        failure is not null ? ForeverActivityResult.Failed(failure) : roam!.Update(bars, facts, progress);

    public void Exit() => roam?.Exit();

    /// <summary>
    /// The roam of the camp <paramref name="task"/>'s quest sends the character to, from where it
    /// stands. The run's start-up check asks the same, from where the run begins.
    /// </summary>
    public static bool TryResolve(ForeverQuestAreaTask task, VisualBarSample bars, ForeverWorldData world, WorldMapAreaDB maps,
        out ForeverRoamAreaTask roam, out ForeverCamp camp, out string reason)
    {
        roam = null!;
        camp = null!;
        if (bars.WorldPosition is not { } here)
        {
            reason = "the player's world position is unreadable";
            return false;
        }
        if (!world.TryGetBook(out QuestBook book, out reason))
            return false;
        return ForeverDestinations.TryRoam(book, maps, task, here.InstanceMapId, new Vector2((float)here.X, (float)here.Y),
            out roam, out camp, out reason);
    }
}
