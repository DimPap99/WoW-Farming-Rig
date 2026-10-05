using System;

namespace Contracts.Bots;

/// <summary>Which runtime a run uses.</summary>
public enum BotRuntimeKind
{
    /// <summary>The Classic bot: a class profile and its paths, through the addon's pixel strip.</summary>
    Classic,
    /// <summary>WoW: Forever's controller, a route, through the visual channel.</summary>
    Forever,
}

/// <summary>The WoW client an agent sees running on its PC.</summary>
/// <param name="Client">The client's data name (<c>som</c>, <c>forever</c>, ...).</param>
/// <param name="Era">Its navmesh and cost-zone era (<c>precata</c>, <c>forever</c>, ...).</param>
/// <param name="ProcessId">The game's process.</param>
/// <param name="Version">The executable's file version.</param>
public sealed record AgentGame(string Client, string Era, int ProcessId, string Version);

/// <summary>A Forever run: a route file from the library, for at most <paramref name="Minutes"/>.</summary>
/// <param name="Route">The route's file name in the library (<c>Json/path/_forever</c>).</param>
public sealed record ForeverAssignment(string Route, int Minutes);

/// <summary>A Classic run: a class profile, with its first path replaced when <paramref name="PathOverride"/> is set.</summary>
/// <param name="ClassProfile">The profile's name in <c>Json/class</c>, <c>/</c> between subfolders.</param>
/// <param name="PathOverride">A recorded path's library name.</param>
public sealed record ClassicAssignment(string ClassProfile, string? PathOverride);

/// <summary>What a bot runs when it is told to start: one of <see cref="Forever"/> or <see cref="Classic"/>, as <see cref="Kind"/> says.</summary>
public sealed record BotAssignment(BotRuntimeKind Kind, ForeverAssignment? Forever, ClassicAssignment? Classic)
{
    public static BotAssignment ForForever(string route, int minutes)
    {
        return new BotAssignment(BotRuntimeKind.Forever, new ForeverAssignment(route, minutes), null);
    }

    public static BotAssignment ForClassic(string classProfile, string? pathOverride = null)
    {
        return new BotAssignment(BotRuntimeKind.Classic, null, new ClassicAssignment(classProfile, pathOverride));
    }

    public override string ToString()
    {
        return Kind switch
        {
            BotRuntimeKind.Forever when Forever is { } forever => $"route {forever.Route} for at most {forever.Minutes} min",
            BotRuntimeKind.Classic when Classic is { } classic => classic.PathOverride is null
                ? $"profile {classic.ClassProfile}"
                : $"profile {classic.ClassProfile} on {classic.PathOverride}",
            _ => $"{Kind} (incomplete)",
        };
    }
}

/// <summary>A run under way: what it runs and since when.</summary>
/// <param name="RunId">The agent's id for the run.</param>
/// <param name="Summary">What it runs, for a person: the route or the profile.</param>
public sealed record BotRunInfo(string RunId, BotRuntimeKind Kind, string Summary, DateTimeOffset StartedAt);

/// <summary>What an agent says on every connection, before anything else.</summary>
/// <param name="AgentId">Its id: created on its first start and kept on its PC.</param>
/// <param name="Name">What to call it; the machine name unless configured.</param>
/// <param name="Protocol">The <see cref="BotManagerApi.AgentProtocol"/> it speaks.</param>
/// <param name="Run">The run under way, when it reconnects mid-run.</param>
/// <param name="ClassProfiles">The class profiles on its PC, for Classic assignments.</param>
public sealed record AgentHello(string AgentId, string Name, string Machine, int Protocol, string AgentVersion,
    AgentGame? Game, BotRunInfo? Run, string[] ClassProfiles);

/// <summary>The manager's answer to <see cref="AgentHello"/>: refused with why, or the bot's current assignment.</summary>
public sealed record AgentWelcome(bool Accepted, string? Reason, BotAssignment? Assignment);

public enum BotCommandKind
{
    /// <summary>Start the assignment the command carries.</summary>
    Start,
    /// <summary>Stop the run: release input first, then tear it down.</summary>
    Stop,
}

/// <summary>A command from the manager to one agent.</summary>
public sealed record BotCommand(string CommandId, BotCommandKind Kind, BotAssignment? Assignment);

/// <summary>An agent's answer to a <see cref="BotCommand"/>: taken, with the run it starts, or refused with why.</summary>
public sealed record CommandAck(bool Accepted, string? Reason, string? RunId);

/// <summary>Where a bot is.</summary>
public enum BotRunState
{
    /// <summary>No run; the agent waits for a command.</summary>
    Idle,
    /// <summary>Pulling the data its assignment needs from the Pathing Service.</summary>
    Syncing,
    /// <summary>Attaching to the client, loading the profile, waiting for the preflight.</summary>
    Starting,
    /// <summary>Waiting for a person to click into the game window; nothing has moved.</summary>
    WaitingForClick,
    /// <summary>The bot plays.</summary>
    Running,
    /// <summary>Releasing input and tearing the run down.</summary>
    Stopping,
}

/// <summary>The player a run controls.</summary>
public sealed record BotCharacter(string Class, int Level, string? Race);

/// <summary>Health and power, 0 to 100; the target's when there is one.</summary>
public sealed record BotVitals(float Health, float Power, float? Target, bool InCombat);

/// <summary>Where the player is: a zone map in the coordinates a player reads, 0 to 100.</summary>
public sealed record BotPosition(int UiMapId, float MapX, float MapY);

/// <summary>What a run has done so far.</summary>
public sealed record BotProgress(int Kills, int Deaths, int Loots);

/// <summary>
/// A bot's state, sent about once a second through read-only taps. Everything but
/// <see cref="State"/> is optional: an idle agent knows only its client.
/// </summary>
/// <param name="Activity">The goal or activity under way, as the run names it.</param>
/// <param name="Agenda">A Forever route's step and progress.</param>
/// <param name="Message">What the agent wants a person to know: why the last run ended, what it waits for.</param>
public sealed record BotSnapshot(string AgentId, DateTimeOffset At, BotRunState State, BotRunInfo? Run, AgentGame? Game,
    BotCharacter? Character, BotVitals? Vitals, BotPosition? Position, string? Activity, string? Agenda,
    BotProgress? Progress, string? Message);

/// <summary>A line the agent logged.</summary>
/// <param name="Level">Serilog's level name: Debug, Information, Warning, Error, Fatal.</param>
/// <param name="Source">The class that logged it.</param>
public sealed record BotLogLine(DateTimeOffset At, string Level, string Source, string Message);

/// <summary>How a run ended.</summary>
public enum BotRunOutcome
{
    /// <summary>The run did what it was for: a route's last step, or a repeating route's time bound.</summary>
    Complete,
    /// <summary>A stop command ended it.</summary>
    Stopped,
    /// <summary>It never moved: no client, the wrong client, no data, the preflight, no click in time.</summary>
    Refused,
    /// <summary>It moved and then failed, or ran out of time.</summary>
    Failed,
    /// <summary>It did not end within the stop's grace period; its input was released and the agent restarts.</summary>
    Abandoned,
}

/// <summary>An ended run, as the agent reports it.</summary>
public sealed record RunReport(BotRunInfo Run, BotRunOutcome Outcome, DateTimeOffset EndedAt, string Message,
    int? ExitCode, BotProgress? Progress);

/// <summary>An ended run of one bot, as the manager keeps it.</summary>
public sealed record RunRecord(string AgentId, RunReport Report);

/// <summary>A bot as the manager knows it: what it said when it registered, its assignment and its latest state.</summary>
/// <param name="Online">Its agent is connected now.</param>
/// <param name="LastSeen">When it last registered or disconnected.</param>
/// <param name="Latest">Its latest snapshot while online.</param>
public sealed record BotInfo(string AgentId, string Name, string Machine, bool Online, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, int Protocol, string AgentVersion, AgentGame? Game, string[] ClassProfiles,
    BotAssignment? Assignment, BotRunInfo? Run, BotSnapshot? Latest);

/// <summary>The manager's state, for a status bar.</summary>
public sealed record ManagerHealth(int ApiVersion, int AgentProtocol, int Bots, int Online);
