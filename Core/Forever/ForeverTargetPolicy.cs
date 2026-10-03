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

    public static bool IsEligible(VisualNameplate plate, VisualPlayerIdentity player,
        ClassConfiguration config) =>
        !plate.IsEmpty &&
        IsEligible(plate.Level, plate.Attackable, plate.Dead, plate.IsPlayer, plate.TapDenied,
            player, config);

    /// <summary>The selected unit, when its checked flags and identity make it eligible.</summary>
    public static bool TryEligibleTarget(VisualBarSample bars, ClassConfiguration config,
        out VisualTargetIdentity identity)
    {
        identity = default;
        if (!ForeverFrame.HasTargetIdentity(bars) ||
            bars.TargetFlags is not { Present: true, PlayerDead: false } flags ||
            bars.TargetIdentity is not { Present: true } target ||
            bars.PlayerIdentity is not { } player ||
            bars.TargetHealth <= 0 ||
            !IsEligible(target.Level, flags.Attackable, flags.Dead, target.IsPlayer,
                tapDenied: null, player, config))
            return false;
        identity = target;
        return true;
    }

    /// <summary>
    /// The plate to take next: eligible, its marker on screen to click, not
    /// set aside, and in combat one that fights the player. Out of combat a
    /// unit too close for every opener is passed over. Preferred, in order: an
    /// attacker, the current target (no click needed), a unit the camera can
    /// see, one an opener reaches, one a Combat spell reaches, then the lowest
    /// on screen, which on open ground is the nearest.
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
            if (roster.GetSlot(slot) is not { Screen: { } screen } plate ||
                !IsEligible(plate, player, config) || exclusions.IsExcluded(plate) ||
                (inCombat && !plate.Attacks))
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
