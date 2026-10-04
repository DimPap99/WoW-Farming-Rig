namespace Core.Forever;

/// <summary>
/// Which units the guarded runtime may fight, and which visible nameplate it
/// takes next. Decisions over one frame only; <see cref="ForeverTargetSelector"/>
/// makes the game target the choice.
/// </summary>
public static class ForeverTargetPolicy
{
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
    /// The same for a plate's unit, which must also be of a kind the
    /// profile's <see cref="ClassConfiguration.TargetMask"/> allows (normal,
    /// trivial and rare by default, as upstream), unless it fights the
    /// player. An unknown classification passes.
    /// </summary>
    public static bool IsEligible(VisualNameplate plate, VisualPlayerIdentity player,
        ClassConfiguration config) =>
        !plate.IsEmpty &&
        IsEligible(plate.Level, plate.Attackable, plate.Dead, plate.IsPlayer, plate.TapDenied,
            player, config) &&
        (plate.Attacks || plate.Classification is not { } kind || config.TargetMask.HasFlagF(kind));

    /// <summary>
    /// The selected unit, when its checked flags and identity make it
    /// eligible and the death latch has not reported it dead.
    /// </summary>
    public static bool TryEligibleTarget(VisualBarSample bars, ClassConfiguration config,
        out VisualTargetIdentity identity)
    {
        identity = default;
        if (!ForeverFrame.HasTargetIdentity(bars) ||
            bars.TargetFlags is not { Present: true, PlayerDead: false } flags ||
            bars.TargetIdentity is not { Present: true } target ||
            bars.PlayerIdentity is not { } player ||
            bars.TargetHealth <= 0 ||
            ReportedDead(bars, target.GuidHash1, target.GuidHash2) ||
            !IsEligible(target.Level, flags.Attackable, flags.Dead, target.IsPlayer,
                tapDenied: null, player, config))
            return false;
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
    /// The plate to take next: eligible, its marker on screen to click, not
    /// set aside unless it fights the player, and in combat one that fights
    /// the player. Out of combat a unit too close for every opener is passed
    /// over. Preferred, in order: an attacker, the current target (no click
    /// needed), a unit the camera can see, one an opener reaches, one a Combat
    /// spell reaches, then the lowest on screen, which on open ground is the nearest.
    /// </summary>
    public static bool TryChoose(VisualBarSample bars, ClassConfiguration config,
        IForeverCombatRoutine combat, ForeverExclusions exclusions, out VisualNameplate candidate)
    {
        candidate = default;
        if (bars.Nameplates is not { } roster || bars.PlayerIdentity is not { } player ||
            bars.TargetFlags is not { } flags)
            return false;

        bool inCombat = flags.PlayerCombat;
        bool found = false;
        int bestRank = -1;
        double bestHeight = double.MaxValue;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            // A unit set aside earlier is still fought once it attacks.
            if (roster.GetSlot(slot) is not { Screen: { } screen } plate ||
                !IsEligible(plate, player, config) ||
                ReportedDead(bars, plate.GuidHash1, plate.GuidHash2) ||
                (inCombat ? !plate.Attacks : exclusions.IsExcluded(plate)))
                continue;

            ForeverReach reach = combat.Reach(bars, ForeverSlotRanges.Of(plate));
            if (!inCombat && reach.TooCloseToOpen)
                continue;

            int rank = (plate.Attacks ? 16 : 0) + (plate.IsTarget == true ? 8 : 0) +
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
}
