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

public enum ForeverLootStep { Wait, Attacked, ClearTarget, Interact, Approach, Skin, Walk, Search, Collected, Done }

/// <summary>
/// What the round does on one frame: the step, the body it is about (its
/// identity hashes), the ledger's entry for it when it has one, and where to walk.
/// </summary>
public readonly record struct ForeverLootPlan(ForeverLootStep Step, (int Hash1, int Hash2) Body = default,
    ForeverCorpse? Corpse = null, Vector2 Point = default);

/// <summary>
/// Loots the bodies of a fight once it is over, as a person walks over to
/// each: nearest first, along the line the body fell on, until the client's
/// Interact Key finds it, then that key; Auto Loot takes the items and closes
/// the window. A body that reads lootable (<c>CanLootUnit</c>) is in reach
/// with loot on it and is pressed at once, as every earlier live run looted.
/// The key is trusted for a body of the fight that does not: <c>CanLootUnit</c>
/// reads false out of reach as well as with nothing on it, so the round steps
/// closer, until the body reads lootable or for a step, and presses. A press
/// that draws "too far away", or nothing at all, is tried again a step
/// closer, up to <see cref="MaxAttempts"/> presses. A body is left before
/// that only when the game says why: the bags are full (every body is left
/// then), it is not the player's to loot, or it has nothing on it and needs
/// more skill to skin. Any other body the key finds is taken while it shows
/// loot. With the profile's <c>"Skin": true</c> (upstream's flag) each body is
/// skinned once its loot is taken, where the game allows it. <see cref="Plan"/>
/// decides from a frame and keeps the round's place; <see cref="Update"/>
/// carries it out.
/// </summary>
public sealed class ForeverLoot : IForeverLootRoutine
{
    /// <summary>Presses on one body, each from a step closer than the one before, before it is given up.</summary>
    public const int MaxAttempts = 3;

    // One body gets this long to be found and looted.
    private const int CorpseMs = 30000;
    // A press gets this long to be answered: a window, or a message. On a
    // body with nothing to loot it starts the Skinning cast, whose window
    // opens after it.
    private const int AnswerMs = 3000;
    // Before the next press the round steps toward the body this long, the
    // soft target being in front.
    private const int ApproachMs = 600;
    private const float ApproachYards = 4;
    // An open window gets this long to empty and close.
    private const int WindowMs = 5000;
    private const int ClearTargetRetryMs = 1000;
    // A point on a body's line within this many yards is reached.
    private const float ReachedYards = 1.5f;
    // The line is estimated from a bearing and may cross ground the mesh
    // cannot reach exactly: the walk gets this close, and the Interact Key's
    // soft target finds the body from there.
    private const float BodySlackYards = 5;
    // After its loot, the soft target gets this long to settle on the emptied
    // body before the body counts as having nothing to skin.
    private const int SkinFindMs = 1500;
    // The Skinning cast and the window it opens get this long from the press.
    private const int SkinMs = 6000;
    // At the far end of a body's line, a person turns round looking for it:
    // the Interact Key's soft target takes a body only in front. This share
    // of full steering, one circle.
    private const double SearchRate = 0.35;

    private readonly ILogger<ForeverLoot> logger;
    private readonly bool skin;
    private readonly ForeverCorpses corpses;
    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly ConfigurableInput input;
    private readonly CancellationToken token;

    // The body walked to, whether its near point was passed, and whether the
    // look round at its far end is under way.
    private ForeverCorpse? walking;
    private bool pastNear;
    private bool searching;
    private readonly ForeverLookAround search = new();
    private long walkingSince;
    // The body the Interact Key was pressed on; the loot latch and the cast
    // event before the first press, and the error latch before the last.
    private (int Hash1, int Hash2)? interacting;
    private VisualLootState lootBefore;
    private VisualCastEvent castBefore;
    private VisualErrorEvent errorBefore;
    private int attempts;
    private long lastInteract;
    // The last press waits for its answer; once it is past, the next press
    // comes from a step closer (closer), the step under way until approachUntil.
    private bool awaiting;
    private bool closer;
    private long approachUntil;
    // The body stepped toward before its first press, as it did not read lootable.
    private (int Hash1, int Hash2)? steppedToward;
    private long windowOpenedAt;
    private long lastClearTarget;
    // The game said the bags have no room: every body is left this round.
    private bool bagsFull;
    // Bodies looted or given up this round; the key is not pressed on them again.
    private readonly HashSet<(int, int)> done = [];
    // The body being skinned, the loot latch before it, since when, and
    // whether the key was pressed on it.
    private (int Hash1, int Hash2)? skinning;
    private VisualLootState skinBefore;
    private long skinSince;
    private bool skinPressed;

    public ForeverLoot(ILogger<ForeverLoot> logger, ForeverCorpses corpses, ForeverTargetSelector selector,
        ForeverNavigator navigator, ConfigurableInput input, ClassConfiguration config,
        CancellationTokenSource<GoapAgent> cts)
    {
        this.logger = logger;
        skin = config.Skin;
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
        searching = false;
        interacting = null;
        attempts = 0;
        lastInteract = windowOpenedAt = lastClearTarget = 0;
        awaiting = closer = bagsFull = false;
        approachUntil = 0;
        steppedToward = null;
        skinning = null;
        done.Clear();
    }

    /// <summary>
    /// The bags that take any item have no free slot, as the bag grid reads;
    /// false while the grid is unreadable, which is no proof of room.
    /// </summary>
    public static bool BagsFull(VisualBarSample bars)
    {
        if (bars.Bags is not { } bags)
            return false;
        int general = 0, free = 0;
        for (int bag = 0; bag < VisualBagSnapshot.BagCount; bag++)
        {
            if (bags.GetBag(bag) is not { } header)
                return false;
            // A quiver or an ammo pouch holds only its own kind.
            if (header.SlotCount > 0 && header.Family == 0)
            {
                general++;
                free += header.FreeSlots;
            }
        }
        return general > 0 && free == 0;
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
            skinning = null;
            awaiting = closer = false;
            approachUntil = 0;
            return new(ForeverLootStep.Attacked);
        }
        if (interacting is { } pressed && Window(pressed, bars, loot) is { } window)
            return window;

        // No room in the bags: the one reason the round leaves the bodies of
        // the fight unlooted. Nothing would fit, the hides neither.
        if (bagsFull || BagsFull(bars))
        {
            if (corpses.Any)
                LeaveAll("the bags are full");
            return new(ForeverLootStep.Done);
        }

        // The Interact Key acts on a selected unit first; a selected corpse
        // or anything else is cleared, so it acts on the soft target.
        if (flags.Present)
            return GetElapsedTime(lastClearTarget).TotalMilliseconds >= ClearTargetRetryMs
                ? new(ForeverLootStep.ClearTarget) : new(ForeverLootStep.Wait);

        // A body just emptied is skinned before the round moves on.
        if (skinning is { } hide && Skin(hide, bars, loot) is { } skinStep)
            return skinStep;

        // The Interact Key found a body. One that reads lootable is in reach
        // with loot on it: pressed at once. One of this fight's that does not
        // may be out of reach, or empty and only to skin, which CanLootUnit
        // cannot tell apart: trust the key, from a step closer, as before
        // each later press. Another's body only while it shows loot.
        if (bars.SoftInteract is { Present: true, Dead: true } soft &&
            !done.Contains((soft.GuidHash1, soft.GuidHash2)) &&
            (soft.Lootable == true || Pending(soft.GuidHash1, soft.GuidHash2)))
        {
            (int, int) body = (soft.GuidHash1, soft.GuidHash2);
            if (interacting is { } other && other != body)
            {
                interacting = null;
                awaiting = closer = false;
                approachUntil = 0;
            }
            if (interacting is null && soft.Lootable != true && steppedToward != body)
            {
                steppedToward = body;
                closer = true;
            }
            if (closer)
                return new(ForeverLootStep.Approach, body);
            if (interacting is null || attempts < MaxAttempts)
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
            searching = false;
            walkingSince = GetTimestamp();
        }
        if (GetElapsedTime(walkingSince).TotalMilliseconds > CorpseMs)
        {
            GiveUp((next.GuidHash1, next.GuidHash2), "it was not found in time");
            return new(ForeverLootStep.Wait);
        }
        if (searching || Vector2.Distance(here, pastNear ? next.Far : next.Near) <= ReachedYards)
        {
            if (pastNear)
            {
                // At the far end, or as near it as the way goes: look round
                // for it there before giving it up.
                if (!searching)
                {
                    searching = true;
                    search.Reset();
                }
                if (!search.Turned(bars))
                    return new(ForeverLootStep.Search, (next.GuidHash1, next.GuidHash2), next);
                searching = false;
                GiveUp((next.GuidHash1, next.GuidHash2),
                    "it was not found along the line it fell on, nor by looking round at its end");
                return new(ForeverLootStep.Wait);
            }
            pastNear = true;
        }
        return new(ForeverLootStep.Walk, (next.GuidHash1, next.GuidHash2), next,
            pastNear ? next.Far : next.Near);
    }

    // A body of this fight still waiting to be looted.
    private bool Pending(int hash1, int hash2)
    {
        foreach (ForeverCorpse corpse in corpses.Pending)
        {
            if (corpse.Is(hash1, hash2))
                return true;
        }
        return false;
    }

    // The skinning of a body whose loot is taken begins.
    private void BeginSkinning((int Hash1, int Hash2) body, VisualLootState loot)
    {
        skinning = body;
        skinBefore = loot;
        skinSince = GetTimestamp();
        skinPressed = false;
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
                    castBefore = bars.LastCastEvent ?? default;
                    attempts = 0;
                    windowOpenedAt = 0;
                }
                errorBefore = bars.LastError ?? default;
                input.PressRandom(interact, token);
                attempts++;
                awaiting = true;
                lastInteract = GetTimestamp();
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Approach:
                long now = GetTimestamp();
                if (approachUntil == 0)
                    approachUntil = now + (long)(ApproachMs * (Frequency / 1000.0));
                // Close enough once the body shows its loot, else after the step.
                if (now >= approachUntil || bars.SoftInteract is { Lootable: true } shows &&
                        (shows.GuidHash1, shows.GuidHash2) == plan.Body)
                {
                    navigator.StopPursuit();
                    approachUntil = 0;
                    closer = false;
                    return ForeverLootResult.Waiting;
                }
                if (bars.WorldPosition is { } at && bars.Position is { } map &&
                    navigator.WalkToward(bars, ForeverNavigator.Along(new((float)at.X, (float)at.Y), map.Direction,
                        ApproachYards), out string approachReason, BodySlackYards) == ForeverNavigationResult.Failed)
                {
                    reason = approachReason;
                    return ForeverLootResult.Failed;
                }
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Skin:
                navigator.StopPursuit();
                if (!ForeverBindings.InteractTarget.TryGetKeyAction(bars, "Forever Skin Corpse",
                        out KeyAction skinKey))
                {
                    reason = "corpse interaction binding changed";
                    return ForeverLootResult.Failed;
                }
                input.PressRandom(skinKey, token);
                skinPressed = true;
                skinSince = GetTimestamp();
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Search:
                if (navigator.TurnInPlace(bars, SearchRate, out string turnReason) == ForeverNavigationResult.Failed)
                {
                    reason = turnReason;
                    return ForeverLootResult.Failed;
                }
                return ForeverLootResult.Waiting;
            case ForeverLootStep.Walk:
                switch (navigator.WalkToward(bars, plan.Point, out string walkReason, BodySlackYards))
                {
                    case ForeverNavigationResult.Blocked or ForeverNavigationResult.Stuck:
                        // No way to this point: try the line's far point, then
                        // look round from as near it as the way went.
                        navigator.StopPursuit();
                        if (pastNear && !searching)
                        {
                            searching = true;
                            search.Reset();
                        }
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
            default:
                return ForeverLootResult.Waiting;
        }
    }

    // After a press on a body: the window opens, Auto Loot empties it, and it
    // closes. A message answers instead: "too far away" sends the round a
    // step closer for the next press, and full bags, a body that is not the
    // player's or a skill too low leave it. A press nothing answers is tried
    // again a step closer, up to MaxAttempts presses. Null once a step closer
    // is due before the next press, or the round may move on.
    private ForeverLootPlan? Window((int Hash1, int Hash2) body, VisualBarSample bars, VisualLootState loot)
    {
        if (loot.OpenSequence != lootBefore.OpenSequence)
        {
            if (loot.IsOpen)
            {
                if (windowOpenedAt == 0)
                    windowOpenedAt = GetTimestamp();
                if (GetElapsedTime(windowOpenedAt).TotalMilliseconds <= WindowMs)
                    return new(ForeverLootStep.Wait, body);
                // Auto Loot left items in it, as when the bags are full; the
                // window closes once the round walks away.
                interacting = null;
                if (BagsFull(bars) || bars.LastError is { Error: VisualGameError.InventoryFull } full &&
                        full.IsNewSince(errorBefore))
                    bagsFull = true;
                GiveUp(body, $"its window stayed open with {loot.RemainingSlots} items Auto Loot did not take");
                return new(ForeverLootStep.Wait, body);
            }
            if (loot.CloseSequence == lootBefore.CloseSequence)
                return new(ForeverLootStep.Wait, body);
            interacting = null;
            done.Add(body);
            corpses.Remove(body.Hash1, body.Hash2);
            if (!loot.CompletedSince(lootBefore))
            {
                // Items left on it: the round goes on, and the body cannot be skinned.
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever loot: a body's window closed with {Left} items left",
                        loot.RemainingSlots);
                return new(ForeverLootStep.Collected, body);
            }
            // A cast came between the press and the window: the press skinned
            // a body with nothing to loot, and that window held the hide.
            if (bars.LastCastEvent is { } cast && cast.Sequence != castBefore.Sequence)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Forever skinned a body");
            }
            else if (skin)
                BeginSkinning(body, loot);
            return new(ForeverLootStep.Collected, body);
        }
        // A step closer is under way: the plan clears the target and steps.
        if (!awaiting)
            return null;
        if (bars.LastError is { } error && error.IsNewSince(errorBefore))
        {
            errorBefore = error;
            switch (error.Error)
            {
                case VisualGameError.TooFarAway or VisualGameError.OutOfRange:
                    return Retry(body, "it was too far away");
                case VisualGameError.InventoryFull:
                    interacting = null;
                    bagsFull = true;
                    return new(ForeverLootStep.Wait, body);
                case VisualGameError.LootRefused:
                    interacting = null;
                    GiveUp(body, "the game says it is not the player's to loot");
                    return new(ForeverLootStep.Wait, body);
                case VisualGameError.SkillTooLow:
                    interacting = null;
                    GiveUp(body, "it has nothing to loot, and skinning it needs more skill");
                    return new(ForeverLootStep.Wait, body);
            }
        }
        // Nothing yet: a body with nothing to loot may be in its Skinning cast.
        if (GetElapsedTime(lastInteract).TotalMilliseconds < AnswerMs)
            return new(ForeverLootStep.Wait, body);
        return Retry(body, "nothing answered the press");
    }

    // The last press is spent: the next comes from a step closer, until
    // MaxAttempts presses gave the body up. Null while another press is due.
    private ForeverLootPlan? Retry((int Hash1, int Hash2) body, string why)
    {
        awaiting = false;
        if (attempts >= MaxAttempts)
        {
            interacting = null;
            GiveUp(body, $"{why} after {attempts} presses, each a step closer");
            return new(ForeverLootStep.Wait, body);
        }
        closer = true;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever loot: {Why}; a step closer for press {Next} of {Max}",
                why, attempts + 1, MaxAttempts);
        return null;
    }

    // The skinning of a body just looted: the Interact Key again once the
    // soft target is the emptied body, the client's Skinning cast, and the
    // window it opens, which Auto Loot empties. The game decides whether the
    // body can be skinned and the skill suffices: a body the soft target does
    // not settle on has nothing to skin, and a press that opens no window in
    // time leaves it as it is. Null once the skinning is over.
    private ForeverLootPlan? Skin((int Hash1, int Hash2) body, VisualBarSample bars, VisualLootState loot)
    {
        double elapsed = GetElapsedTime(skinSince).TotalMilliseconds;
        if (loot.OpenSequence != skinBefore.OpenSequence)
        {
            if (loot.IsOpen || loot.CloseSequence == skinBefore.CloseSequence)
                return elapsed <= SkinMs + WindowMs
                    ? new(ForeverLootStep.Wait, body)
                    : LeaveHide("its window stayed open");
            skinning = null;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Forever skinned a body");
            return null;
        }
        if (!skinPressed)
        {
            if (bars.SoftInteract is { Dead: true } soft &&
                soft.GuidHash1 == body.Hash1 && soft.GuidHash2 == body.Hash2)
                return new(ForeverLootStep.Skin, body);
            return elapsed < SkinFindMs
                ? new(ForeverLootStep.Wait, body)
                : LeaveHide("the Interact Key does not find it, so there is nothing to skin");
        }
        return elapsed < SkinMs
            ? new(ForeverLootStep.Wait, body)
            : LeaveHide("the press started no skinning; it may need more skill");
    }

    private ForeverLootPlan? LeaveHide(string why)
    {
        skinning = null;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever left a body unskinned: {Why}", why);
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

    // Every body still waiting is left.
    private void LeaveAll(string why)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever loot: {Why}; leaving {Count} bodies", why, corpses.Pending.Count);
        corpses.Clear();
        walking = null;
        skinning = null;
    }
}
