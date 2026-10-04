using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Core.Forever;

/// <summary>
/// One unit the run could pull now and what the pull would cost: how many
/// hostile units could join the fight, then the effort of getting to it.
/// <see cref="ForeverPullOptions"/> lists them and takes the best.
/// </summary>
/// <param name="Plate">The unit's nameplate, to click.</param>
/// <param name="At">Where it stands in the world, as its nameplate and the minimap's tracking place it.</param>
/// <param name="Yards">How far that is from the player.</param>
/// <param name="HostileNear">Other hostile units standing near it, which a pull of it could draw too.</param>
/// <param name="Occluded">The camera cannot see it, so a shot could be refused for line of sight.</param>
/// <param name="InReach">An attack reaches it from where the player stands.</param>
/// <param name="LevelsAbove">How many levels above the player it is; 0 at or below.</param>
/// <param name="Selected">It is the current target, which needs no click.</param>
public readonly record struct ForeverPullOption(VisualNameplate Plate, Vector2 At, float Yards, int HostileNear,
    bool Occluded, bool InReach, int LevelsAbove, bool Selected)
{
    /// <summary>
    /// What getting to the fight takes, in yards of walking: how far the unit
    /// stands, plus a price for each thing that slows the pull. It only orders
    /// the pulls; no effort makes a pull refused.
    /// </summary>
    public float Effort =>
        Yards +
        (Occluded ? ForeverPullOptions.OccludedYards : 0) +
        (InReach ? 0 : ForeverPullOptions.OutOfReachYards) +
        LevelsAbove * ForeverPullOptions.LevelAboveYards +
        (Selected ? 0 : ForeverPullOptions.ClickYards) +
        (Clickable ? 0 : ForeverPullOptions.TurnYards);

    /// <summary>
    /// Its marker lies where a click reaches it; otherwise at the side of the
    /// screen, and the run turns to face it first.
    /// </summary>
    public bool Clickable => Plate.Screen is { } marker && ForeverTargetPolicy.Clickable(marker);

    /// <summary>For the log: "hostile level 13, 18 yd, alone: effort 28".</summary>
    public override string ToString()
    {
        StringBuilder text = new();
        text.Append(Plate.Reaction switch
        {
            >= 1 and <= ForeverPullOptions.HostileReaction => "hostile",
            ForeverPullOptions.NeutralReaction => "neutral",
            _ => "unit",
        });
        text.Append(CultureInfo.InvariantCulture, $" level {Plate.Level}, {Yards:0} yd, ");
        text.Append(HostileNear == 0 ? "alone" : $"{HostileNear} hostile near");
        if (Occluded) text.Append(", out of sight");
        if (!InReach) text.Append(", out of reach");
        if (Selected) text.Append(", selected");
        if (!Clickable) text.Append(", at the side of the screen");
        text.Append(CultureInfo.InvariantCulture, $": effort {Effort:0}");
        return text.ToString();
    }
}

/// <summary>
/// Which unit in view to pull, as a person picks a mob standing on its own:
/// every unit the run could pull now is an option (<see cref="Of"/>), and the
/// best one wins (<see cref="TryBest"/>, by <see cref="Compare"/>). Safety
/// comes first: the fewer hostile units that could join the fight, the
/// better, whatever the distance. Of a hostile and a neutral unit standing
/// together the hostile one goes first, since the neutral one does not join a
/// fight it was not drawn into, and the neutral one, alone after that, next.
/// Among equally safe pulls the least <see cref="ForeverPullOption.Effort"/>
/// wins. Nothing here refuses a pull: what may be pulled at all is the
/// target policy's (the profile's level window and kinds); a unit with
/// company is pulled on its own (<see cref="ForeverRotation"/> keeps the pet
/// back). Units are placed by their nameplate's bearing and range flags, or
/// exactly by the minimap's tracking. Decisions over one frame only; in
/// combat <see cref="ForeverTargetPolicy.TryChooseAttacker"/> decides.
/// </summary>
public static class ForeverPullOptions
{
    /// <summary>The camera cannot see the unit: the way round to a line of sight.</summary>
    public const float OccludedYards = 40;
    /// <summary>No attack reaches the unit from here yet: the approach before the first one.</summary>
    public const float OutOfReachYards = 10;
    /// <summary>
    /// Per level the unit stands above the player: a slightly longer fight.
    /// Small on purpose, a preference between near alike units, never a reason
    /// to pass a unit on its own by.
    /// </summary>
    public const float LevelAboveYards = 5;
    /// <summary>A unit other than the current target needs a click first.</summary>
    public const float ClickYards = 5;
    /// <summary>The unit stands at the side of the screen: the turn to face it.</summary>
    public const float TurnYards = 5;
    /// <summary>
    /// A hostile unit standing this near the one pulled can join the fight: a
    /// mob's aggro radius is about 20 yards against its own level.
    /// </summary>
    public const float NeighbourYards = 20;
    /// <summary>
    /// The highest <c>UnitReaction</c> of a red plate (hated, hostile,
    /// unfriendly): a unit that attacks whoever comes near.
    /// </summary>
    public const int HostileReaction = 3;
    /// <summary>A yellow plate's <c>UnitReaction</c>: a unit that fights only when attacked.</summary>
    public const int NeutralReaction = 4;

    /// <summary>
    /// Out of combat, every unit the run could pull now: eligible, its marker
    /// between the clickable bottom and top (<see cref="ForeverTargetPolicy.InClickableRow"/>;
    /// one at the side is turned to before the click), not
    /// set aside, and not too close for every opener.
    /// </summary>
    public static List<ForeverPullOption> Of(VisualBarSample bars, ClassConfiguration config,
        IForeverCombatRoutine combat, ForeverExclusions exclusions)
    {
        List<ForeverPullOption> options = [];
        if (bars.Nameplates is not { } roster || bars.PlayerIdentity is not { } player ||
            bars.WorldPosition is not { } world || bars.TargetFlags is not { PlayerCombat: false })
            return options;
        Vector2 here = new((float)world.X, (float)world.Y);
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is not { Screen: { } marker } plate ||
                !ForeverTargetPolicy.InClickableRow(marker) ||
                !ForeverTargetPolicy.IsEligible(plate, player, config) ||
                ForeverTargetPolicy.ReportedDead(bars, plate.GuidHash1, plate.GuidHash2) ||
                exclusions.IsExcluded(plate))
                continue;
            ForeverReach reach = combat.Reach(bars, ForeverSlotRanges.Of(plate));
            if (reach.TooCloseToOpen ||
                !ForeverCorpses.TryPlace(bars, plate, out Vector2 near, out Vector2 far))
                continue;
            Vector2 at = Vector2.Lerp(near, far, 0.5f);
            options.Add(new(plate, at, Vector2.Distance(here, at),
                HostileNear(bars, plate), plate.Occluded == true, reach.Opener || reach.Combat,
                Math.Max(0, plate.Level - player.Level), plate.IsTarget == true));
        }
        return options;
    }

    /// <summary>
    /// The better of two pulls first: fewer hostile units that could join the
    /// fight, then less effort, then the lower on screen, which on open ground
    /// is the nearer.
    /// </summary>
    public static int Compare(ForeverPullOption a, ForeverPullOption b) =>
        a.HostileNear != b.HostileNear ? a.HostileNear.CompareTo(b.HostileNear)
        : a.Effort != b.Effort ? a.Effort.CompareTo(b.Effort)
        : a.Plate.Screen!.Value.Y.CompareTo(b.Plate.Screen!.Value.Y);

    /// <summary>The best of <paramref name="options"/>; false only when there are none.</summary>
    public static bool TryBest(IReadOnlyList<ForeverPullOption> options, out ForeverPullOption best)
    {
        best = default;
        bool found = false;
        foreach (ForeverPullOption option in options)
        {
            if (!found || Compare(option, best) < 0)
            {
                best = option;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// How many other hostile units, alive and fighting no one, may stand
    /// within <see cref="NeighbourYards"/> of <paramref name="plate"/>'s unit.
    /// A neutral unit does not join a fight it was not drawn into, and a unit
    /// that cannot be placed says nothing.
    /// </summary>
    public static int HostileNear(VisualBarSample bars, VisualNameplate plate)
    {
        if (bars.Nameplates is not { } roster || !TryPoint(bars, plate, out Vector2 at))
            return 0;
        int count = 0;
        for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
        {
            if (roster.GetSlot(slot) is not { IsEmpty: false } other || other.Slot == plate.Slot ||
                other.Reaction is not (>= 1 and <= HostileReaction) ||
                other.Dead == true || other.TapDenied == true || other.Attacks ||
                ForeverTargetPolicy.ReportedDead(bars, other.GuidHash1, other.GuidHash2) ||
                !TryPoint(bars, other, out Vector2 otherAt))
                continue;
            if (Vector2.Distance(at, otherAt) <= NeighbourYards)
                count++;
        }
        return count;
    }

    // A unit's place as one point: the tracked dot, or the middle of the
    // distance band its range flags give. A plate whose flags say nothing
    // could be anywhere within plate distance, too vague to count.
    private static bool TryPoint(VisualBarSample bars, VisualNameplate plate, out Vector2 point)
    {
        point = default;
        if (!ForeverCorpses.TryPlace(bars, plate, out Vector2 near, out Vector2 far) ||
            near != far && plate.Close is null && plate.WithinFollowDistance is null)
            return false;
        point = Vector2.Lerp(near, far, 0.5f);
        return true;
    }
}
