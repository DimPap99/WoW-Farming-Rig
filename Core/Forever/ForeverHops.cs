using System;
using System.Diagnostics;

namespace Core.Forever;

/// <summary>
/// When a walking character hops; the navigator presses the key, and only
/// what the automaton's current state allows (<see cref="ForeverAllowed"/>).
/// A stall in held movement gets one hop, as a person hops when snagged on
/// something the mesh does not know. Idle hops: every few seconds of walking a
/// roll may start one to three hops in a row, each as the character lands
/// from the last, as a person hops now and then for no reason. Decides only,
/// from the times it is given.
/// </summary>
public sealed class ForeverHops(Random random)
{
    /// <summary>Held movement that made no progress this long hops once.</summary>
    public const double SnagAfterSeconds = 1.5;
    /// <summary>While walking, the chance of idle hops is rolled this often.</summary>
    public const int IdleRollMs = 4000;
    /// <summary>The chance that a roll starts idle hops: about one roll in four.</summary>
    public const double IdleChance = 0.25;
    /// <summary>The most idle hops in a row.</summary>
    public const int MaxIdleHops = 3;
    /// <summary>A jump lasts about this long, so the next hop of a row comes as the character lands.</summary>
    public const int MinHopGapMs = 650;
    public const int MaxHopGapMs = 900;

    private bool walking;
    private bool snagHopped;
    private int idleLeft;
    // The next roll, or the next hop of a row.
    private long idleDueAt;

    /// <summary>Held movement began; the first roll comes a whole interval later.</summary>
    public void Started(long now)
    {
        walking = true;
        snagHopped = false;
        idleLeft = 0;
        idleDueAt = now + Ticks(IdleRollMs);
    }

    /// <summary>Held movement ended, and a row of hops with it.</summary>
    public void Stopped()
    {
        walking = false;
        idleLeft = 0;
    }

    /// <summary>The character moved, so the next stall gets its own hop.</summary>
    public void Progressed() => snagHopped = false;

    /// <summary>One hop for a stall that has lasted <see cref="SnagAfterSeconds"/>.</summary>
    public bool Snag(double stalledSeconds)
    {
        if (snagHopped || stalledSeconds <= SnagAfterSeconds)
            return false;
        snagHopped = true;
        return true;
    }

    /// <summary>An idle hop is due at <paramref name="now"/>, a <see cref="Stopwatch"/> timestamp.</summary>
    public bool Idle(long now)
    {
        if (!walking || now < idleDueAt)
            return false;
        if (idleLeft == 0)
        {
            // Most rolls come to nothing.
            if (random.NextDouble() >= IdleChance)
            {
                idleDueAt = now + Ticks(IdleRollMs);
                return false;
            }
            idleLeft = random.Next(1, MaxIdleHops + 1);
        }
        idleLeft--;
        idleDueAt = now + Ticks(idleLeft > 0 ? random.Next(MinHopGapMs, MaxHopGapMs + 1) : IdleRollMs);
        return true;
    }

    private static long Ticks(int ms) => ms * Stopwatch.Frequency / 1000;
}
