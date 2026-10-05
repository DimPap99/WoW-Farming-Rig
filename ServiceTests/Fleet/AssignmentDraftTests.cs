using ControlCenter.Bots;

using Contracts.Bots;

using ServiceClients.Bots;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>The console's assignment form: a choice not made, or the manager's own reason, keeps a draft from being saved.</summary>
public sealed class AssignmentDraftTests
{
    private static readonly AgentGame Forever = new("forever", "forever", 1, "1.60.1.70009");
    private static readonly AgentGame Classic = new("som", "precata", 1, "1.15.7.61582");

    [Fact]
    public void A_new_draft_is_of_the_kind_the_bots_client_plays_and_asks_for_its_choice()
    {
        AssignmentDraft forever = AssignmentDraft.From(null, Forever);
        AssignmentDraft classic = AssignmentDraft.From(null, Classic);
        AssignmentDraft unknown = AssignmentDraft.From(null, null);

        Assert.Equal(BotRuntimeKind.Forever, forever.Kind);
        Assert.Equal(BotRuntimeKind.Classic, classic.Kind);
        Assert.Equal(BotRuntimeKind.Forever, unknown.Kind);
        Assert.NotNull(forever.Missing);
        Assert.NotNull(classic.Missing);
        Assert.Null(forever.Refusal);
        Assert.False(forever.Valid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BotManagerApi.MaxMinutes + 1)]
    public void A_time_bound_out_of_range_is_refused_with_the_managers_reason(int minutes)
    {
        AssignmentDraft draft = AssignmentDraft.From(null, Forever) with { Route = "crossroads_trip.json", Minutes = minutes };

        Assert.Null(draft.Missing);
        Assert.Equal(AssignmentRules.Problem(BotAssignment.ForForever("crossroads_trip.json", minutes)), draft.Refusal);
        Assert.Contains("minutes", draft.Refusal);
        Assert.False(draft.Valid);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("no_extension")]
    public void A_path_that_is_not_a_library_name_is_refused_with_the_managers_reason(string path)
    {
        AssignmentDraft draft = AssignmentDraft.From(null, Classic) with { Profile = "Hunter_10.json", Path = path };

        Assert.NotNull(draft.Refusal);
        Assert.Contains(path, draft.Refusal);
        Assert.False(draft.Valid);
    }

    [Fact]
    public void A_draft_gives_back_the_assignment_it_was_made_from()
    {
        BotAssignment forever = BotAssignment.ForForever("crossroads_trip.json", 45);
        BotAssignment classic = BotAssignment.ForClassic("Forever/Paladin_Forever_Auto.json", "Durotar/1_Valley.json");
        BotAssignment profileOnly = BotAssignment.ForClassic("Hunter_10.json");

        Assert.Equal(forever, AssignmentDraft.From(forever, Classic).Build());
        Assert.Equal(classic, AssignmentDraft.From(classic, Forever).Build());
        Assert.Equal(profileOnly, AssignmentDraft.From(profileOnly, null).Build());
        Assert.True(AssignmentDraft.From(classic, null).Valid);
        Assert.Equal(AssignmentDraft.From(forever, null), AssignmentDraft.From(forever, Classic));
    }
}
