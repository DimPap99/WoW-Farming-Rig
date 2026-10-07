using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;
using SharedLib.Forever;
using SharedLib.Quests;

using System.Linq;
using System.Numerics;

using Xunit;

namespace ServiceTests.Forever;

/// <summary>
/// The places a Forever run finds by who or what is there (<see cref="ForeverDestinations"/>), on
/// Forever's own data: the nearest NPC of a role who serves the character's side, an NPC by name,
/// where a quest is given and handed in, the camp a quest sends a character to, and each as the
/// travel or the roam a runner carries out. The runners only add where the character stands, read
/// from the screen.
/// </summary>
public sealed class ForeverDestinationsTests
{
    private const int EasternKingdoms = 0;
    private const int Kalimdor = 1;
    private const int ElwynnForest = 1429;
    private const int Durotar = 1411;
    private const int StormwindCity = 1453;

    private const int KoboldCampCleanup = 7;
    private const int LazyPeons = 5441;

    private static readonly DataConfig Forever = new() { Root = TestData.RepoJson, Exp = "forever" };
    private static readonly QuestBook Book = QuestBook.Load(Forever, NullLogger.Instance);
    private static readonly WorldMapAreaDB Maps = new(Forever);

    // Where a character stands, as a player reads it.
    private static readonly Vector2 Goldshire = At(ElwynnForest, 43, 66);
    private static readonly Vector2 Northshire = At(ElwynnForest, 48, 40);
    private static readonly Vector2 RazorHill = At(Durotar, 52, 43);
    private static readonly Vector2 ValleyOfTrials = At(Durotar, 43, 67);

    [Fact]
    public void The_nearest_innkeeper_is_one_who_serves_the_characters_side()
    {
        Assert.True(Find(ForeverVisitTarget.Nearest("innkeeper"), QuestSide.Alliance, EasternKingdoms, Goldshire, out ForeverFound alliance, out string why), why);
        Assert.True(Find(ForeverVisitTarget.Nearest("innkeeper"), QuestSide.Horde, EasternKingdoms, Goldshire, out ForeverFound horde, out why), why);
        Assert.True(Find(ForeverVisitTarget.Nearest("innkeeper"), QuestSide.Horde, Kalimdor, RazorHill, out ForeverFound grosk, out why), why);

        Assert.Equal("Innkeeper Farley", alliance.Name);
        Assert.True(alliance.Yards < 100, alliance.ToString());
        Assert.NotEqual("Innkeeper Farley", horde.Name);
        Assert.True(horde.Yards > alliance.Yards, horde.ToString());
        Assert.Equal("Innkeeper Grosk", grosk.Name);
    }

    [Fact]
    public void A_neutral_towns_npc_serves_both_sides_and_a_race_has_its_side()
    {
        Creature skindle = Book.Creatures.Values.Single(c => c.Name == "Innkeeper Skindle");
        Creature farley = Book.Creatures.Values.Single(c => c.Name == "Innkeeper Farley");

        Assert.True(QuestSides.Serves(skindle, QuestSide.Alliance) && QuestSides.Serves(skindle, QuestSide.Horde));
        Assert.True(QuestSides.Serves(skindle, QuestSide.Both));
        Assert.True(QuestSides.Serves(farley, QuestSide.Alliance));
        Assert.False(QuestSides.Serves(farley, QuestSide.Horde));
        Assert.False(QuestSides.Serves(farley, QuestSide.Both));
        Assert.Equal(QuestSide.Alliance, QuestSides.OfRace(1));
        Assert.Equal(QuestSide.Horde, QuestSides.OfRace(2));
        Assert.Equal(QuestSide.Alliance, QuestSides.OfRace(33));
        Assert.Equal(QuestSide.Horde, QuestSides.OfRace(34));
        Assert.Equal(QuestSide.Both, QuestSides.OfRace(9));
        Assert.Equal(QuestSide.Both, QuestSides.OfRace(0));
    }

    [Fact]
    public void A_trainer_is_found_by_its_title()
    {
        Assert.True(Find(ForeverVisitTarget.Nearest("classtrainer", "Warrior Trainer"), QuestSide.Alliance, EasternKingdoms, Northshire,
            out ForeverFound warrior, out string why), why);
        Assert.True(Find(ForeverVisitTarget.Nearest("classtrainer", "paladin trainer"), QuestSide.Alliance, EasternKingdoms, Northshire,
            out ForeverFound paladin, out why), why);

        Assert.Equal("Llane Beshere", warrior.Name);
        Assert.Equal("Brother Sammuel", paladin.Name);
    }

    [Fact]
    public void A_quest_is_visited_where_it_is_given_and_handed_in_and_an_npc_by_name()
    {
        Assert.True(Find(ForeverVisitTarget.QuestStart(KoboldCampCleanup), QuestSide.Both, EasternKingdoms, Goldshire, out ForeverFound start, out string why), why);
        Assert.True(Find(ForeverVisitTarget.QuestEnd(KoboldCampCleanup), QuestSide.Both, EasternKingdoms, Goldshire, out ForeverFound end, out why), why);
        Assert.True(Find(ForeverVisitTarget.Npc("innkeeper farley"), QuestSide.Horde, EasternKingdoms, Northshire, out ForeverFound farley, out why), why);

        Assert.Equal("Marshal McBride", start.Name);
        Assert.Equal(start.At, end.At);
        Assert.Equal("Innkeeper Farley", farley.Name);
    }

    [Theory]
    [InlineData(ForeverVisitKind.QuestStart, KoboldCampCleanup, "", Kalimdor, "is not on this continent")]
    [InlineData(ForeverVisitKind.QuestStart, 999999999, "", EasternKingdoms, "quest 999999999 is not in the quest data")]
    [InlineData(ForeverVisitKind.Role, 0, "dentist", EasternKingdoms, "dentist is not an NPC role")]
    [InlineData(ForeverVisitKind.Npc, 0, "Nobody Atall", EasternKingdoms, "Nobody Atall is not on this continent")]
    public void A_place_the_data_does_not_have_here_is_refused_with_why(ForeverVisitKind kind, int quest, string name, int map,
        string expected)
    {
        Assert.False(Find(new ForeverVisitTarget(kind, name, quest), QuestSide.Alliance, map, Goldshire, out _, out string why));

        Assert.Contains(expected, why);
    }

    [Fact]
    public void A_quests_camp_is_the_nearest_of_its_creatures_spawns_and_those_near_them()
    {
        Assert.True(ForeverDestinations.TryCamp(Book, KoboldCampCleanup, EasternKingdoms, Northshire, out ForeverCamp camp, out string why), why);
        Assert.False(ForeverDestinations.TryCamp(Book, KoboldCampCleanup, Kalimdor, RazorHill, out _, out string elsewhere));

        Assert.Equal(["Kobold Vermin"], camp.Names);
        Assert.True(camp.Places >= 5, camp.ToString());
        Vector2 size = camp.Max - camp.Min;
        Assert.InRange(size.X, ForeverDestinations.CampMinYards, 500);
        Assert.InRange(size.Y, ForeverDestinations.CampMinYards, 500);
        Assert.Contains("sends no one to hunt or gather on this continent", elsewhere);
    }

    // A camp's rectangle goes on one map that holds it whole: Durotar's and the Barrens' both hold the
    // Valley of Trials, and the rule picks either, as a guess without the area grid does. Either names
    // the same ground.
    [Fact]
    public void A_visit_and_a_camp_become_a_travel_and_a_roam_on_a_map_that_holds_them()
    {
        ForeverVisitTask visit = new(ForeverVisitTarget.QuestStart(KoboldCampCleanup), ForeverPulls.OnTheWay, ForeverUntil.Never);
        ForeverQuestAreaTask quest = new(LazyPeons, ForeverPulls.Any, ForeverUntil.AfterKills(5)) { Seed = 3 };

        Assert.True(ForeverDestinations.TryTravel(Book, Maps, visit, QuestSide.Both, EasternKingdoms, Goldshire,
            out ForeverTravelTask travel, out _, out string why), why);
        Assert.True(ForeverDestinations.TryRoam(Book, Maps, quest, Kalimdor, ValleyOfTrials, out ForeverRoamAreaTask roam, out _, out why), why);

        Assert.Equal(ElwynnForest, travel.To.UIMapId);
        Assert.Equal(48.9, travel.To.X, 0.2);
        Assert.Equal(41.6, travel.To.Y, 0.2);
        Assert.Equal(ForeverPulls.OnTheWay, travel.Pulls);
        Assert.True(roam.Area.TryToWorld(Maps, out ForeverWorldArea area, out int map, out why), why);
        Assert.Equal(Kalimdor, map);
        Assert.True(ForeverDestinations.TryCamp(Book, LazyPeons, Kalimdor, ValleyOfTrials, out ForeverCamp camp, out why), why);
        Assert.True(area.Contains(camp.Min + new Vector2(1)) && area.Contains(camp.Max - new Vector2(1)), camp.ToString());
        Assert.InRange(camp.Places, 5, ForeverDestinations.CampMaxPlaces);
        Assert.Equal(5, roam.Until.Kills);
        Assert.Equal(3, roam.Seed);
        Assert.True(area.Width >= ForeverDestinations.CampMinYards - 1, $"{area.Width} yd wide");
    }

    [Fact]
    public void A_world_spot_is_named_on_the_zone_map_it_lies_furthest_inside()
    {
        Vector2 stormwind = At(StormwindCity, 50, 50);

        ForeverMapPoint? inCity = ForeverMapPoint.FromWorld(Maps, EasternKingdoms, stormwind.X, stormwind.Y);
        ForeverMapPoint? inForest = ForeverMapPoint.FromWorld(Maps, EasternKingdoms, Goldshire.X, Goldshire.Y);
        ForeverMapPoint? nowhere = ForeverMapPoint.FromWorld(Maps, EasternKingdoms, 50000, 50000);

        Assert.Equal(StormwindCity, inCity?.UIMapId);
        Assert.Equal(ElwynnForest, inForest?.UIMapId);
        Assert.Equal(43, inForest!.Value.X, 0.01);
        Assert.Equal(66, inForest.Value.Y, 0.01);
        Assert.Null(nowhere);
    }

    private static bool Find(ForeverVisitTarget target, QuestSide side, int map, Vector2 from, out ForeverFound found, out string why)
    {
        return ForeverDestinations.TryFind(Book, target, side, map, from, out found, out why);
    }

    private static Vector2 At(int uiMap, double x, double y)
    {
        Assert.True(new ForeverMapPoint(uiMap, x, y).TryToWorld(Maps, out Vector2 world, out _, out string why), why);
        return world;
    }
}
