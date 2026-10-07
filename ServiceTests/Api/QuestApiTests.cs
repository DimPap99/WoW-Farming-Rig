using Contracts.Pathing;

using ServiceClients;
using ServiceClients.Pathing;

using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The quests a Forever service serves: by zone, by name, and one with every place it sends a character to.</summary>
public sealed class QuestApiTests
{
    private const string Forever = "forever";
    private const int ElwynnForest = 1429;
    private const int KoboldCampCleanup = 7;
    private const int WolvesAcrossTheBorder = 33;

    [Fact]
    public async Task A_zone_lists_the_quests_given_in_it_with_their_givers()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson, Forever);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<QuestSummary[]> elwynn = await client.GetQuestsAsync(ElwynnForest, TestContext.Current.CancellationToken);

        Assert.True(elwynn.Ok, elwynn.Error);
        QuestSummary kobolds = Assert.Single(elwynn.Value!, q => q.Id == KoboldCampCleanup);
        Assert.Equal("Marshal McBride", kobolds.Giver);
        Assert.Equal("Alliance", kobolds.Side);
        Assert.NotNull(kobolds.GiverAt);
        Assert.Equal(0, kobolds.GiverMapId);
        Assert.True(elwynn.Value!.Length > 20, $"{elwynn.Value.Length} quests in Elwynn");
        Assert.Equal(elwynn.Value.OrderBy(q => q.MinLevel).Select(q => q.MinLevel), elwynn.Value.Select(q => q.MinLevel));
    }

    [Fact]
    public async Task A_quest_comes_with_where_it_is_given_what_it_asks_and_where_it_is_handed_in()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson, Forever);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<QuestDetail> read = await client.GetQuestAsync(KoboldCampCleanup, TestContext.Current.CancellationToken);

        Assert.True(read.Ok, read.Error);
        QuestDetail quest = read.Value!;
        Assert.Equal("Kobold Camp Cleanup", quest.Name);
        Assert.Equal(QuestPartKind.Start, quest.Parts[0].Kind);
        Assert.Equal("Marshal McBride", quest.Parts[0].Name);
        QuestPart kill = Assert.Single(quest.Parts, p => p.Kind == QuestPartKind.Kill);
        Assert.Equal("Kobold Vermin", kill.Name);
        Assert.Equal(0, kill.Objective);
        Assert.True(kill.Places.Length > 10, $"{kill.Places.Length} places");
        Assert.All(kill.Places, p => Assert.Equal(0, p.MapId));
        Assert.Equal(QuestPartKind.End, quest.Parts[^1].Kind);
        Assert.Contains("Kobold Vermin", quest.Text[0]);
    }

    [Fact]
    public async Task An_item_to_collect_comes_with_the_creatures_that_drop_it()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson, Forever);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<QuestDetail> read = await client.GetQuestAsync(WolvesAcrossTheBorder, TestContext.Current.CancellationToken);

        QuestPart[] loot = [.. read.Value!.Parts.Where(p => p.Kind == QuestPartKind.Loot)];
        Assert.NotEmpty(loot);
        Assert.All(loot, p => Assert.Equal("Tough Wolf Meat", p.Item));
        Assert.All(loot, p => Assert.NotEmpty(p.Places));
    }

    [Fact]
    public async Task Quests_are_found_by_name_and_refused_without_a_name()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson, Forever);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<QuestSummary[]> found = await client.SearchQuestsAsync("kobold camp", TestContext.Current.CancellationToken);
        ServiceResult<QuestSummary[]> tooShort = await client.SearchQuestsAsync("k", TestContext.Current.CancellationToken);
        ServiceResult<QuestDetail> unknown = await client.GetQuestAsync(999999999, TestContext.Current.CancellationToken);
        ServiceResult<QuestSummary[]> noZone = await client.GetQuestsAsync(999999, TestContext.Current.CancellationToken);

        Assert.Contains(found.Value!, q => q.Id == KoboldCampCleanup);
        Assert.Equal((int)HttpStatusCode.BadRequest, tooShort.Status);
        Assert.Equal((int)HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal((int)HttpStatusCode.BadRequest, noZone.Status);
    }

    // Forever's NPCs and nodes need no code of their own: the converter writes the files the
    // layers already read (known issue: Forever had none).
    [Fact]
    public async Task Forever_zones_list_their_npcs_nodes_and_mailboxes()
    {
        using PathingServiceFactory factory = new(TestData.RepoJson, Forever);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<NpcInfo[]> vendors = await client.GetNpcsAsync(ElwynnForest, "vendor", TestContext.Current.CancellationToken);
        ServiceResult<NpcInfo[]> innkeepers = await client.GetNpcsAsync(ElwynnForest, "innkeeper", TestContext.Current.CancellationToken);
        int[] counts = new int[SpawnLayers.All.Length];
        for (int i = 0; i < counts.Length; i++)
        {
            ServiceResult<SpawnPoint[]> layer = await client.GetSpawnsAsync(ElwynnForest, SpawnLayers.All[i], TestContext.Current.CancellationToken);
            Assert.True(layer.Ok, layer.Error);
            counts[i] = layer.Value!.Length;
        }

        Assert.True(vendors.Value!.Length > 10, $"{vendors.Value.Length} vendors");
        Assert.Contains(innkeepers.Value!, n => n.Name == "Innkeeper Farley");
        Assert.All(counts.Zip(SpawnLayers.All), c => Assert.True(c.First > 0, $"no {c.Second} in Elwynn"));
    }

    [Fact]
    public async Task A_client_without_quest_files_has_none()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<QuestSummary[]> elwynn = await client.GetQuestsAsync(ElwynnForest, TestContext.Current.CancellationToken);

        Assert.True(elwynn.Ok, elwynn.Error);
        Assert.Empty(elwynn.Value!);
    }
}
