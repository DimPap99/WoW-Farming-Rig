using Core.Database;
using Core.Goals;

using Microsoft.Extensions.Logging;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

/// <summary>
/// How a press ended. <see cref="Queued"/>: a next-swing ability waits for
/// the swing. <see cref="NotTaken"/>: the game did not take a next-swing
/// press, as when the target is out of melee reach; nothing happened.
/// <see cref="Refused"/>: the game answered with an error message
/// (<see cref="ForeverCaster.LastRefusal"/>); nothing happened either.
/// </summary>
public enum ForeverCastResult { NotReady, Succeeded, Failed, Queued, NotTaken, Refused, Voided }

/// <summary>
/// Presses one guarded Forever action and waits for the visual cast-success
/// event that proves it, or the game's error message that refuses it. Only the
/// checked visual channel is read; the Classic aura, GCD, current-action,
/// cast-time and UI-error cells do not exist here.
/// </summary>
public sealed class ForeverCaster
{
    // Action and cooldown rows refresh every 500ms. A cast event can arrive
    // before that row reflects its GCD; wait for the producer to refresh.
    private const int RowRefreshMs = 650;
    // A target gone before its cast's success came gets this long for a late
    // success or death event; then the press is void.
    private const int TargetGoneMs = 500;
    private const int PollMs = 50;
    // A cast bar, or a self cast that may follow a cast bar, gets the longer wait.
    private const int LongCastTimeoutMs = 5000;
    private const int InstantCastTimeoutMs = 3000;
    // A next-swing ability (AfterCastWaitSwing) resolves with the swing, which
    // may be seconds away. Its press is proved once the slot shows it current,
    // and the success event then arrives with the swing on its own. A press
    // that leaves the slot as it was for this long was not taken; it is tried
    // again no sooner than the retry, as a person presses again.
    private const int SwingQueueMs = 1000;
    private const int NotTakenRetryMs = 1500;
    // A new error message after a press refuses it unless the success event
    // follows within this long: an unrelated message, such as a melee swing
    // or an Auto Shot out of reach, can arrive alongside a press that worked.
    private const int RefusalGraceMs = 500;

    private readonly ILogger<ForeverCaster> logger;
    private readonly ConfigurableInput input;
    private readonly ForeverVisualBarTracker tracker;
    private readonly SpellDB spells;
    private readonly ClassConfiguration config;

    // A missed event, unreadable frame or changed target after a press latches
    // casting closed for this session: a press whose outcome cannot be proved
    // from the visual event is never retried.
    private bool needsReset;
    private long lastSucceeded;
    private KeyAction? notTaken;
    private long notTakenAt;

    /// <summary>The error message of the last <see cref="ForeverCastResult.Refused"/> press.</summary>
    public VisualErrorEvent LastRefusal { get; private set; }

    public ForeverCaster(ILogger<ForeverCaster> logger, ConfigurableInput input,
        ForeverVisualBarTracker tracker, SpellDB spells, ClassConfiguration config)
    {
        this.logger = logger;
        this.input = input;
        this.tracker = tracker;
        this.spells = spells;
        this.config = config;
    }

    /// <param name="selfCast">The action affects the player, so a target change does not void it.</param>
    /// <param name="whileWaiting">Runs on each fresh frame while the cast is awaited, so the
    /// caller can keep doing what a person does meanwhile, such as facing the target.</param>
    public ForeverCastResult TryCast(KeyAction item, Func<bool> interrupt, bool selfCast = false,
        Action<VisualBarSample>? whileWaiting = null)
    {
        if (needsReset || interrupt())
            return ForeverCastResult.Failed;
        if (lastSucceeded != 0 &&
            GetElapsedTime(lastSucceeded).TotalMilliseconds < RowRefreshMs ||
            item == notTaken && GetElapsedTime(notTakenAt).TotalMilliseconds < NotTakenRetryMs)
            return ForeverCastResult.NotReady;
        if (!ForeverVerifiedSetup.IsProfile(config.FileName) ||
            !tracker.TryGetLatest(out VisualBarSample before) ||
            !ForeverCastGate.CanPress(before, item, spells, selfCast, config.NPCMaxLevels_Above) ||
            before.LastCastEvent is not { } previousEvent ||
            // Already pressed and waiting for its swing.
            item.AfterCastWaitSwing && before.Attack?.IsCurrent(item.Slot) == true)
            return ForeverCastResult.NotReady;

        needsReset = true;
        VisualTargetIdentity? targetIdentity = before.TargetIdentity;
        bool auraWasActive = before.SlotAuras?.IsActive(item.Slot) == true;
        VisualErrorEvent? errorBefore = before.LastError;
        Press(item);
        item.SetClicked();

        long start = GetTimestamp();
        long lastFrame = before.FrameId;
        long errorSeenAt = 0;
        long targetGoneAt = 0;
        bool nextSwing = item.AfterCastWaitSwing;
        // A next-swing press without a readable attack state keeps the long
        // wait for the swing's own event, and fails closed after it.
        int timeoutMs = selfCast || item.HasCastBar || nextSwing
            ? LongCastTimeoutMs : InstantCastTimeoutMs;
        while (GetElapsedTime(start).TotalMilliseconds < timeoutMs)
        {
            if (interrupt())
                return ForeverCastResult.Failed;
            if (!tracker.TryGetLatest(out VisualBarSample current) ||
                current.TargetFlags is not { } flags ||
                flags.Present != current.TargetPresent)
            {
                Thread.Sleep(PollMs);
                continue;
            }
            if (whileWaiting is not null && current.FrameId != lastFrame)
            {
                lastFrame = current.FrameId;
                whileWaiting(current);
            }
            if (flags.PlayerDead)
            {
                logger.LogWarning("Forever cast of {Spell} ended: the player died", item.Name);
                return ForeverCastResult.Failed;
            }

            if (current.LastCastEvent is { } castEvent &&
                castEvent.ConfirmsNewSuccess(previousEvent, item.SpellId) &&
                (selfCast ||
                 current.TargetIdentity == targetIdentity ||
                 targetIdentity is { } killed && current.LastDeathEvent is { } death &&
                 death.Confirms(killed, before.LastDeathEvent?.Sequence ?? 0)))
                return Succeeded(item);

            if (!selfCast &&
                flags.Present && current.TargetIdentity is { Present: true } currentTarget &&
                currentTarget != targetIdentity)
            {
                logger.LogWarning("Forever cast of {Spell} ended: target changed from {Before} to {Current}",
                    item.Name, targetIdentity, currentTarget);
                return ForeverCastResult.Failed;
            }

            // The target vanished before the success came: the press had
            // nothing to land on, which voids it, not the run; the fight
            // decides about the vanished target.
            if (!selfCast && targetIdentity is { Present: true } && !flags.Present)
            {
                if (targetGoneAt == 0)
                    targetGoneAt = GetTimestamp();
                else if (GetElapsedTime(targetGoneAt).TotalMilliseconds >= TargetGoneMs)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Forever press of {Spell} voided: the target vanished before it landed",
                            item.Name);
                    needsReset = false;
                    return ForeverCastResult.Voided;
                }
            }
            else
                targetGoneAt = 0;

            if (nextSwing && current.Attack is { } attack)
            {
                if (attack.IsCurrent(item.Slot))
                    return Succeeded(item, ForeverCastResult.Queued);
                if (GetElapsedTime(start).TotalMilliseconds >= SwingQueueMs)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Forever press of {Spell} did not queue for the next swing; " +
                            "trying again after {Retry} ms", item.Name, NotTakenRetryMs);
                    needsReset = false;
                    notTaken = item;
                    notTakenAt = GetTimestamp();
                    return ForeverCastResult.NotTaken;
                }
            }

            // A buff that newly shows on its slot's aura reference is direct
            // proof even when the successful spellcast event arrives after the frame.
            if (item.AfterCastAuraExpected && !auraWasActive &&
                current.SlotAuras?.IsActive(item.Slot) == true)
                return Succeeded(item);

            // The game answered with an error message and no success followed.
            if (errorBefore is { } quiet && current.LastError is { } error && error.IsNewSince(quiet))
            {
                if (errorSeenAt == 0)
                    errorSeenAt = GetTimestamp();
                else if (GetElapsedTime(errorSeenAt).TotalMilliseconds >= RefusalGraceMs)
                    return Refused(item, error);
            }

            Thread.Sleep(PollMs);
        }

        tracker.TryGetLatest(out VisualBarSample last);
        logger.LogWarning("Forever cast success event was not observed for {Spell} ({SpellId}) within {Timeout}ms; " +
            "event before {Before}, last {Last}; target before {TargetBefore}, last {TargetLast}",
            item.Name, item.SpellId, timeoutMs, previousEvent, last.LastCastEvent,
            targetIdentity, last.TargetIdentity);
        return ForeverCastResult.Failed;
    }

    // The action's key, as a person presses it; nothing is typed into chat
    // (the profile rules refuse a MacroText).
    private void Press(KeyAction item)
    {
        try { input.PressRandom(item.ConsoleKey, ForeverBinding.PressMs); }
        finally { input.SetKeyState(item.ConsoleKey, false, true); }
    }

    // Nothing happened, so casting stays open; the same action waits before
    // it is tried again, as a person presses again rather than mashing.
    private ForeverCastResult Refused(KeyAction item, VisualErrorEvent error)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Forever press of {Spell} refused by the game: {Error}; trying again after {Retry} ms",
                item.Name, error.Error, NotTakenRetryMs);
        needsReset = false;
        notTaken = item;
        notTakenAt = GetTimestamp();
        LastRefusal = error;
        return ForeverCastResult.Refused;
    }

    private ForeverCastResult Succeeded(KeyAction item,
        ForeverCastResult result = ForeverCastResult.Succeeded)
    {
        item.ConsumeCharge();
        needsReset = false;
        lastSucceeded = GetTimestamp();
        return result;
    }
}
