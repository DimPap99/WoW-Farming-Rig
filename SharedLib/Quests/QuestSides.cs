namespace SharedLib.Quests;

/// <summary>Which side a race is on, and which creatures serve a side: the rules quests and NPC visits share.</summary>
public static class QuestSides
{
    /// <summary>
    /// Alliance's races as a race mask (bit <c>race - 1</c>): Classic's, and Forever's Skyborne at bit 32,
    /// as QuestieDB's <c>expansions.lua</c> has them; <c>scripts/extract-questiedb.py</c> sides quests by it.
    /// </summary>
    public const long AllianceRaces = 1 | 4 | 8 | 64 | (1L << 32);

    /// <summary>Horde's races, Forever's Skyborne at bit 33.</summary>
    public const long HordeRaces = 2 | 16 | 32 | 128 | (1L << 33);

    // A race id past the mask's 64 bits is none of these.
    private const int MaskBits = 64;

    /// <summary>
    /// The side of race <paramref name="raceId"/>, the client's own id (1 Human, 2 Orc, ...);
    /// <see cref="QuestSide.Both"/> for a race neither mask holds.
    /// </summary>
    public static QuestSide OfRace(int raceId)
    {
        if (raceId is < 1 or > MaskBits)
        {
            return QuestSide.Both;
        }

        long bit = 1L << (raceId - 1);
        return (AllianceRaces & bit) != 0 ? QuestSide.Alliance
            : (HordeRaces & bit) != 0 ? QuestSide.Horde
            : QuestSide.Both;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> deals with a character of <paramref name="side"/> - a vendor
    /// sells to them, an innkeeper takes them in - by its <see cref="Creature.FriendlyTo"/>. A side
    /// unknown (<see cref="QuestSide.Both"/>) takes only a creature that serves both; a creature whose
    /// data does not say serves no one here.
    /// </summary>
    public static bool Serves(in Creature creature, QuestSide side)
    {
        string friendly = creature.FriendlyTo ?? string.Empty;
        bool alliance = friendly.Contains('A');
        bool horde = friendly.Contains('H');
        return side switch
        {
            QuestSide.Alliance => alliance,
            QuestSide.Horde => horde,
            _ => alliance && horde,
        };
    }
}
