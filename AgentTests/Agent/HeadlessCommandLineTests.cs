using CommandLine;

using HeadlessServer;

using System.Linq;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// HeadlessServer's command line: configuration keys reach the configuration and never the parser,
/// which used to refuse them; a class profile, or --agent, is needed.
/// </summary>
public sealed class HeadlessCommandLineTests
{
    [Fact]
    public void Configuration_keys_go_to_the_configuration_and_the_rest_to_the_parser()
    {
        string[] args = ["Warrior_1.json", "--Pathing:Engine=Navmesh", "-m", "Local", "--BotManager:Url", "http://botbox:5030", "--agent"];

        Assert.Equal(["Warrior_1.json", "-m", "Local", "--agent"], HeadlessArguments.ForParser(args));
        Assert.Equal(["--Pathing:Engine=Navmesh", "--BotManager:Url", "http://botbox:5030"], HeadlessArguments.ForConfiguration(args));
    }

    [Theory]
    [InlineData("--Agent:Name=PC1", true)]
    [InlineData("--Navmesh:Query:EdgeMargin", true)]
    [InlineData("--agent", false)]
    [InlineData("--mode", false)]
    [InlineData("--:x", false)]
    [InlineData("Warrior_1.json", false)]
    public void A_configuration_key_has_a_colon_in_its_name(string arg, bool key)
    {
        Assert.Equal(key, HeadlessArguments.IsConfigurationKey(arg));
    }

    [Fact]
    public void A_configuration_key_no_longer_fails_the_parse()
    {
        ParserResult<RunOptions> parsed = new Parser().ParseArguments<RunOptions>(
            HeadlessArguments.ForParser(["Warrior_1.json", "--Pathing:Engine=Navmesh"]));

        Assert.Equal(ParserResultType.Parsed, parsed.Tag);
        Assert.Equal("Warrior_1.json", parsed.Value.ClassConfig);
    }

    [Fact]
    public void A_class_profile_or_agent_is_needed()
    {
        RunOptions none = Parse();
        RunOptions agent = Parse("--agent", "--manager", "http://botbox:5030", "--name", "PC1", "--pathing-service", "http://botbox:5010");
        RunOptions profile = Parse("Warrior_1.json");
        RunOptions loadOnlyAgent = Parse("--agent", "--loadonly");

        Assert.NotNull(none.Problem);
        Assert.Null(agent.Problem);
        Assert.Null(profile.Problem);
        Assert.NotNull(loadOnlyAgent.Problem);
        Assert.Equal(["BotManager:Url=http://botbox:5030", "Agent:Name=PC1", "PathingService:Url=http://botbox:5010"],
            agent.ConfigurationValues().Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static RunOptions Parse(params string[] args)
    {
        return new Parser().ParseArguments<RunOptions>(args).Value;
    }
}
