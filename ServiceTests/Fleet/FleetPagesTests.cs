using ControlCenter.Bots;
using ControlCenter.Components.Layout;
using ControlCenter.Components.Pages;

using Contracts.Bots;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Bots;

using ServiceTests.Agent;
using ServiceTests.Api;
using ServiceTests.Bots;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

using Xunit;

using FleetPage = ControlCenter.Components.Pages.Fleet;

namespace ServiceTests.Fleet;

/// <summary>
/// The fleet page, a bot's console and the status bar, rendered as a circuit renders them (prerendering
/// is off): with the manager down, and with a manager that knows a bot. The manager is online while the
/// fleet feed is connected: its hub is an in-memory manager's, its bots the fake's.
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
        FleetFeed feed = host.Services.GetRequiredService<FleetFeed>();

        string fleet = await host.RenderAsync<FleetPage>();
        string console = await host.RenderAsync<BotConsole>(Bot(AgentId));
        string bar = await host.RenderAsync<StatusBar>();

        Assert.False(feed.Connected);
        foreach (string page in new[] { fleet, console })
        {
            Assert.Contains("The Bot Manager is offline", page);
            Assert.Contains(feed.Service.Url.ToString(), page);
            Assert.Contains("BotManager/run.sh", page);
            Assert.Contains(BotManagerOptions.UrlKey, page);
        }

        Assert.Contains("Bot Manager offline", bar);
    }

    [Fact]
    public async Task The_fleet_lists_a_bot_and_its_console_shows_it_and_refuses_a_bad_assignment_with_the_rules_reason()
    {
        BotInfo bot = Playing();
        BotLogLine line = new(1, 1, DateTimeOffset.UtcNow, "Warning", "ForeverRun", "No loot in reach");
        FakeBotManager manager = new([bot], [line]);
        using TempDataRoot data = new();
        using BotManagerFactory hub = new(data.Root);
        using ControlCenterFactory host = new(manager, fleetHub: hub.Server.CreateHandler);
        FleetFeed feed = host.Services.GetRequiredService<FleetFeed>();
        await Eventually.True(() => feed.Connected);

        string bar = WebUtility.HtmlDecode(await host.RenderAsync<StatusBar>());
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
        Assert.Contains("Bots <strong>1</strong> <span class=\"pa-faint\">of 1 online", bar);
        Assert.DoesNotContain("The Bot Manager is offline", fleet);
        Assert.Empty(manager.Calls);
    }

    [Fact]
    public async Task A_bot_known_before_the_manager_went_down_stays_under_the_notice()
    {
        BotInfo bot = Playing();
        FakeBotManager manager = new([bot]);
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        BotManagerFactory hub = slot.Bring(new BotManagerFactory(data.Root));
        using ControlCenterFactory host = new(manager, fleetHub: slot.Handler);
        FleetFeed feed = host.Services.GetRequiredService<FleetFeed>();
        await Eventually.True(() => feed.Connected);

        // The manager goes; the feed tries again, and says why it cannot connect.
        slot.Clear();
        hub.Dispose();
        await Eventually.True(() => feed.Error?.Contains("Connection refused (test)", StringComparison.Ordinal) == true);
        string fleet = WebUtility.HtmlDecode(await host.RenderAsync<FleetPage>());
        string console = WebUtility.HtmlDecode(await host.RenderAsync<BotConsole>(Bot(AgentId)));

        Assert.False(feed.Connected);
        foreach (string page in new[] { fleet, console })
        {
            Assert.Contains("The Bot Manager is offline", page);
            Assert.Contains("Connection refused (test)", page);
            Assert.Contains("pa-workspace is-stale", page);
            Assert.Contains(bot.Name, page);
        }
    }

    // Known issue 77: with BotManager:Url naming the Pathing Service, whose /health answered the manager's
    // poll as JSON with none of its members, the status bar showed a green "Bots 0 of 0 online".
    [Fact]
    public async Task A_manager_address_that_names_another_service_is_the_manager_offline()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory pathing = new(data.Root);
        using ControlCenterFactory host = new(new BotManagerClient(pathing.CreateClient()), fleetHub: pathing.Server.CreateHandler);
        FleetFeed feed = host.Services.GetRequiredService<FleetFeed>();

        await Eventually.True(() => feed.Error is { } why && why != FleetFeed.NotConnected);
        string bar = WebUtility.HtmlDecode(await host.RenderAsync<StatusBar>());

        Assert.False(feed.Connected);
        Assert.Contains("Bot Manager offline", bar);
        Assert.DoesNotContain("of 0 online", bar);
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
