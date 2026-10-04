namespace Core.Forever;

/// <summary>
/// Which units the guarded runtime may fight, and which attacker it takes
/// next in combat; out of combat <see cref="ForeverPullOptions"/> weighs the
/// pulls. Decisions over one frame only; <see cref="ForeverTargetSelector"/>
/// makes the game target the choice.
/// </summary>
public static class ForeverTargetPolicy
{
    // The part of the screen where a plate's marker can be clicked, as shares
    // of the screen, Y up from the bottom: off the edges, where a marker clips
    // and slips out of view as the camera moves, and clear of the default
    // interface, whose frames take a click meant for a plate under them (the
    // minimap and the objective tracker at the right, the unit frames, chat
    // and action bars at the bottom). A person turns toward a mob there first.
    public const double ClickableLeft = 0.08;
    public const double ClickableRight = 0.8;
    public const double ClickableBottom = 0.33;
    public const double ClickableTop = 0.94;

    /// <summary>The marker lies where a click reaches its plate.</summary>
    public static bool Clickable(VisualScreenPointSample marker) =>
        InClickableColumn(marker) && InClickableRow(marker);

    /// <summary>
    /// The marker lies between the clickable bottom and top, whatever its side:
    /// above the interface at the bottom, below the top edge. Turning brings a
    /// marker beside them into the middle.
    /// </summary>
    public static bool InClickableRow(VisualScreenPointSample marker) =>
        marker.Y is >= ClickableBottom and <= ClickableTop;

    /// <summary>
    /// The marker lies between the clickable sides, whatever its height:
    /// turning moves a marker across the screen, never up or down.
    /// </summary>
    public static bool InClickableColumn(VisualScreenPointSample marker) =>
        marker.X is >= ClickableLeft and <= ClickableRight;

    /// <summary>
    /// An attackable, living non-player unit that no one else has tapped,
    /// within the profile's level window
    /// (<see cref="ClassConfiguration.NPCMaxLevels_Above"/> and
    /// <see cref="ClassConfiguration.NPCMaxLevels_Below"/>). An unknown flag or
    /// level is not eligible; an unknown tap is, since only plates report it.
    /// </summary>
    public static bool IsEligible(int level, bool? attackable, bool? dead, bool? isPlayer,
        bool? tapDenied, VisualPlayerIdentity player, ClassConfiguration config) =>
        attackable == true && dead == false && isPlayer == false && tapDenied != true &&
        level >= 1 && level <= player.Level + config.NPCMaxLevels_Above &&
        level >= player.Level - config.NPCMaxLevels_Below;

    /// <summary>
    /// A unit that fights the player or the pet is fought whatever its level,
    /// kind or tap, as a person fights back against whatever attacks them: it
    /// only has to be attackable, alive and not a player.
    /// </summary>
    public static bool IsDefendable(bool? attackable, bool? dead, bool? isPlayer) =>
        attackable == true && dead == false && isPlayer == false;

    /// <summary>
    /// The same for a plate's unit, which must also be of a kind the
    /// profile's <see cref="ClassConfiguration.TargetMask"/> allows (normal,
    /// trivial and rare by default, as upstream). An unknown classification
    /// passes. A unit that fights the player or the pet only has to be
    /// <see cref="IsDefendable"/>.
    /// </summary>
    public static bool IsEligible(VisualNameplate plate, VisualPlayerIdentity player,
        ClassConfiguration config) =>
        !plate.IsEmpty &&
        (plate.Attacks
            ? IsDefendable(plate.Attackable, plate.Dead, plate.IsPlayer)
            : IsEligible(plate.Level, plate.Attackable, plate.Dead, plate.IsPlayer, plate.TapDenied,
                player, config) &&
              (plate.Classification is not { } kind || config.TargetMask.HasFlagF(kind)));

    /// <summary>
    /// The selected unit fights the player or the pet, as its own plate tells.
    /// The target always holds a roster slot while its plate is drawn.
    /// </summary>
    public static bool TargetAttacks(VisualBarSample bars) =>
        bars.Nameplates?.Target is { Attacks: true };

    /// <summary>
    /// The selected unit, when its checked flags and identity make it
    /// eligible, or <see cref="IsDefendable"/> while it fights the player or
    /// the pet, and the death latch has not reported it dead.
    /// </summary>
    public static bool TryEligibleTarget(VisualBarSample bars, ClassConfiguration config,
        out VisualTargetIdentity identity) =>
        TryEligibleTarget(bars, config, out identity, out _);

    /// <inheritdoc cref="TryEligibleTarget(VisualBarSample, ClassConfiguration, out VisualTargetIdentity)"/>
    /// <param name="why">Which check refused it, for the log; empty when eligible.</param>
    public static bool TryEligibleTarget(VisualBarSample bars, ClassConfiguration config,
        out VisualTargetIdentity identity, out string why)
    {
        identity = default;
        if (bars.TargetFlags is not { Present: true, PlayerDead: false } flags)
        {
            why = "no target";
            return false;
        }
        if (!ForeverFrame.HasTargetIdentity(bars) || bars.TargetIdentity is not { Present: true } target)
        {
            why = "the target's identity is unreadable";
            return false;
        }
        if (bars.PlayerIdentity is not { } player)
        {
            why = "the player's identity is unreadable";
            return false;
        }
        if (bars.TargetHealth <= 0)
        {
            why = "the target's health reads 0";
            return false;
        }
        if (ReportedDead(bars, target.GuidHash1, target.GuidHash2))
        {
            why = "the target was reported dead";
            return false;
        }
        bool fightable = TargetAttacks(bars)
            ? IsDefendable(flags.Attackable, flags.Dead, target.IsPlayer)
            : IsEligible(target.Level, flags.Attackable, flags.Dead, target.IsPlayer, tapDenied: null, player, config);
        if (!fightable)
        {
            why = $"level {target.Level}, attackable {flags.Attackable}, dead {flags.Dead}, player {target.IsPlayer}: " +
                $"not a unit the profile takes (levels {player.Level - config.NPCMaxLevels_Below} to " +
                $"{player.Level + config.NPCMaxLevels_Above})";
            return false;
        }
        why = string.Empty;
        identity = target;
        return true;
    }

    /// <summary>
    /// The death latch reports this unit dead. It can arrive a frame before
    /// the target's flags or plate say so, and a dead unit never lives again,
    /// so the unit is never taken once reported.
    /// </summary>
    public static bool ReportedDead(VisualBarSample bars, int hash1, int hash2) =>
        (hash1 != 0 || hash2 != 0) &&
        bars.LastDeathEvent is { Sequence: > 0 } death && death.GuidHash1 == hash1 && death.GuidHash2 == hash2;

    /// <summary>
    /// In combat, the selected unit does not fight the player while another
    /// eligible unit does: the target's row says it has the player neither
    /// on its threat table nor as its target, and another plate attacks. A
    /// person drops the mob they were walking to and turns on what hits them,
    /// as upstream's approach does. An unreadable threat state is no proof.
    /// </summary>
    public static bool AttackedByAnother(VisualBarSample bars, ClassConfiguration config)
    {
        if (bars.TargetFlags is not { PlayerCombat: true } ||
            bars.Nameplates is not { } roster || bars.PlayerIdentity is not { } player ||
            roster.Target is not { OnThreatTable: false, TargetsPlayer: not true, TargetsPet: not true })
            return false;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is { IsTarget: not true, Attacks: true } plate &&
                IsEligible(plate, player, config) &&
                !ReportedDead(bars, plate.GuidHash1, plate.GuidHash2))
                return true;
        }
        return false;
    }

    /// <summary>
    /// In combat, the attacker to take next: a unit that fights the player or
    /// the pet, set aside earlier or not, with its marker between the
    /// clickable sides (<see cref="InClickableColumn"/>). Preferred, in order:
    /// one where a click surely reaches it (<see cref="Clickable"/>), the
    /// current target (no click needed), a unit the camera can see, one an
    /// opener reaches, one a Combat spell reaches, then the lowest on screen,
    /// which on open ground is the nearest.
    /// </summary>
    public static bool TryChooseAttacker(VisualBarSample bars, ClassConfiguration config,
        IForeverCombatRoutine combat, out VisualNameplate candidate)
    {
        candidate = default;
        if (bars.Nameplates is not { } roster || bars.PlayerIdentity is not { } player ||
            bars.TargetFlags is not { PlayerCombat: true })
            return false;

        bool found = false;
        int bestRank = -1;
        double bestHeight = double.MaxValue;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is not { Attacks: true, Screen: { } screen } plate ||
                !InClickableColumn(screen) ||
                !IsEligible(plate, player, config) ||
                ReportedDead(bars, plate.GuidHash1, plate.GuidHash2))
                continue;

            ForeverReach reach = combat.Reach(bars, ForeverSlotRanges.Of(plate));
            int rank = (Clickable(screen) ? 16 : 0) + (plate.IsTarget == true ? 8 : 0) +
                (plate.Occluded != true ? 4 : 0) + (reach.Opener ? 2 : 0) + (reach.Combat ? 1 : 0);
            if (rank > bestRank || rank == bestRank && screen.Y < bestHeight)
            {
                bestRank = rank;
                bestHeight = screen.Y;
                candidate = plate;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// A unit fights the player or the pet but its marker cannot be clicked
    /// from here: none shows, as for one behind the camera that went for the
    /// pet, or it lies beside the clickable sides. The attacker with an
    /// identity, so where it was last seen can be looked up, or any.
    /// </summary>
    public static bool TryAttackerOutOfView(VisualBarSample bars, ClassConfiguration config,
        out VisualNameplate attacker)
    {
        attacker = default;
        if (bars.Nameplates is not { } roster || bars.PlayerIdentity is not { } player)
            return false;
        bool found = false;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is not { Attacks: true } plate ||
                plate.Screen is { } shown && InClickableColumn(shown) ||
                !IsEligible(plate, player, config) ||
                ReportedDead(bars, plate.GuidHash1, plate.GuidHash2))
                continue;
            if (!found || !attacker.HasIdentity && plate.HasIdentity)
                attacker = plate;
            found = true;
        }
        return found;
    }
}
