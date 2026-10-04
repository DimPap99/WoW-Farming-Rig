using System.Diagnostics;

namespace Core.Forever;

/// <summary>
/// Whether something fights the player: in combat and either losing health or
/// with a unit in view that has the player on its threat table or as its
/// target. Combat alone is not danger: it lingers after the last kill and can
/// hold a while with nothing attacking, when a person loots and waits rather
/// than looks for an attacker that is not there. Upstream's dangercombat reads
/// damage taken from the combat log; Forever reads the health bar.
/// </summary>
public sealed class ForeverDanger
{
    /// <summary>A fall of health this large is damage taken; smaller changes are bar noise.</summary>
    public const double DamageDrop = 0.01;
    /// <summary>Damage keeps the player in danger this long.</summary>
    public const int DamageMs = 3000;

    // The health a fall is measured from: it follows every rise and every
    // counted fall, so a slow bleed still adds up to a fall.
    private double? reference;
    private long damagedAt;

    /// <summary>Notes the player's health on this frame.</summary>
    /// <param name="now">The frame's time, as a <see cref="Stopwatch"/> timestamp.</param>
    public void Observe(VisualBarSample bars, long now)
    {
        double health = bars.PlayerHealth;
        if (reference is not { } from || health > from)
        {
            reference = health;
            return;
        }
        if (health < from - DamageDrop)
        {
            damagedAt = now;
            reference = health;
        }
    }

    /// <summary>The player lost health within the last <see cref="DamageMs"/>.</summary>
    public bool TakingDamage(long now) =>
        damagedAt != 0 && (now - damagedAt) * 1000.0 / Stopwatch.Frequency < DamageMs;

    /// <summary>The player lost health after <paramref name="since"/>, a <see cref="Stopwatch"/> timestamp.</summary>
    public bool DamagedSince(long since) => damagedAt != 0 && damagedAt > since;

    /// <summary>In combat, and losing health or with an attacker in view.</summary>
    public bool InDanger(VisualBarSample bars, long now) =>
        bars.TargetFlags is { PlayerCombat: true } && (TakingDamage(now) || AttackerInView(bars));

    /// <summary>A living unit in view fights the player, whether or not the run would choose it.</summary>
    public static bool AttackerInView(VisualBarSample bars)
    {
        if (bars.Nameplates is not { } roster)
            return false;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is { IsEmpty: false, Attacks: true, Dead: not true } plate &&
                !ForeverTargetPolicy.ReportedDead(bars, plate.GuidHash1, plate.GuidHash2))
                return true;
        }
        return false;
    }
}
