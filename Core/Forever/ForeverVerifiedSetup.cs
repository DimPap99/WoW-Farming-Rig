using System;

namespace Core.Forever;

/// <summary>
/// What the guarded Forever runtime has been verified against live: these
/// profiles, for these classes. The runtime reads every spell, slot and
/// threshold from the profile; a class joins these lists after a bounded live
/// sweep passes with it. Maps are not listed: the runtime walks any zone the
/// map data places on a continent with a baked navmesh, once the client's map
/// position agrees with its world position there (<see cref="ForeverNavigator"/>).
/// </summary>
public static class ForeverVerifiedSetup
{
    public const string PaladinTrialProfile = "Paladin_Forever_Trial.json";
    public const string PaladinAutoProfile = "Paladin_Forever_Auto.json";
    public const string HunterAutoProfile = "Hunter_Forever_Auto.json";

    // Zones the live runs so far took place in.
    public const int TirisfalGlades = 1420;
    public const int Durotar = 1411;

    /// <summary>The autonomous profile verified for <paramref name="classId"/>, or null.</summary>
    public static string? AutoProfileFor(int classId) => classId switch
    {
        (int)UnitClass.Paladin => PaladinAutoProfile,
        (int)UnitClass.Hunter => HunterAutoProfile,
        _ => null
    };

    public static bool IsVerifiedClass(int classId) => AutoProfileFor(classId) is not null;

    public static bool IsProfile(string? fileName) =>
        IsTrialProfile(fileName) || IsAutoProfile(fileName);

    public static bool IsTrialProfile(string? fileName) =>
        string.Equals(fileName, PaladinTrialProfile, StringComparison.OrdinalIgnoreCase);

    public static bool IsAutoProfile(string? fileName) =>
        string.Equals(fileName, PaladinAutoProfile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, HunterAutoProfile, StringComparison.OrdinalIgnoreCase);
}
