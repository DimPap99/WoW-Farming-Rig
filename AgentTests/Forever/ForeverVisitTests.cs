using Core;
using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using SharedLib;
using SharedLib.Forever;

using System;
using System.IO;
using System.Numerics;

using Xunit;

namespace AgentTests.Forever;

/// <summary>
/// What a run makes of a step that names its place by who or what is there: the side the character's
/// race is on, its class's trainer, where it stands - read from the screen - and the world data
/// (<see cref="ForeverWorldData"/>), into the travel or the roam that carries the step out.
/// </summary>
public sealed class ForeverVisitTests
{
    private const int Durotar = 1411;
    private const int ElwynnForest = 1429;
    private const int LazyPeons = 5441;
    private const int KoboldCampCleanup = 7;

    private static readonly DataConfig Forever = new() { Root = RepoJson, Exp = "forever" };
    private static readonly WorldMapAreaDB Maps = new(Forever);
    private static readonly ForeverWorldData World = new(Forever, NullLogger<ForeverWorldData>.Instance);

    [Fact]
    public void A_role_is_visited_for_the_side_and_the_class_of_the_character()
    {
        VisualBarSample orcWarrior = Standing(Durotar, 52, 43, UnitClass.Warrior, UnitRace.Orc);
        VisualBarSample humanPaladin = Standing(ElwynnForest, 48, 40, UnitClass.Paladin, UnitRace.Human);

        string inn = Visit(ForeverVisitTarget.Nearest("innkeeper"), orcWarrior);
        string orcTrainer = Visit(ForeverVisitTarget.Nearest("classtrainer"), orcWarrior);
        string humanTrainer = Visit(ForeverVisitTarget.Nearest("classtrainer"), humanPaladin);

        Assert.Equal("Innkeeper Grosk", inn);
        Assert.Equal("Tarshaw Jaggedscar", orcTrainer);
        Assert.Equal("Brother Sammuel", humanTrainer);
    }

    [Fact]
    public void A_role_needs_the_race_on_the_screen_and_a_quest_does_not()
    {
        VisualBarSample unread = Standing(ElwynnForest, 48, 40, UnitClass.Paladin, UnitRace.Human) with { PlayerIdentity = null };
        ForeverVisitTask role = new(ForeverVisitTarget.Nearest("vendor"), ForeverPulls.None, ForeverUntil.Never);
        ForeverVisitTask quest = new(ForeverVisitTarget.QuestStart(KoboldCampCleanup), ForeverPulls.None, ForeverUntil.Never);

        bool roleFound = ForeverVisit.TryResolve(role, unread, World, Maps, out _, out _, out string why);
        bool questFound = ForeverVisit.TryResolve(quest, unread, World, Maps, out ForeverTravelTask travel, out ForeverFound found,
            out string questWhy);

        Assert.False(roleFound);
        Assert.Contains("race is unreadable", why);
        Assert.True(questFound, questWhy);
        Assert.Equal("Marshal McBride", found.Name);
        Assert.Equal(ElwynnForest, travel.To.UIMapId);
    }

    [Fact]
    public void A_quests_camp_is_roamed_from_where_the_character_stands()
    {
        VisualBarSample valley = Standing(Durotar, 43, 67, UnitClass.Warrior, UnitRace.Orc);
        ForeverQuestAreaTask quest = new(LazyPeons, ForeverPulls.Any, ForeverUntil.AfterKills(5)) { Seed = 7 };

        Assert.True(ForeverQuestArea.TryResolve(quest, valley, World, Maps, out ForeverRoamAreaTask roam, out ForeverCamp camp,
            out string why), why);

        Assert.Equal(["Lazy Peon"], camp.Names);
        Assert.Equal(5, roam.Until.Kills);
        Assert.Equal(7, roam.Seed);
        Assert.Equal(ForeverPulls.Any, roam.Pulls);
    }

    private static string Visit(ForeverVisitTarget target, VisualBarSample bars)
    {
        ForeverVisitTask task = new(target, ForeverPulls.None, ForeverUntil.Never);
        Assert.True(ForeverVisit.TryResolve(task, bars, World, Maps, out _, out ForeverFound found, out string why), why);
        return found.Name;
    }

    // A character of the class and race at a point of a zone map, as the screen shows it.
    private static VisualBarSample Standing(int uiMap, double x, double y, UnitClass unitClass, UnitRace race)
    {
        Assert.True(new ForeverMapPoint(uiMap, x, y).TryToWorld(Maps, out Vector2 world, out int instance, out string why), why);
        return new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with
        {
            Position = new VisualPositionSample(uiMap, x / 100, y / 100, 0),
            WorldPosition = new VisualWorldPositionSample(world.X, world.Y, 0, instance),
            PlayerIdentity = new VisualPlayerIdentity((int)unitClass, (int)race, 10),
        };
    }

    private static string RepoJson
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "WoWFarmingRig.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return Path.Combine(dir ?? throw new DirectoryNotFoundException("WoWFarmingRig.sln"), "Json");
        }
    }
}
