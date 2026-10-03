using Core.GOAP;

using SharedLib;

using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverLootResult { Waiting, Collected, Empty, Failed }

public interface IForeverLootRoutine
{
    void Begin(VisualTargetIdentity corpse, VisualLootState before);
    ForeverLootResult Update(VisualBarSample bars, out string reason);
}

/// <summary>Restores the killed target, interacts once, and confirms collection from loot events.</summary>
public sealed class ForeverLoot : IForeverLootRoutine
{
    private readonly ForeverTargetSelector selector;
    private readonly ConfigurableInput input;
    private readonly ExecGameCommand command;
    private readonly CancellationToken token;
    private VisualTargetIdentity corpse;
    private VisualLootState before;
    private long started, lastInput;
    private bool restored, collected;
    private int interactions;

    public ForeverLoot(ForeverTargetSelector selector, ConfigurableInput input,
        ExecGameCommand command, CancellationTokenSource<GoapAgent> cts)
    {
        this.selector = selector;
        this.input = input;
        this.command = command;
        token = cts.Token;
    }

    public void Begin(VisualTargetIdentity corpse, VisualLootState before)
    {
        this.corpse = corpse;
        this.before = before;
        started = GetTimestamp();
        lastInput = 0;
        restored = collected = false;
        interactions = 0;
    }

    public ForeverLootResult Update(VisualBarSample bars, out string reason)
    {
        reason = string.Empty;
        if (GetElapsedTime(started).TotalSeconds > 15)
        {
            reason = "corpse loot did not complete within 15 seconds";
            return ForeverLootResult.Failed;
        }
        if (token.IsCancellationRequested || bars.Loot is not { } loot ||
            bars.TargetFlags is not { } flags || flags.PlayerCombat)
            return ForeverLootResult.Waiting;

        if (loot.OpenSequence != before.OpenSequence)
        {
            if (!loot.IsOpen && loot.CloseSequence != before.CloseSequence)
            {
                if (loot.CompletedSince(before))
                    return loot.InitialSlots == 0 ? ForeverLootResult.Empty : ForeverLootResult.Collected;
                reason = $"loot window closed with {loot.RemainingSlots} uncollected slots";
                return ForeverLootResult.Failed;
            }
            if (loot.IsOpen && !collected && ReadyForInput())
            {
                // Native Loot All, scoped to this open window; no CVar changes.
                command.Run("/run C_LootFrame.TryAutoLoot()");
                collected = true;
                lastInput = GetTimestamp();
            }
            return ForeverLootResult.Waiting;
        }

        if (!flags.Present)
        {
            if (!restored)
            {
                selector.RestoreLastTarget();
                restored = true;
                lastInput = GetTimestamp();
            }
            return ForeverLootResult.Waiting;
        }
        if (bars.TargetIdentity != corpse || !flags.Dead)
        {
            reason = "selected unit is not the verified corpse";
            return ForeverLootResult.Failed;
        }
        if (!ReadyForInput()) return ForeverLootResult.Waiting;
        if (loot.Matches(corpse) && loot.TargetLootable == false &&
            GetElapsedTime(started).TotalMilliseconds > 1000)
            return ForeverLootResult.Empty;

        if (interactions < 2)
        {
            if (!ForeverBindings.InteractTarget.TryGetKeyAction(bars, "Forever Loot Corpse",
                    out KeyAction interact))
            {
                reason = "corpse interaction binding changed";
                return ForeverLootResult.Failed;
            }
            input.PressRandom(interact, token);
            interactions++;
            lastInput = GetTimestamp();
        }
        return ForeverLootResult.Waiting;
    }

    private bool ReadyForInput() => lastInput == 0 ||
        GetElapsedTime(lastInput).TotalMilliseconds >= 1200;
}
