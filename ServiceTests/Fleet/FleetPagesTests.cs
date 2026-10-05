using ControlCenter.Bots;
using ControlCenter.Components.Pages;

using Contracts.Bots;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Bots;

using ServiceTests.Api;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

using Xunit;

using FleetPage = ControlCenter.Components.Pages.Fleet;

namespace ServiceTests.Fleet;

/// <summary>
/// The fleet page and a bot's console, rendered as a circuit renders them (prerendering is off):
/// with the manager down, and with a manager that knows a bot.
/// </summary>
public sealed class FleetPagesTests
{
    private const string AgentId = "0f4e2d6c8a1b3e5d7f9a0c2e4b6d8f1a";

    // Stored before AssignmentRules lowered the bound, say: the form says why it cannot be saved as it is.
    private static readonly BotAssignment TooLong = BotAssignment.ForForever("crossroads_trip.json", BotManagerApi.MaxMinutes + 300);

    private static readonly BotRunInfo Run = new("run-9", BotRuntimeKind.Forever, TooLong.ToString(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task With_the_manager_down_both_pages_say_so_and_how_to_start_it()
    {
        using ControlCenterFactory host = new();
        BotManagerStatus status = host.Services.GetRequiredService<BotManagerStatus>();
        await status.RefreshAsync(TestContext.Current.CancellationToken);

        string fleet = await host.RenderAsync<FleetPage>();
        string console = await host.RenderAsync<BotConsole>(Bot(AgentId));

        Assert.False(status.Online);
        foreach (string page in new[] { fleet, console })
        {
            Assert.Contains("The Bot Manager is offline", page);
            Assert.Contains(status.Service.Url.ToString(), page);
            Assert.Contains("BotManager/run.sh", page);
            Assert.Contains(BotManagerOptions.UrlKey, page);
        }
    }

    [Fact]
    public async Task The_fleet_lists_a_bot_and_its_console_shows_it_and_refuses_a_bad_assignment_with_the_rules_reason()
    {
        BotInfo bot = Playing();
        BotLogLine line = new(DateTimeOffset.UtcNow, "Warning", "ForeverRun", "No loot in reach");
        FakeBotManager manager = new([bot], [line]);
        using ControlCenterFactory host = new(manager);
        await host.Services.GetRequiredService<BotManagerStatus>().RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(await host.Services.GetRequiredService<FleetFeed>().ResyncAsync(TestContext.Current.CancellationToken));

        string fleet = WebUtility.HtmlDecode(await host.RenderAsync<FleetPage>());
        string console = WebUtility.HtmlDecode(await host.RenderAsync<BotConsole>(Bot(AgentId)));

        Assert.Contains("1 of 1 bot online", fleet);
        Assert.Contains(bot.Name, fleet);
        Assert.Contains(BotConsole.Href(AgentId), fleet);
        Assert.Contains("Waiting for a click: someone at GAMEPC-2 must click into the game window", fleet);
        Assert.Contains(TooLong.ToString(), fleet);
        Assert.Contains("12 kills · 1 death · 9 loots", fleet);

        Assert.Contains(bot.Name, console);
        Assert.Contains("Hunter 12 Orc", console);
        Assert.Contains(line.Message, console);
        Assert.Contains($"Not valid: {AssignmentRules.Problem(TooLong)}", console);
        Assert.Empty(manager.Calls);
    }

    [Fact]
    public async Task A_bot_known_before_the_manager_went_down_stays_under_the_notice()
    {
        BotInfo bot = Playing();
        FakeBotManager manager = new([bot]);
        using ControlCenterFactory host = new(manager);
        BotManagerStatus status = host.Services.GetRequiredService<BotManagerStatus>();
        Assert.True(await host.Services.GetRequiredService<FleetFeed>().ResyncAsync(TestContext.Current.CancellationToken));

        manager.Down = true;
        await status.RefreshAsync(TestContext.Current.CancellationToken);
        string fleet = WebUtility.HtmlDecode(await host.RenderAsync<FleetPage>());
        string console = WebUtility.HtmlDecode(await host.RenderAsync<BotConsole>(Bot(AgentId)));

        Assert.False(status.Online);
        foreach (string page in new[] { fleet, console })
        {
            Assert.Contains("The Bot Manager is offline", page);
            Assert.Contains(FakeBotManager.Unreachable, page);
            Assert.Contains("pa-workspace is-stale", page);
            Assert.Contains(bot.Name, page);
        }
    }

    private static Dictionary<string, object?> Bot(string agentId)
    {
        return new() { [nameof(BotConsole.AgentId)] = agentId };
    }

    private static BotInfo Playing()
    {
        AgentGame game = new("forever", "forever", 4242, "1.60.1.70009");
        BotSnapshot latest = new(AgentId, DateTimeOffset.UtcNow, BotRunState.WaitingForClick, Run, game,
            new BotCharacter("Hunter", 12, "Orc"), new BotVitals(82.5f, 40, null, false), new BotPosition(1413, 51.9f, 29.9f),
            "Task/Travel", "task 1 of 2", new BotProgress(12, 1, 9), null);
        return new BotInfo(AgentId, "Hunter box", "GAMEPC-2", true, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            BotManagerApi.AgentProtocol, "agent 1.0", game, ["Hunter_10.json"], TooLong, Run, latest);
    }
}
