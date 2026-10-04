using Microsoft.Extensions.Logging;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverRestState { Idle, Rest, Failed }

/// <summary>
/// Out of combat, recovery before the next pull. After a fight (<see cref="Pending"/>)
/// the dead target is cleared and health and mana come back to
/// <see cref="ForeverFrame.IsRested"/>; otherwise the run only waits until it
/// has enough to open a fight. The profile's out-of-combat Adhoc heals run
/// meanwhile, before this activity on each frame. An attack in the meantime
/// is combat, where Threat takes over.
/// </summary>
public sealed class ForeverRestActivity : IForeverActivity
{
    /// <summary>After the fight, Threat and the loot round; before the agenda's task.</summary>
    public const float Priority = 5f;

    // Give the flags a moment to follow a clear-target press before another:
    // the fallback is Escape, which opens the game menu with nothing selected.
    private const int ClearTargetRetryMs = 1000;
    private const int DeadTargetMs = 30000;
    private const int RecoverMs = 45000;

    private readonly ForeverTargetSelector selector;
    private readonly ForeverNavigator navigator;
    private readonly ForeverStateMachine<ForeverRestState> machine;
    private long lastClearTarget;

    public ForeverRestActivity(ILogger<ForeverRestActivity> logger, ForeverTargetSelector selector,
        ForeverNavigator navigator)
    {
        this.selector = selector;
        this.navigator = navigator;
        machine = new ForeverStateMachine<ForeverRestState>(logger, "Forever rest",
                ForeverRestState.Idle, ForeverRestState.Failed)
            .On(ForeverRestState.Idle, exits: [ForeverRestState.Rest])
            .On(ForeverRestState.Rest, Rest, exits: [ForeverRestState.Idle])
            .On(ForeverRestState.Failed, enter: navigator.StopPursuit);
    }

    public string Name => "Rest";
    public float Cost => Priority;
    public ForeverAllowed Allowed => machine.Allowed;
    public string Status => Name;
    public ForeverRestState State => machine.State;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverRestState state) => machine.AllowedIn(state);

    /// <summary>
    /// A kill out of combat or a finished loot round: rest until rested
    /// before the next pull.
    /// </summary>
    public bool Pending { get; set; }

    public bool Wants(in ForeverFacts facts) => !facts.InCombat && (Pending || !facts.CanOpenFight);

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        navigator.StopPursuit();
        if (machine.State == ForeverRestState.Idle)
            machine.Go(ForeverRestState.Rest);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts)
    {
        machine.Tick(bars);
        return machine.State == ForeverRestState.Failed
            ? ForeverActivityResult.Failed(machine.LastFailure)
            : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        if (machine.State == ForeverRestState.Rest)
            machine.Go(ForeverRestState.Idle);
    }

    private void Rest(VisualBarSample bars)
    {
        // Short of what a pull needs, with no fight to recover from: stand
        // until there is enough.
        if (!Pending)
        {
            if (machine.ElapsedMs > RecoverMs)
                machine.Fail(bars.PlayerHealth < ForeverFrame.ReadyHealth
                    ? "health did not recover in time"
                    : "mana did not recover in time");
            return;
        }
        VisualTargetFlags flags = bars.TargetFlags!.Value;
        if (flags.Present)
        {
            if (!flags.Dead)
            {
                machine.Fail("new live target appeared during recovery");
                return;
            }
            // Any corpse is safe to deselect, including one whose identity did
            // not read back; repeat the command only after the flags had time.
            if (GetElapsedTime(lastClearTarget).TotalMilliseconds >= ClearTargetRetryMs)
            {
                selector.ClearTarget(bars);
                lastClearTarget = GetTimestamp();
            }
            if (machine.ElapsedMs > DeadTargetMs)
                machine.Fail("dead target stayed selected during recovery");
            return;
        }
        if (!ForeverFrame.IsRested(bars))
        {
            if (machine.ElapsedMs > RecoverMs)
                machine.Fail(bars.PlayerHealth < ForeverFrame.RestedHealth
                    ? "health did not recover in time"
                    : "mana did not recover in time");
            return;
        }
        Pending = false;
        machine.Go(ForeverRestState.Idle);
    }
}
