using Microsoft.Extensions.Logging;

using static System.Diagnostics.Stopwatch;

namespace Core.Forever;

public enum ForeverThreatState { Idle, Watch, Failed }

/// <summary>
/// In combat with nothing engaged: find what fights the player. A selected
/// unit that fights in reach is adopted as it is; otherwise an attacking
/// plate is clicked. While combat only lingers, a person stands and waits
/// for it to drop; only while taking damage do they turn round to find the
/// attacker out of view. Upstream's FindThreatGoal does this job there.
/// </summary>
public sealed class ForeverThreatActivity : IForeverActivity
{
    /// <summary>
    /// After the fight in hand and after looting, which wants to run only out
    /// of danger, so in danger the bodies wait and this comes first.
    /// </summary>
    public const float Priority = 4.6f;

    private const int WatchMs = 45000;
    // Combat lingers a moment after the last kill; with no attacker in view
    // the character stands this long before looking for one out of view.
    private const int CombatLingersMs = 3000;
    // Hit by something out of view, a person turns round briskly to find it.
    private const double CombatLookAroundRate = 0.5;

    private readonly ForeverEngageActivity engage;
    private readonly ForeverNavigator navigator;
    private readonly IForeverCombatRoutine combat;
    private readonly ClassConfiguration config;
    private readonly ForeverStateMachine<ForeverThreatState> machine;
    private readonly ForeverLookAround lookAround = new();
    private ForeverFacts facts;

    public ForeverThreatActivity(ILogger<ForeverThreatActivity> logger, ForeverEngageActivity engage,
        ForeverNavigator navigator, IForeverCombatRoutine combat, ClassConfiguration config)
    {
        this.engage = engage;
        this.navigator = navigator;
        this.combat = combat;
        this.config = config;
        machine = new ForeverStateMachine<ForeverThreatState>(logger, "Forever threat",
                ForeverThreatState.Idle, ForeverThreatState.Failed)
            .On(ForeverThreatState.Idle, exits: [ForeverThreatState.Watch])
            .On(ForeverThreatState.Watch, Watch, exits: [ForeverThreatState.Idle],
                timeoutMs: WatchMs, onTimeout: "no safe target found within acquisition time",
                enter: lookAround.Reset, exit: navigator.StopPursuit)
            .On(ForeverThreatState.Failed, enter: navigator.StopPursuit);
    }

    public string Name => "Threat";
    public float Cost => Priority;
    public ForeverAllowed Allowed => machine.Allowed;
    public string Status => Name;
    public ForeverThreatState State => machine.State;
    public string LastFailure => machine.LastFailure;

    /// <summary>What <paramref name="state"/> allows besides its work, as declared.</summary>
    public ForeverAllowed AllowedIn(ForeverThreatState state) => machine.AllowedIn(state);

    public bool Wants(in ForeverFacts facts) => facts.InCombat;

    public void Enter(VisualBarSample bars, in ForeverFacts facts)
    {
        if (machine.State == ForeverThreatState.Idle)
            machine.Go(ForeverThreatState.Watch);
    }

    public ForeverActivityResult Update(VisualBarSample bars, in ForeverFacts facts)
    {
        this.facts = facts;
        machine.Tick(bars);
        return machine.State == ForeverThreatState.Failed
            ? ForeverActivityResult.Failed(machine.LastFailure)
            : ForeverActivityResult.Running;
    }

    public void Exit()
    {
        navigator.StopPursuit();
        if (machine.State == ForeverThreatState.Watch)
            machine.Go(ForeverThreatState.Idle);
    }

    private void Watch(VisualBarSample bars)
    {
        // Aggro can begin before our first cast: a selected unit in reach is
        // fought as it is, without a click, unless something else attacks
        // while it does not.
        if (ForeverTargetPolicy.TryEligibleTarget(bars, config, out VisualTargetIdentity selected) &&
            combat.InRange(bars, selected) && !ForeverTargetPolicy.AttackedByAnother(bars, config))
        {
            engage.Adopt(bars, selected);
            return;
        }
        // In combat the policy offers only plates that fight the player.
        if (ForeverTargetPolicy.TryChoose(bars, config, combat, engage.Exclusions, out VisualNameplate chosen))
        {
            engage.Offer(chosen);
            return;
        }
        if (facts.LastKillAt != 0 && GetElapsedTime(facts.LastKillAt).TotalMilliseconds < CombatLingersMs)
        {
            navigator.StopPursuit();
            return;
        }
        // Combat lingers but nothing hits the player: a person waits for it to
        // drop rather than spin round for an attacker that is not there.
        if (!facts.TakingDamage)
        {
            navigator.StopPursuit();
            lookAround.Reset();
            return;
        }
        if (!lookAround.Turned(bars))
        {
            if (navigator.TurnInPlace(bars, CombatLookAroundRate, out string turnReason) ==
                    ForeverNavigationResult.Failed)
                machine.Fail(turnReason);
            return;
        }
        // Something fights the player out of view: keep looking for it.
        lookAround.Reset();
    }
}
