using Contracts.Bots;

using ServiceClients.Bots;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>What makes an assignment complete and its names safe to read files by.</summary>
public sealed class AssignmentRulesTests
{
    [Fact]
    public void Complete_assignments_pass()
    {
        Assert.Null(AssignmentRules.Problem(BotAssignment.ForForever("crossroads_trip.json", 30)));
        Assert.Null(AssignmentRules.Problem(BotAssignment.ForForever("loop.json", BotManagerApi.MaxMinutes)));
        Assert.Null(AssignmentRules.Problem(BotAssignment.ForClassic("Hunter_10.json")));
        Assert.Null(AssignmentRules.Problem(BotAssignment.ForClassic("Forever/Paladin_Forever_Auto.json", "Elwynn/route 1.json")));
    }

    [Theory]
    [InlineData("sub/trip.json", 30, "route file name")]
    [InlineData("trip.txt", 30, "route file name")]
    [InlineData("../trip.json", 30, "route file name")]
    [InlineData("trip.json", 0, "1 to")]
    [InlineData("trip.json", 601, "1 to")]
    public void A_forever_assignment_needs_a_plain_route_and_a_bound(string route, int minutes, string why)
    {
        Assert.Contains(why, AssignmentRules.Problem(BotAssignment.ForForever(route, minutes)));
    }

    [Theory]
    [InlineData("../Hunter.json", null)]
    [InlineData("/etc/Hunter.json", null)]
    [InlineData("class\\Hunter.json", null)]
    [InlineData("Hunter.json", "../escape.json")]
    [InlineData("Hunter.json", "a//b.json")]
    public void A_classic_assignment_names_files_inside_their_folders(string profile, string? path)
    {
        Assert.NotNull(AssignmentRules.Problem(BotAssignment.ForClassic(profile, path)));
    }

    [Fact]
    public void An_assignment_carries_its_kind_only()
    {
        Assert.NotNull(AssignmentRules.Problem(null));
        Assert.NotNull(AssignmentRules.Problem(new BotAssignment(BotRuntimeKind.Forever, null, null)));
        Assert.NotNull(AssignmentRules.Problem(new BotAssignment(BotRuntimeKind.Classic, new ForeverAssignment("trip.json", 5),
            new ClassicAssignment("Hunter.json", null))));
    }

    [Theory]
    [InlineData("3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f", true)]
    [InlineData("agent-1_b", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    public void Agent_ids_are_safe_file_names(string id, bool valid)
    {
        Assert.Equal(valid, AssignmentRules.IsAgentId(id));
    }
}
