using Core.Database;
using Core.Goals;

using Microsoft.Extensions.Logging;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverCastResult { NotReady, Succeeded, Failed }

/// <summary>
/// Presses one guarded Forever action and waits for the visual cast-success
/// event that proves it. Only the checked visual channel is read; the Classic
/// aura, GCD, current-action, cast-time and UI-error cells do not exist here.
/// </summary>
public sealed class ForeverCaster
{
    // Action and cooldown rows refresh every 500ms. A cast event can arrive
    // before that row reflects its GCD; wait for the producer to refresh.
    private const int RowRefreshMs = 650;
    private const int PressMs = 75;
    private const int PollMs = 50;
    // A cast bar, a next-swing ability (AfterCastWaitSwing: its success
    // event arrives with the swing), or a self cast that may follow a cast
    // bar gets the longer wait.
    private const int LongCastTimeoutMs = 5000;
    private const int InstantCastTimeoutMs = 3000;

    private readonly ILogger<ForeverCaster> logger;
    private readonly ConfigurableInput input;
    private readonly ExecGameCommand command;
    private readonly ForeverVisualBarTracker tracker;
    private readonly SpellDB spells;
    private readonly ClassConfiguration config;

    // A missed event, unreadable frame or changed target after a press latches
    // casting closed for this session: a press whose outcome cannot be proved
    // from the visual event is never retried.
    private bool needsReset;
    private long lastSucceeded;

    public ForeverCaster(ILogger<ForeverCaster> logger, ConfigurableInput input,
        ExecGameCommand command, ForeverVisualBarTracker tracker,
        SpellDB spells, ClassConfiguration config)
    {
        this.logger = logger;
        this.input = input;
        this.command = command;
        this.tracker = tracker;
        this.spells = spells;
        this.config = config;
    }

    /// <param name="selfCast">The action affects the player, so a target change does not void it.</param>
    public ForeverCastResult TryCast(KeyAction item, Func<bool> interrupt, bool selfCast = false)
    {
        if (needsReset || interrupt())
            return ForeverCastResult.Failed;
        if (lastSucceeded != 0 &&
            GetElapsedTime(lastSucceeded).TotalMilliseconds < RowRefreshMs)
            return ForeverCastResult.NotReady;
        if (!ForeverVerifiedSetup.IsProfile(config.FileName) ||
            !tracker.TryGetLatest(out VisualBarSample before) ||
            !ForeverCastGate.CanPress(before, item, spells, selfCast) ||
            before.LastCastEvent is not { } previousEvent)
            return ForeverCastResult.NotReady;

        needsReset = true;
        VisualTargetIdentity? targetIdentity = before.TargetIdentity;
        bool auraWasActive = before.SlotAuras?.IsActive(item.Slot) == true;
        Press(item);
        item.SetClicked();

        long start = GetTimestamp();
        int timeoutMs = selfCast || item.HasCastBar || item.AfterCastWaitSwing
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

            // A buff that newly shows on its slot's aura reference is direct
            // proof even when the successful spellcast event arrives after the frame.
            if (item.AfterCastAuraExpected && !auraWasActive &&
                current.SlotAuras?.IsActive(item.Slot) == true)
                return Succeeded(item);

            Thread.Sleep(PollMs);
        }

        tracker.TryGetLatest(out VisualBarSample last);
        logger.LogWarning("Forever cast success event was not observed for {Spell} ({SpellId}) within {Timeout}ms; " +
            "event before {Before}, last {Last}; target before {TargetBefore}, last {TargetLast}",
            item.Name, item.SpellId, timeoutMs, previousEvent, last.LastCastEvent,
            targetIdentity, last.TargetIdentity);
        return ForeverCastResult.Failed;
    }

    private void Press(KeyAction item)
    {
        // As upstream, a profile macro is typed as a command instead of a key
        // press; this is how a profile targets the player explicitly.
        if (!string.IsNullOrEmpty(item.MacroText))
        {
            // Macro() substitutes profile variables once RequirementFactory set it up.
            string text = item.Macro();
            command.Run(string.IsNullOrEmpty(text) ? item.MacroText : text);
            return;
        }

        try { input.PressFixed(item.ConsoleKey, PressMs, CancellationToken.None); }
        finally { input.SetKeyState(item.ConsoleKey, false, true); }
    }

    private ForeverCastResult Succeeded(KeyAction item)
    {
        item.ConsumeCharge();
        needsReset = false;
        lastSucceeded = GetTimestamp();
        return ForeverCastResult.Succeeded;
    }
}
