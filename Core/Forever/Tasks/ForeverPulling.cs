using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Core.Forever;

/// <summary>
/// What a pull did on one frame: nothing to take, the best unit offered to
/// the fight, turning to face it first, or a failure that stops the run.
/// </summary>
public enum ForeverPull { Nothing, Offered, Turning, Failed }

/// <summary>
/// How a task takes a unit in view out of combat: an attack the client
/// reports unusable for a reason other than mana stops the run first, as
/// before every pull; otherwise the best pull (<see cref="ForeverPullOptions"/>)
/// is offered to <see cref="ForeverEngageActivity"/>, and the options weighed
/// are logged. A best unit at the side of the screen, where a click would not
/// reach its plate, is turned to first, as a person turns to face a mob
/// before clicking it; the click follows once its marker is in the middle.
/// </summary>
public sealed class ForeverPulling
{
    private readonly ILogger logger;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    // The unit last turned to, so the turn is said once.
    private (int Slot, int Incarnation) turningTo;

    public ForeverPulling(ILogger logger, IForeverCombatRoutine combat, ClassConfiguration config,
        ForeverEngageActivity engage, ForeverNavigator navigator)
    {
        this.logger = logger;
        this.combat = combat;
        this.config = config;
        this.engage = engage;
        this.navigator = navigator;
    }

    /// <summary>
    /// One frame of taking a unit in view. <see cref="ForeverPull.Offered"/>
    /// and <see cref="ForeverPull.Turning"/> own the frame's input, so the
    /// task does not walk or look round meanwhile; <see cref="ForeverPull.Failed"/>
    /// stops the run with <paramref name="reason"/>.
    /// </summary>
    /// <param name="where">Takes only units whose place it accepts, as a
    /// travel takes only what stands on its way; null takes any.</param>
    public ForeverPull TryPull(VisualBarSample bars, out string reason, Func<Vector2, bool>? where = null)
    {
        if (!combat.TryAttacksUsable(bars, out reason))
            return ForeverPull.Failed;
        List<ForeverPullOption> options = ForeverPullOptions.Of(bars, config, combat, engage.Exclusions);
        if (where is not null)
            options.RemoveAll(option => !where(option.At));
        if (!ForeverPullOptions.TryBest(options, out ForeverPullOption best))
            return ForeverPull.Nothing;
        if (!best.Clickable)
        {
            if (turningTo != (best.Plate.Slot, best.Plate.Incarnation))
            {
                turningTo = (best.Plate.Slot, best.Plate.Incarnation);
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever pull: turning to face {Best} first", best);
            }
            return navigator.TurnToward(bars, best.Plate.Screen!.Value, out reason) == ForeverNavigationResult.Failed
                ? ForeverPull.Failed
                : ForeverPull.Turning;
        }
        if (logger.IsEnabled(LogLevel.Information))
        {
            IEnumerable<ForeverPullOption> others = options.Where(option => option.Plate.Slot != best.Plate.Slot);
            logger.LogInformation("Forever pull: {Best}{Others}", best,
                options.Count > 1 ? "; passed over " + string.Join("; ", others) : "");
        }
        engage.Offer(best.Plate);
        return ForeverPull.Offered;
    }
}
