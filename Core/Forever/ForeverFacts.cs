using System.Collections.Generic;

namespace Core.Forever;

/// <summary>
/// What the activities decide on, built once per frame: the player's combat
/// and danger, the fight in hand, the bodies waiting and the vitals. Every
/// activity reads the same facts for one frame; nothing here presses. It is
/// what upstream's GOAP world state is to its goals, from the visual channel.
/// </summary>
/// <param name="Now">The frame's time, a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</param>
/// <param name="InCombat">The player is in combat.</param>
/// <param name="InDanger">Something fights the player (<see cref="ForeverDanger"/>).</param>
/// <param name="TakingDamage">The player lost health within <see cref="ForeverDanger.DamageMs"/>.</param>
/// <param name="Engaged">A unit is chosen to click or locked to fight.</param>
/// <param name="BodiesPending">Kills not yet looted or given up.</param>
/// <param name="CanOpenFight">Health and mana are enough to open a fight out of combat.</param>
/// <param name="LastKillAt">When the last kill was verified, or 0.</param>
public readonly record struct ForeverFacts(
    long Now,
    bool InCombat,
    bool InDanger,
    bool TakingDamage,
    bool Engaged,
    int BodiesPending,
    bool CanOpenFight,
    long LastKillAt)
{
    public static ForeverFacts From(VisualBarSample bars, ForeverDanger danger, ForeverCorpses corpses,
        ForeverEngageActivity engage, long now) => new(
            now,
            bars.TargetFlags is { PlayerCombat: true },
            danger.InDanger(bars, now),
            danger.TakingDamage(now),
            engage.Engaged,
            corpses.Pending.Count,
            ForeverFrame.CanOpenFight(bars, inCombat: false),
            engage.LastKillAt);

    /// <summary>The facts that are set, for the log line of an activity switch.</summary>
    public string Describe()
    {
        List<string> parts = [];
        if (InCombat) parts.Add("in combat");
        if (InDanger) parts.Add("in danger");
        if (Engaged) parts.Add("engaged");
        if (BodiesPending > 0) parts.Add($"{BodiesPending} bodies");
        if (!CanOpenFight) parts.Add("too low to open a fight");
        return parts.Count == 0 ? "nothing pressing" : string.Join(", ", parts);
    }
}
