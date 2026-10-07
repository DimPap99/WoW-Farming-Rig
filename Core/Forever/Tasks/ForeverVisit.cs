using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Forever;
using SharedLib.Quests;

using System;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// Carries out <see cref="ForeverVisitTask"/>: when the task begins, finds the place it names - the
/// nearest of a role's NPCs who serve the side the character's race is on, an NPC by name, where a
/// quest is given or handed in - from where the character stands (<see cref="ForeverDestinations"/>),
/// then travels there as <see cref="ForeverTravel"/> does, and is done on arrival. The place is not
/// looked for again: a fight or a rest on the way resumes the same travel.
/// </summary>
public sealed class ForeverVisit : IForeverTaskRunner
{
    private readonly ILogger logger;
    private readonly ForeverVisitTask task;
    private readonly ForeverWorldData world;
    private readonly WorldMapAreaDB maps;
    private readonly Func<ForeverTravelTask, ForeverTravel> travelTo;
    private ForeverTravel? travel;
    private ForeverFound found;
    private string? failure;

    public ForeverVisit(ILogger logger, ForeverVisitTask task, ForeverWorldData world, WorldMapAreaDB maps,
        Func<ForeverTravelTask, ForeverTravel> travelTo)
    {
        this.logger = logger;
        this.task = task;
        this.world = world;
        this.maps = maps;
        this.travelTo = travelTo;
    }

    public string Status => travel is null ? task.Name : $"{task.Name}: {found.Name}/{travel.Status}";
    public ForeverAllowed Allowed => travel?.Allowed ?? ForeverAllowed.None;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (failure is not null)
            return;
        if (travel is null)
        {
            if (!TryResolve(task, bars, world, maps, out ForeverTravelTask to, out found, out string reason))
            {
                failure = reason;
                return;
            }
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever visit: {Target} is {Found}; travelling to {Point}", task.Target, found, to.To);
            travel = travelTo(to);
        }
        travel.Enter(bars, facts);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts, ForeverTaskProgress progress) =>
        failure is not null ? ForeverActivityResult.Failed(failure) : travel!.Update(bars, facts, progress);

    public void Exit() => travel?.Exit();

    /// <summary>
    /// The travel to what <paramref name="task"/> visits, from where the character stands: a role's
    /// NPC must serve the side its race is on (a class trainer, with no title given, is of its class).
    /// The run's start-up check asks the same, from where the run begins.
    /// </summary>
    public static bool TryResolve(ForeverVisitTask task, VisualBarSample bars, ForeverWorldData world, WorldMapAreaDB maps,
        out ForeverTravelTask travel, out ForeverFound found, out string reason)
    {
        travel = null!;
        found = default;
        if (bars.WorldPosition is not { } here)
        {
            reason = "the player's world position is unreadable";
            return false;
        }
        ForeverVisitTarget target = task.Target;
        QuestSide side = QuestSide.Both;
        if (target.Kind == ForeverVisitKind.Role)
        {
            if (bars.PlayerIdentity is not { } player)
            {
                reason = "the player's race is unreadable, so the side its NPCs must serve is not known";
                return false;
            }
            side = QuestSides.OfRace(player.RaceId);
            if (target.Title is null && target.Name == nameof(SharedLib.Data.NpcFlags.ClassTrainer).ToLowerInvariant())
                target = target with { Title = $"{(UnitClass)player.ClassId} Trainer" };
        }
        if (!world.TryGetBook(out QuestBook book, out reason))
            return false;
        return ForeverDestinations.TryTravel(book, maps, task with { Target = target }, side, here.InstanceMapId,
            new Vector2((float)here.X, (float)here.Y), out travel, out found, out reason);
    }
}
