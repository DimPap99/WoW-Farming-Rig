using BotAgent.Runtime;

using Contracts.Bots;

using HeadlessServer;
using HeadlessServer.Runtimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using System;
using System.IO;
using System.Threading;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// The runtimes refuse what they cannot run before anything attaches to a game: no client, the wrong
/// client, a route that is not there or would be refused. Nothing here needs a game; on a machine
/// without one, a run that gets as far as looking for the client is refused too.
/// </summary>
public sealed class RuntimeRefusalTests : IDisposable
{
    private const string RouteFile = "crossroads_trip.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), "AgentTests-" + Guid.NewGuid().ToString("N"));

    public RuntimeRefusalTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "dbc", "forever"));
        Directory.CreateDirectory(Path.Combine(root, "path", "_forever"));
        File.Copy(Path.Combine(RepoJson, "dbc", "forever", "WorldMapArea.json"), Path.Combine(root, "dbc", "forever", "WorldMapArea.json"));
        File.Copy(Path.Combine(RepoJson, "path", "_forever", RouteFile), Path.Combine(root, "path", "_forever", RouteFile));
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

    [Fact]
    public void Without_a_client_every_run_is_refused()
    {
        HeadlessRuntimeFactory factory = Factory(game: null);

        Assert.False(factory.TryCreate(BotAssignment.ForForever(RouteFile, 5), out _, out string refusal));
        Assert.Equal("no WoW client is running on this PC", refusal);
    }

    [Fact]
    public void A_run_for_the_other_client_is_refused()
    {
        HeadlessRuntimeFactory classic = Factory(new AgentGame("som", "precata", 1, "1.15.7.61582"));
        HeadlessRuntimeFactory forever = Factory(new AgentGame("forever", "forever", 1, "1.60.1.70009"));

        Assert.False(classic.TryCreate(BotAssignment.ForForever(RouteFile, 5), out _, out string notForever));
        Assert.False(forever.TryCreate(BotAssignment.ForClassic("Hunter_10.json"), out _, out string notClassic));
        Assert.Contains("needs WoW: Forever", notForever);
        Assert.Contains("needs a Classic client", notClassic);
    }

    [Fact]
    public void A_route_that_is_not_there_or_would_be_refused_never_runs()
    {
        AgentGame game = new("forever", "forever", 1, "1.60.1.70009");
        File.WriteAllText(Path.Combine(root, "path", "_forever", "broken.json"),
            """{ "UIMapId": 1411, "Steps": [ { "Travel": { "To": [152, 44] } } ] }""");

        Assert.False(ForeverRuntime.TryCreate(new ForeverAssignment("missing.json", 5), game, Data(), NullLoggerFactory.Instance,
            out _, out string missing));
        Assert.False(ForeverRuntime.TryCreate(new ForeverAssignment("broken.json", 5), game, Data(), NullLoggerFactory.Instance,
            out _, out string broken));
        Assert.Contains("not in this PC's", missing);
        Assert.Contains("step 1: the map point", broken);
    }

    [Fact]
    public void A_route_run_looks_for_the_client_before_anything_else()
    {
        AgentGame game = new("forever", "forever", int.MaxValue, "1.60.1.70009");

        Assert.True(ForeverRuntime.TryCreate(new ForeverAssignment(RouteFile, 5), game, Data(), NullLoggerFactory.Instance,
            out IBotRuntime runtime, out string refusal), refusal);
        using (runtime)
        {
            BotTelemetry before = runtime.Read();
            BotRunResult result = runtime.Run(CancellationToken.None);

            Assert.Equal(BotRuntimeKind.Forever, runtime.Kind);
            Assert.Equal($"route {RouteFile}", runtime.Summary);
            Assert.Equal(BotRunState.Starting, before.State);
            Assert.Equal(BotRunOutcome.Refused, result.Outcome);
            Assert.Equal("WowB is not running.", result.Message);
            runtime.ForceRelease();
        }
    }

    [Theory]
    // A class profile's path group: Newtonsoft reads it as points at 0, 0, 0.
    [InlineData("[ { \"PathFilename\": \"x.json\" } ]", "its entries start with \"PathFilename\"")]
    [InlineData("{ \"UIMapId\": 1411, \"Steps\": [] }", "not a list of points")]
    [InlineData("[ { \"X\": 1, ", "not JSON")]
    public void A_path_in_place_of_the_first_that_does_not_load_is_refused_before_anything_attaches(string text, string why)
    {
        Directory.CreateDirectory(Path.Combine(root, "class"));
        File.WriteAllText(Path.Combine(root, "class", "Hunter.json"), "{}");
        File.WriteAllText(Path.Combine(root, "path", "override.json"), text);
        AgentGame game = new("som", "precata", 1, "1.15.7.61582");
        DataConfig data = new() { Root = root, Exp = "som" };

        // Known issue 68: the bot turned on with no GOAP thread, and the run said Running.
        Assert.False(ClassicRuntime.TryCreate(new ClassicAssignment("Hunter.json", "override.json"), game, data, new RunOptions(),
            new ConfigurationBuilder().Build(), NullLoggerFactory.Instance, out _, out string refusal));
        Assert.Contains("the path override.json is not a recorded path", refusal);
        Assert.Contains(why, refusal);
    }

    [Fact]
    public void The_probe_finds_no_client_where_none_runs()
    {
        Assert.Null(new WowGameProbe(new RunOptions { Pid = int.MaxValue }, NullLogger<WowGameProbe>.Instance).Probe());
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private DataConfig Data()
    {
        return new DataConfig { Root = root, Exp = "forever" };
    }

    private static HeadlessRuntimeFactory Factory(AgentGame? game)
    {
        return new HeadlessRuntimeFactory(new RunOptions(), new ConfigurationBuilder().Build(), new FixedProbe(game), NullLoggerFactory.Instance);
    }

    private sealed class FixedProbe(AgentGame? game) : IGameProbe
    {
        public AgentGame? Probe()
        {
            return game;
        }
    }
}
