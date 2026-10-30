using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;
using SharedLib.Quests;

using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace ServiceTests.Forever;

/// <summary>
/// WoW: Forever's quests as scripts/extract-questiedb.py wrote them (Json/quests/forever), read by
/// the book the Pathing Service and the bot share, and turned into the places a quest sends a
/// character to.
/// </summary>
public sealed class QuestBookTests
{
    private const int KoboldCampCleanup = 7;
    private const int WolvesAcrossTheBorder = 33;
    private const int TheFargodeepMine = 62;
    private const int PyrewoodAmbush = 452;

    private const int MarshalMcBride = 197;
    private const int KoboldVermin = 6;
    private const int EasternKingdoms = 0;

    private static readonly QuestBook Book = QuestBook.Load(new DataConfig { Root = TestData.RepoJson, Exp = "forever" },
        NullLogger.Instance);

    [Fact]
    public void The_forever_book_holds_quests_objects_items_creatures_and_spawns()
    {
        Assert.True(Book.Quests.Count > 4000, $"{Book.Quests.Count} quests");
        Assert.True(Book.Objects.Count > 4000, $"{Book.Objects.Count} objects");
        Assert.True(Book.Items.Count > 3000, $"{Book.Items.Count} items");
        Assert.True(Book.Creatures.Count > 13000, $"{Book.Creatures.Count} creatures");
        Assert.NotEmpty(Book.SpawnsOfCreature(KoboldVermin));
        Assert.Equal("Kobold Camp Cleanup", Book.Quests[KoboldCampCleanup].Name);
        Assert.Equal(QuestSide.Alliance, Book.Quests[KoboldCampCleanup].Side);
    }

    [Fact]
    public void A_kill_quest_is_given_killed_and_taken_back_where_its_creatures_stand()
    {
        List<QuestPlace> places = Book.PlacesOf(Book.Quests[KoboldCampCleanup]);

        QuestPlace start = Assert.Single(places, p => p.Kind == QuestPlaceKind.Start);
        QuestPlace end = Assert.Single(places, p => p.Kind == QuestPlaceKind.End);
        List<QuestPlace> kills = [.. places.Where(p => p.Kind == QuestPlaceKind.Kill)];

        Assert.Equal(MarshalMcBride, start.Source);
        Assert.Equal("Marshal McBride", start.Name);
        Assert.Equal(start.At, end.At);
        Assert.True(kills.Count > 10, $"{kills.Count} Kobold Vermin spawns");
        Assert.All(kills, p =>
        {
            Assert.Equal(KoboldVermin, p.Source);
            Assert.Equal(0, p.Objective);
            Assert.Equal(EasternKingdoms, p.At.Map);
            // Northshire, by the abbey: within a few hundred yards of the marshal.
            Assert.InRange(System.MathF.Sqrt(Sq(p.At.X - start.At.X) + Sq(p.At.Y - start.At.Y)), 0, 400);
        });
    }

    [Fact]
    public void An_item_to_collect_is_placed_where_it_drops()
    {
        List<QuestPlace> loot = [.. Book.PlacesOf(Book.Quests[WolvesAcrossTheBorder]).Where(p => p.Kind == QuestPlaceKind.Loot)];

        Assert.NotEmpty(loot);
        Assert.All(loot, p => Assert.Equal("Tough Wolf Meat", p.Item));
        Assert.Contains(loot, p => p.Name.Contains("Wolf"));
    }

    [Fact]
    public void A_place_to_explore_and_kill_credit_from_several_creatures()
    {
        QuestPlace scout = Assert.Single(Book.PlacesOf(Book.Quests[TheFargodeepMine]), p => p.Kind == QuestPlaceKind.Explore);
        HashSet<int> credited = [.. Book.PlacesOf(Book.Quests[PyrewoodAmbush]).Where(p => p.Kind == QuestPlaceKind.Kill).Select(p => p.Source)];

        Assert.Contains("Fargodeep", scout.Name);
        Assert.Equal(EasternKingdoms, scout.At.Map);
        Assert.True(credited.Count > 1, "every creature of the ambush counts");
    }

    [Fact]
    public void A_side_takes_its_own_quests_and_those_for_both()
    {
        QuestRecord alliance = Book.Quests[KoboldCampCleanup];

        Assert.True(QuestBook.ForSide(alliance, QuestSide.Alliance));
        Assert.False(QuestBook.ForSide(alliance, QuestSide.Horde));
        Assert.True(QuestBook.ForSide(alliance with { Side = QuestSide.Both }, QuestSide.Horde));
    }

    [Fact]
    public void A_client_without_the_files_has_an_empty_book()
    {
        using TempDataRoot data = new(withMapData: true, client: "forever");

        QuestBook empty = QuestBook.Load(data.Config(), NullLogger.Instance);

        Assert.Empty(empty.Quests);
        Assert.Empty(empty.NpcSpawns);
    }

    private static float Sq(float v) => v * v;
}
