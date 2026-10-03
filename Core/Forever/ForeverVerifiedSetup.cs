using System;

namespace Core.Forever;

/// <summary>
/// What the guarded Forever runtime has been verified against live: these
/// profiles, on this map, for these classes. The runtime reads every spell,
/// slot and threshold from the profile; a class joins this list after its
/// profile passes a bounded live sweep.
/// </summary>
public static class ForeverVerifiedSetup
{
    public const string TrialProfile = "Paladin_Forever_Trial.json";
    public const string AutoProfile = "Paladin_Forever_Auto.json";

    /// <summary>Tirisfal Glades, the only UI map with verified positions and navmesh.</summary>
    public const int UIMapId = 1420;

    public static bool IsProfile(string? fileName) =>
        IsTrialProfile(fileName) || IsAutoProfile(fileName);

    public static bool IsTrialProfile(string? fileName) =>
        string.Equals(fileName, TrialProfile, StringComparison.OrdinalIgnoreCase);

    public static bool IsAutoProfile(string? fileName) =>
        string.Equals(fileName, AutoProfile, StringComparison.OrdinalIgnoreCase);

    public static bool IsVerifiedClass(int classId) =>
        classId == (int)UnitClass.Paladin;
}
