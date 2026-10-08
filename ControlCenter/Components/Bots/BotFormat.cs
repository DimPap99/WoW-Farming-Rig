using ControlCenter.Components.Shared;
using ControlCenter.Pathing;

using Contracts.Pathing;

using Contracts.Bots;

using System;
using System.Globalization;

namespace ControlCenter.Components.Bots;

/// <summary>How the fleet pages put a bot's state into words and styles, alike on every page.</summary>
public static class BotFormat
{
    /// <summary>The state in words and its dot's class; an offline bot has neither run nor state.</summary>
    public static (string Text, string Dot) State(BotInfo bot)
    {
        if (!bot.Online)
        {
            return ("Offline", string.Empty);
        }

        return (bot.Latest?.State ?? BotRunState.Idle) switch
        {
            BotRunState.Syncing => ("Syncing data", "pa-dot--busy"),
            BotRunState.Starting => ("Starting", "pa-dot--busy"),
            BotRunState.WaitingForClick => ("Waiting for a click", "pa-dot--warn"),
            BotRunState.Running => ("Running", "pa-dot--live"),
            BotRunState.Stopping => ("Stopping", "pa-dot--busy"),
            _ => ("Idle", "pa-dot--ok"),
        };
    }

    /// <summary>What a person must know before anything else: the run waits for their click.</summary>
    public static string? WaitingForClick(BotInfo bot)
    {
        return bot is { Online: true, Latest.State: BotRunState.WaitingForClick }
            ? $"Waiting for a click: someone at {bot.Machine} must click into the game window. Nothing moves until then."
            : null;
    }

    /// <summary>When an offline bot was last seen, or that it is online.</summary>
    public static string Seen(BotInfo bot)
    {
        if (bot.Online)
        {
            return "online";
        }

        DateTimeOffset seen = bot.LastSeen.ToLocalTime();
        string format = seen.Date == DateTimeOffset.Now.Date ? "HH:mm" : "yyyy-MM-dd HH:mm";
        return "offline since " + seen.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>An agent's build: its version, and its commit cut to the 7 characters git shows.</summary>
    public static string Version(string version)
    {
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    /// <summary>The client, as its agent last saw it.</summary>
    public static string Client(AgentGame? game)
    {
        return game is null ? "no client running" : $"{game.Client} {game.Version}";
    }

    public static string Character(BotCharacter? character)
    {
        return character is null
            ? string.Empty
            : $"{character.Class} {character.Level.ToString(CultureInfo.InvariantCulture)}{(character.Race is null ? null : " " + character.Race)}";
    }

    /// <summary>
    /// A zone map position as a player reads it: the zone's name and its coordinates. The zone is named
    /// from the Pathing Service's world, so only for a bot that plays its era: another era's maps share its
    /// numbers, not its zones (known issue 77).
    /// </summary>
    public static string Place(BotPosition position, WorldSnapshot? world, AgentGame? game)
    {
        ZoneInfo? named = game is not null && world?.World.Era == game.Era ? world.Zone(position.UiMapId) : null;
        string zone = named?.Name ?? $"map {position.UiMapId.ToString(CultureInfo.InvariantCulture)}";
        return $"{zone} {position.MapX.ToString("F1", CultureInfo.InvariantCulture)}, {position.MapY.ToString("F1", CultureInfo.InvariantCulture)}";
    }

    public static string Progress(BotProgress progress)
    {
        return $"{Count(progress.Kills, "kill")} · {Count(progress.Deaths, "death")} · {Count(progress.Loots, "loot")}";
    }

    /// <summary>A share of 0 to 100 as a CSS width: invariant, or a decimal comma makes it invalid and it is ignored.</summary>
    public static string Percent(float value)
    {
        return Math.Clamp(value, 0, 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    public static string Time(DateTimeOffset at)
    {
        return at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string Duration(TimeSpan span)
    {
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span:mm\\:ss}"
            : span.ToString("m\\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>How a run's outcome is coloured.</summary>
    public static ResultKind Outcome(BotRunOutcome outcome)
    {
        return outcome switch
        {
            BotRunOutcome.Complete => ResultKind.Ok,
            BotRunOutcome.Stopped => ResultKind.Info,
            BotRunOutcome.Refused => ResultKind.Warn,
            _ => ResultKind.Error,
        };
    }

    /// <summary>A log line's level as the console's tag and class (Serilog's level names, as the agent sends them).</summary>
    public static (string Tag, string Css) Level(string level)
    {
        return level switch
        {
            "Verbose" => ("VRB", "pa-line--vrb"),
            "Debug" => ("DBG", "pa-line--dbg"),
            "Information" => ("INF", "pa-line--inf"),
            "Warning" => ("WRN", "pa-line--wrn"),
            "Error" => ("ERR", "pa-line--err"),
            "Fatal" => ("FTL", "pa-line--ftl"),
            _ => (level.Length > 3 ? level[..3].ToUpperInvariant() : level.ToUpperInvariant(), string.Empty),
        };
    }

    private static string Count(int count, string noun)
    {
        return $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";
    }
}
