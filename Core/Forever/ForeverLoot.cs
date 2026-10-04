using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;

using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// How one frame of a loot round ended. <see cref="Collected"/>: a body's
/// loot was taken and the round goes on. <see cref="Attacked"/>: something
/// fights the player; the bodies stay pending for the next round.
/// </summary>
public enum ForeverLootResult { Waiting, Collected, Done, Attacked, Failed }

public interface IForeverLootRoutine
{
    /// <summary>Starts a round over every body <see cref="ForeverCorpses"/> holds.</summary>
    void Begin();
    /// <param name="inDanger">Something fights the player (<see cref="ForeverDanger"/>);
    /// combat that merely lingers does not stop the round.</param>
    ForeverLootResult Update(VisualBarSample bars, bool inDanger, out string reason);
}

public enum ForeverLootStep { Wait, Attacked, ClearTarget, Interact, Walk, Collected, Done, Failed }

/// <summary>
/// What the round does on one frame: the step, the body it is about (its
/// identity hashes), the ledger's entry for it when it has one, and where to walk.
/// </summary>
public readonly record struct ForeverLootPlan(ForeverLootStep Step, (int Hash1, int Hash2) Body = default,
    ForeverCorpse? Corpse = null, Vector2 Point = default, string Reason = "");

/// <summary>
/// Loots the bodies of a fight once it is over, as a person walks over to
/// each: nearest first, along the line the body fell on, until the client's
/// Interact Key finds it (the soft-interact unit is dead and lootable), then
/// that key; Auto Loot takes the items and closes the window. Any lootable
/// body the key finds on the way is taken too. <see cref="Plan"/> decides from
/// a frame and keeps the round's place; <see cref="Update"/> carries it out.
/// </summary>
public sealed class ForeverLoot : IForeverLootRoutine
{
    // One body gets this long to be found and looted.
    private const int CorpseMs = 30000;
    // A press the loot window has not answered within this long is retried,
    // at most once.
    private const int InteractRetryMs = 1200;
    private const int MaxInteracts = 2;
    // An open window gets this long to empty and close.
    private const int WindowMs = 5000;
    private const int ClearTargetRetryMs = 1000;
    // A point on a body's line within this many yards is reached.
    private const float ReachedYards = 1.5f;

    private readonly ILogger<ForeverLoot> logger;
    private readonly ForeverCorpses corpses;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly ConfigurableInput input;
    private readonly CancellationToken token;

    // The body walked to, and whether its near point was passed.
    private ForeverCorpse? walking;
    private bool pastNear;
    private long walkingSince;
    // The body the Interact Key was pressed on, and the loot latch before the first press.
    private (int Hash1, int Hash2)? interacting;
    private VisualLootState lootBefore;
    private int interacts;
    private long lastInteract;
    private long windowOpenedAt;
    private long lastClearTarget;
    // Bodies looted or given up this round; the key is not pressed on them again.
    private readonly HashSet<(int, int)> done = [];

    public ForeverLoot(ILogger<ForeverLoot> logger, ForeverCorpses corpses, ForeverTargetSelector selector,
        ForeverNavigator navigator, ConfigurableInput input, CancellationTokenSource<GoapAgent> cts)
    {
        this.logger = logger;
        this.corpses = corpses;
        this.selector = selector;
        this.navigator = navigator;
        this.input = input;
        token = cts.Token;
    }

    public void Begin()
    {
        walking = null;
        pastNear = false;
        interacting = null;
        interacts = 0;
        lastInteract = windowOpenedAt = lastClearTarget = 0;
        done.Clear();
    }

    /// <summary>The round's next step on <paramref name="bars"/>; it presses nothing.</summary>
    /// <param name="inDanger">Something fights the player; lingering combat alone goes on looting.</param>
    public ForeverLootPlan Plan(VisualBarSample bars, bool inDanger)
    {
        if (bars.TargetFlags is not { } flags || bars.Loot is not { } loot ||
            bars.WorldPosition is not { } world)
            return new(ForeverLootStep.Wait);
        if (inDanger)
        {
            interacting = null;
            return new(ForeverLootStep.Attacked);
        }
        if (interacting is { } pressed && Window(pressed, loot) is { } window)
            return window;

        // The Interact Key acts on a selected unit first; a selected corpse
        // or anything else is cleared, so it acts on the soft target.
        if (flags.Present)
            return GetElapsedTime(lastClearTarget).TotalMilliseconds >= ClearTargetRetryMs
                ? new(ForeverLootStep.ClearTarget) : new(ForeverLootStep.Wait);

        // The client found a body in reach: loot it, whichever of ours it is.
        if (bars.SoftInteract is { IsLootableCorpse: true } soft &&
            !done.Contains((soft.GuidHash1, soft.GuidHash2)))
        {
            (int, int) body = (soft.GuidHash1, soft.GuidHash2);
            if (interacting is { } other && other != body)
                interacting = null;
            if (interacting is null || interacts < MaxInteracts)
                return new(ForeverLootStep.Interact, body);
        }

        // Otherwise walk the line of the nearest body still pending.
        Vector2 here = new((float)world.X, (float)world.Y);
        if (corpses.Nearest(here) is not { } next)
            return new(ForeverLootStep.Done);
        if (next != walking)
        {
            walking = next;
            pastNear = false;
            walkingSince = GetTimestamp();
        }
        if (GetElapsedTime(walkingSince).TotalMilliseconds > CorpseMs)
        {
            GiveUp((next.GuidHash1, next.GuidHash2), "it was not found in time");
            return new(ForeverLootStep.Wait);
        }
        if (Vector2.Distance(here, pastNear ? next.Far : next.Near) <= ReachedYards)
        {
            if (pastNear)
            {
                GiveUp((next.GuidHash1, next.GuidHash2), "it was not found along the line it fell on");
                return new(ForeverLootStep.Wait);
            }
            pastNear = true;
        }
        return new(ForeverLootStep.Walk, (next.GuidHash1, next.GuidHash2), next,
            pastNear ? next.Far : next.Near);
    }

    public ForeverLootResult Update(VisualBarSample bars, bool inDanger, out string reason)
    {
        reason = string.Empty;
        ForeverLootPlan plan = Plan(bars, inDanger);
        switch (plan.Step)
        {
            case ForeverLootStep.Attacked:
                navigator.StopPursuit();
                return ForeverLootResult.Attacked;
            case ForeverLootStep.ClearTarget:
                navigator.StopPursuit();
                selector.ClearTarget(bars);
                lastClearTarget = GetTimestamp();
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Interact:
                navigator.StopPursuit();
                if (!ForeverBindings.InteractTarget.TryGetKeyAction(bars, "Forever Loot Corpse",
                        out KeyAction interact))
                {
                    reason = "corpse interaction binding changed";
                    return ForeverLootResult.Failed;
                }
                if (interacting != plan.Body)
                {
                    interacting = plan.Body;
                    lootBefore = bars.Loot!.Value;
                    interacts = 0;
                    windowOpenedAt = 0;
                }
                input.PressRandom(interact, token);
                interacts++;
                lastInteract = GetTimestamp();
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Walk:
                switch (navigator.WalkToward(bars, plan.Point, out string walkReason))
                {
                    case ForeverNavigationResult.Blocked or ForeverNavigationResult.Stuck:
                        // No way to this point: try the line's far point, then give up.
                        navigator.StopPursuit();
                        if (pastNear)
                            GiveUp(plan.Body, walkReason);
                        pastNear = true;
                        return ForeverLootResult.Waiting;
                    case ForeverNavigationResult.Failed:
                        reason = walkReason;
                        return ForeverLootResult.Failed;
                }
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Collected:
                return ForeverLootResult.Collected;
            case ForeverLootStep.Done:
                navigator.StopPursuit();
                return ForeverLootResult.Done;
            case ForeverLootStep.Failed:
                navigator.StopPursuit();
                reason = plan.Reason;
                return ForeverLootResult.Failed;
            default:
                return ForeverLootResult.Waiting;
        }
    }

    // After a press on a body: the window opens, Auto Loot empties it, and it
    // closes. Null once the press went unanswered and may be repeated, or the
    // round may move on.
    private ForeverLootPlan? Window((int Hash1, int Hash2) body, VisualLootState loot)
    {
        if (loot.OpenSequence != lootBefore.OpenSequence)
        {
            if (loot.IsOpen)
            {
                if (windowOpenedAt == 0)
                    windowOpenedAt = GetTimestamp();
                return GetElapsedTime(windowOpenedAt).TotalMilliseconds > WindowMs
                    ? new(ForeverLootStep.Failed, body,
                        Reason: "the loot window stayed open; Auto Loot did not take the items")
                    : new(ForeverLootStep.Wait, body);
            }
            if (loot.CloseSequence == lootBefore.CloseSequence)
                return new(ForeverLootStep.Wait, body);
            interacting = null;
            if (!loot.CompletedSince(lootBefore))
                return new(ForeverLootStep.Failed, body,
                    Reason: $"loot window closed with {loot.RemainingSlots} uncollected slots");
            done.Add(body);
            corpses.Remove(body.Hash1, body.Hash2);
            return new(ForeverLootStep.Collected, body);
        }
        if (GetElapsedTime(lastInteract).TotalMilliseconds < InteractRetryMs)
            return new(ForeverLootStep.Wait, body);
        if (interacts >= MaxInteracts)
        {
            interacting = null;
            GiveUp(body, "its loot window did not open");
            return new(ForeverLootStep.Wait, body);
        }
        return null;
    }

    private void GiveUp((int Hash1, int Hash2) body, string why)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever loot gave up a body: {Why}", why);
        done.Add(body);
        corpses.Remove(body.Hash1, body.Hash2);
        if (walking is { } corpse && corpse.Is(body.Hash1, body.Hash2))
            walking = null;
    }
}
