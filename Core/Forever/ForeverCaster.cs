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
    private const int SelfCastTimeoutMs = 5000;
    private const int TargetCastTimeoutMs = 3000;

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

    public ForeverCastResult TryCast(KeyAction item, Func<bool> interrupt)
    {
        if (needsReset || interrupt())
            return ForeverCastResult.Failed;
        if (lastSucceeded != 0 &&
            GetElapsedTime(lastSucceeded).TotalMilliseconds < RowRefreshMs)
            return ForeverCastResult.NotReady;
        if (!ForeverPaladin.IsProfile(config.FileName) ||
            !tracker.TryGetLatest(out VisualBarSample before) ||
            !ForeverCastGate.CanPress(before, item, spells) ||
            before.LastCastEvent is not { } previousEvent)
            return ForeverCastResult.NotReady;

        needsReset = true;
        VisualTargetIdentity? targetIdentity = before.TargetIdentity;
        bool selfCast = ForeverPaladin.IsSelfCast(item.SpellId);
        Press(item);
        item.SetClicked();

        long start = GetTimestamp();
        int timeoutMs = selfCast ? SelfCastTimeoutMs : TargetCastTimeoutMs;
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
                return ForeverCastResult.Failed;

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
                return ForeverCastResult.Failed;

            // The checked player aura is direct proof of this self buff even
            // when the successful spellcast event arrives after the frame.
            if (item.SpellId == ForeverPaladin.SealOfRighteousness &&
                current.SealOfRighteousnessActive == true)
                return Succeeded(item);

            Thread.Sleep(PollMs);
        }

        logger.LogWarning("Forever cast success event was not observed for {Spell} ({SpellId})",
            item.Name, item.SpellId);
        return ForeverCastResult.Failed;
    }

    private void Press(KeyAction item)
    {
        if (item.SpellId == ForeverPaladin.HolyLight)
        {
            // Explicit self target works even with a hostile target selected,
            // independently of the client's auto-self-cast preference.
            command.Run("/cast [@player] Holy Light(Rank 1)");
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
