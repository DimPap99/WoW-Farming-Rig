using BotManager.Registry;

using Contracts.Bots;

using Microsoft.Extensions.Logging.Abstractions;

using ServiceTests.Api;

using System;
using System.IO;
using System.Linq;

using Xunit;

namespace ServiceTests.Bots;

/// <summary>
/// The registry keeps its bots (known issue 72): a read that failed for a lock started it empty, and
/// the first registration then wrote over every other bot; a write that failed changed memory anyway.
/// </summary>
public sealed class JsonBotRegistryTests
{
    private const string First = "3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f";
    private const string Second = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static readonly BotAssignment Trip = BotAssignment.ForForever("crossroads_trip.json", 30);

    [Fact]
    public void A_bots_file_that_cannot_be_read_stops_the_start_and_stays_as_it_is()
    {
        SkipWithoutUnixRights();
        using TempDataRoot data = new();
        new JsonBotRegistry(data.Root, NullLogger<JsonBotRegistry>.Instance).Save(Bot(First));
        string bots = Path.Combine(data.Root, JsonBotRegistry.BotsFile);
        byte[] before = File.ReadAllBytes(bots);
        File.SetUnixFileMode(bots, UnixFileMode.None);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => new JsonBotRegistry(data.Root, NullLogger<JsonBotRegistry>.Instance));
        }
        finally
        {
            File.SetUnixFileMode(bots, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(before, File.ReadAllBytes(bots));
        Assert.Empty(Directory.GetFiles(data.Root, "*.unreadable-*"));
    }

    [Fact]
    public void A_damaged_bots_file_is_kept_aside_and_the_registry_starts_without_it()
    {
        using TempDataRoot data = new();
        File.WriteAllText(Path.Combine(data.Root, JsonBotRegistry.BotsFile), "{ not json");

        JsonBotRegistry registry = new(data.Root, NullLogger<JsonBotRegistry>.Instance);

        Assert.Empty(registry.List());
        Assert.Single(Directory.GetFiles(data.Root, JsonBotRegistry.BotsFile + ".unreadable-*"));
    }

    [Fact]
    public void A_write_that_fails_changes_nothing()
    {
        SkipWithoutUnixRights();
        using TempDataRoot data = new();
        JsonBotRegistry registry = new(data.Root, NullLogger<JsonBotRegistry>.Instance);
        registry.Save(Bot(First));
        File.SetUnixFileMode(data.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => registry.Save(Bot(First) with { Assignment = Trip }));
            Assert.ThrowsAny<UnauthorizedAccessException>(() => registry.Save(Bot(Second)));
            Assert.ThrowsAny<UnauthorizedAccessException>(() => registry.Remove(First));
        }
        finally
        {
            File.SetUnixFileMode(data.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Null(registry.Get(First)!.Assignment);
        Assert.Null(registry.Get(Second));
        Assert.Equal([First], registry.List().Select(bot => bot.AgentId));
    }

    [Fact]
    public void A_record_kept_while_the_disk_failed_is_written_with_the_next_save()
    {
        using TempDataRoot data = new();
        JsonBotRegistry registry = new(data.Root, NullLogger<JsonBotRegistry>.Instance);

        registry.Keep(Bot(First));
        registry.Save(Bot(Second));

        string[] reread = [.. new JsonBotRegistry(data.Root, NullLogger<JsonBotRegistry>.Instance).List().Select(bot => bot.AgentId)];
        Assert.Equal([Second, First], reread);
    }

    [Fact]
    public void A_report_sent_again_is_kept_once()
    {
        using TempDataRoot data = new();
        JsonBotRegistry registry = new(data.Root, NullLogger<JsonBotRegistry>.Instance);
        BotRunInfo run = new("run1", BotRuntimeKind.Forever, "route trip.json", DateTimeOffset.UtcNow);
        RunReport report = new(run, BotRunOutcome.Stopped, DateTimeOffset.UtcNow, "stopped", null, null);

        registry.AddRun(First, report);
        registry.AddRun(First, report);

        Assert.Single(registry.Runs(First));
    }

    private static BotRecord Bot(string agentId)
    {
        return new BotRecord(agentId, "PC " + agentId[..4], "BOX", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BotManagerApi.AgentProtocol,
            "test", null, [], null);
    }

    // File rights are Unix's here; and root reads and writes whatever its rights say.
    private static void SkipWithoutUnixRights()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Skip("needs Unix file rights that bind the user running the tests");
        }
    }
}
