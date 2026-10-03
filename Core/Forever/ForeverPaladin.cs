using System;

namespace Core.Forever;

/// <summary>
/// The one character setup the guarded Forever runtime is verified against:
/// a Paladin in Tirisfal Glades with this action layout. Every Forever goal and
/// gate checks against these values instead of repeating the numbers.
/// </summary>
public static class ForeverPaladin
{
    public const string TrialProfile = "Paladin_Forever_Trial.json";
    public const string AutoProfile = "Paladin_Forever_Auto.json";

    /// <summary>Tirisfal Glades, the only UI map with verified positions and navmesh.</summary>
    public const int UIMapId = 1420;

    public const int HolyLight = 635;
    public const int Judgement = 20271;
    public const int HolyStrikeRank1 = 679;
    public const int HolyStrikeRank2 = 678;
    public const int SealOfRighteousness = 20287;

    public const int HolyLightSlot = 1;
    public const int JudgementSlot = 2;
    public const int HolyStrikeSlot = 3;
    public const int SealSlot = 5;

    public static bool IsProfile(string? fileName) =>
        string.Equals(fileName, TrialProfile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, AutoProfile, StringComparison.OrdinalIgnoreCase);

    public static bool IsHolyStrike(int spellId) =>
        spellId is HolyStrikeRank1 or HolyStrikeRank2;

    /// <summary>Spells cast on the player, so a target change does not void them.</summary>
    public static bool IsSelfCast(int spellId) =>
        spellId is HolyLight or SealOfRighteousness;
}
