using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Core.Forever;

/// <summary>
/// What the guarded Forever runtime has been verified against live: these
/// profiles, on these maps, for these classes. The runtime reads every spell,
/// slot and threshold from the profile; a class or map joins these lists after
/// a bounded live sweep passes there.
/// </summary>
public static class ForeverVerifiedSetup
{
    public const string PaladinTrialProfile = "Paladin_Forever_Trial.json";
    public const string PaladinAutoProfile = "Paladin_Forever_Auto.json";
    public const string HunterAutoProfile = "Hunter_Forever_Auto.json";

    public const int TirisfalGlades = 1420;
    public const int Durotar = 1411;

    /// <summary>
    /// UI maps with verified positions and a baked navmesh, each with the
    /// instance map its world positions report (0 Azeroth, 1 Kalimdor).
    /// </summary>
    private static readonly FrozenDictionary<int, int> Maps =
        new Dictionary<int, int>
        {
            [TirisfalGlades] = 0,
            [Durotar] = 1,
        }.ToFrozenDictionary();

    public static bool IsVerifiedMap(int uiMapId, int instanceMapId) =>
        Maps.TryGetValue(uiMapId, out int instance) && instance == instanceMapId;

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
