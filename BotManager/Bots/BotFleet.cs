using BotManager.Agents;
using BotManager.Fleet;
using BotManager.Registry;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BotManager.Bots;

/// <summary>
/// The bots: what the registry keeps about them, which agents are connected now and over which
/// connection, their latest snapshot and log, and the commands sent to them. Every change is sent
/// on to the fleet hub's listeners (the Control Center). An agent is reached only over the
/// connection it opened; a bot whose agent is not connected is offline, and commands to it are
/// refused rather than queued.
/// </summary>
public sealed partial class BotFleet(IBotRegistry registry, IHubContext<AgentHub> agents, IHubContext<FleetHub> fleet,
    BotFleetOptions options, TimeProvider clock, ILogger<BotFleet> logger)
{

    /// <summary>Log lines kept per bot, for a console opened late.</summary>
    public const int KeptLogLines = 500;

    public const int MaxNameLength = 64;

    /// <summary>More class profiles than any PC holds; a guard against runaway hellos.</summary>
    public const int MaxClassProfiles = 2000;

    /// <summary>The longest text a report or a log line from an agent keeps: a guard against runaway messages.</summary>
    public const int MaxMessageLength = 4000;

    private readonly Lock sync = new();
    private readonly Dictionary<string, LiveBot> online = [];
    private readonly Dictionary<string, string> agentByConnection = [];
    private readonly Dictionary<string, Queue<BotLogLine>> logs = [];

    public int OnlineCount
    {
        get
        {
            lock (sync)
            {
                return online.Count;
            }
        }
    }

    public int Count => registry.List().Count;

    // --- from agents -----------------------------------------------------------------------

    public async Task<AgentWelcome> RegisterAsync(string connectionId, AgentHello hello)
    {
        if (hello.Protocol != BotManagerApi.AgentProtocol)
        {
            LogRefused(logger, Printable(Cap(hello.AgentId ?? string.Empty)), $"protocol {hello.Protocol}");
            return new AgentWelcome(false,
                $"this manager speaks agent protocol {BotManagerApi.AgentProtocol} and the agent {hello.Protocol}: update the older one", null);
        }

        if (!AssignmentRules.IsAgentId(hello.AgentId))
        {
            LogRefused(logger, Printable(Cap(hello.AgentId ?? string.Empty)), "its id");
            return new AgentWelcome(false, $"'{hello.AgentId}' is not an agent id: letters, digits, - and _", null);
        }

        DateTimeOffset now = clock.GetUtcNow();
        BotRecord record;
        lock (sync)
        {
            BotRecord? known = registry.Get(hello.AgentId);
            record = new BotRecord(hello.AgentId, Clean(hello.Name, hello.Machine, hello.AgentId), Clean(hello.Machine, string.Empty),
                known?.FirstSeen ?? now, now, hello.Protocol, Clean(hello.AgentVersion, string.Empty), hello.Game,
                [.. (hello.ClassProfiles ?? []).Where(AssignmentRules.IsRelativeName).Take(MaxClassProfiles)], known?.Assignment);
            // An agent that registers is online whether or not its record could be written.
            Record(record);

            // A reconnect may reach the manager before the old connection's end does: the newest wins.
            if (online.Remove(hello.AgentId, out LiveBot? stale))
            {
                agentByConnection.Remove(stale.ConnectionId);
            }
            online[hello.AgentId] = new LiveBot(connectionId) { Run = hello.Run, Game = hello.Game };
            agentByConnection[connectionId] = hello.AgentId;
        }

        LogRegistered(logger, record.Name, record.AgentId, Printable(hello.Run?.Summary ?? "idle"));
        await fleet.Clients.All.SendAsync(FleetHubMethods.BotChanged, Info(record));
        return new AgentWelcome(true, null, record.Assignment);
    }

    public async Task SnapshotAsync(string connectionId, BotSnapshot snapshot)
    {
        BotSnapshot latest;
        lock (sync)
        {
            if (!TryLive(connectionId, out string agentId, out LiveBot? live))
            {
                return;
            }

            // The connection says who sent it, whatever the snapshot claims.
            latest = snapshot with { AgentId = agentId };
            live.Latest = latest;
            live.Run = latest.Run;
            live.Game = latest.Game ?? live.Game;
        }

        await fleet.Clients.All.SendAsync(FleetHubMethods.Snapshot, latest);
    }

    public async Task LogAsync(string connectionId, BotLogLine[] lines)
    {
        string agentId;
        BotLogLine[] kept;
        lock (sync)
        {
            if (!TryLive(connectionId, out agentId, out _) || lines is null || lines.Length == 0)
            {
                return;
            }

            kept = [.. (lines.Length > KeptLogLines ? lines[^KeptLogLines..] : lines).Select(line => line with { Message = Cap(line.Message) })];
            if (!logs.TryGetValue(agentId, out Queue<BotLogLine>? log))
            {
                logs[agentId] = log = new Queue<BotLogLine>();
            }

            foreach (BotLogLine line in kept)
            {
                log.Enqueue(line);
            }

            while (log.Count > KeptLogLines)
            {
                log.Dequeue();
            }
        }

        await fleet.Clients.All.SendAsync(FleetHubMethods.Log, agentId, kept);
    }

    public async Task RunEndedAsync(string connectionId, RunReport report)
    {
        report = report with { Message = Cap(report.Message), Run = report.Run with { Summary = Cap(report.Run.Summary) } };
        string agentId;
        lock (sync)
        {
            if (!TryLive(connectionId, out agentId, out LiveBot? live))
            {
                return;
            }

            if (live.Run?.RunId == report.Run.RunId)
            {
                live.Run = null;
            }
        }

        // A write that fails throws to the agent, which keeps the report and sends it again.
        registry.AddRun(agentId, report);
        LogRunEnded(logger, agentId, Printable(report.Run.Summary), report.Outcome, Printable(report.Message));
        await fleet.Clients.All.SendAsync(FleetHubMethods.RunEnded, new RunRecord(agentId, report));
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        BotRecord? record;
        lock (sync)
        {
            if (!agentByConnection.Remove(connectionId, out string? agentId) ||
                !online.TryGetValue(agentId, out LiveBot? live) || live.ConnectionId != connectionId)
            {
                return;
            }

            online.Remove(agentId);
            record = registry.Get(agentId);
            if (record is not null)
            {
                record = record with { LastSeen = clock.GetUtcNow(), Game = live.Game ?? record.Game };
                Record(record);
            }
        }

        if (record is not null)
        {
            LogOffline(logger, record.Name, record.AgentId);
            await fleet.Clients.All.SendAsync(FleetHubMethods.BotChanged, Info(record));
        }
    }

    // --- from the Control Center ------------------------------------------------------------

    public IReadOnlyList<BotInfo> List()
    {
        return [.. registry.List().OrderBy(bot => bot.Name, StringComparer.OrdinalIgnoreCase).Select(Info)];
    }

    public BotInfo? Get(string agentId)
    {
        return registry.Get(agentId) is { } record ? Info(record) : null;
    }

    public async Task<FleetResult> AssignAsync(string agentId, BotAssignment? assignment)
    {
        if (assignment is not null && AssignmentRules.Problem(assignment) is string problem)
        {
            return FleetResult.Invalid(problem);
        }

        BotRecord? record;
        string? connection;
        lock (sync)
        {
            record = registry.Get(agentId);
            if (record is null)
            {
                return FleetResult.NotFound;
            }

            record = record with { Assignment = assignment };
            try
            {
                registry.Save(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing changed: the bot keeps the assignment it had.
                LogNotRecorded(logger, record.Name, ex.Message);
                return FleetResult.Failed($"the assignment was not saved: {ex.Message}");
            }

            connection = online.GetValueOrDefault(agentId)?.ConnectionId;
        }

        if (connection is not null)
        {
            await agents.Clients.Client(connection).SendAsync(AgentHubMethods.Assign, assignment);
        }

        await fleet.Clients.All.SendAsync(FleetHubMethods.BotChanged, Info(record));
        return FleetResult.Done;
    }

    public Task<FleetResult<CommandAck>> StartAsync(string agentId, CancellationToken token)
    {
        return CommandAsync(agentId, BotCommandKind.Start, token);
    }

    public Task<FleetResult<CommandAck>> StopAsync(string agentId, CancellationToken token)
    {
        return CommandAsync(agentId, BotCommandKind.Stop, token);
    }

    public async Task<FleetResult> ForgetAsync(string agentId)
    {
        lock (sync)
        {
            if (online.ContainsKey(agentId))
            {
                return FleetResult.Conflict("the bot is online; stop its agent first");
            }

            try
            {
                if (!registry.Remove(agentId))
                {
                    return FleetResult.NotFound;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNotRecorded(logger, agentId, ex.Message);
                return FleetResult.Failed($"the bot was not forgotten: {ex.Message}");
            }

            logs.Remove(agentId);
        }

        await fleet.Clients.All.SendAsync(FleetHubMethods.BotRemoved, agentId);
        return FleetResult.Done;
    }

    /// <summary>The bot's latest log lines, oldest first; null for a bot the manager does not know.</summary>
    public BotLogLine[]? Log(string agentId)
    {
        lock (sync)
        {
            if (registry.Get(agentId) is null)
            {
                return null;
            }

            return logs.TryGetValue(agentId, out Queue<BotLogLine>? log) ? [.. log] : [];
        }
    }

    /// <summary>The bot's ended runs, newest first; null for a bot the manager does not know.</summary>
    public RunRecord[]? Runs(string agentId)
    {
        return registry.Get(agentId) is null ? null : [.. registry.Runs(agentId).Select(run => new RunRecord(agentId, run))];
    }

    private async Task<FleetResult<CommandAck>> CommandAsync(string agentId, BotCommandKind kind, CancellationToken token)
    {
        BotRecord? record;
        string? connection;
        lock (sync)
        {
            record = registry.Get(agentId);
            connection = online.GetValueOrDefault(agentId)?.ConnectionId;
        }

        if (record is null)
        {
            return FleetResult<CommandAck>.NotFound;
        }

        if (connection is null)
        {
            return FleetResult<CommandAck>.Conflict($"{record.Name} is offline: its agent is not connected");
        }

        if (kind == BotCommandKind.Start && record.Assignment is null)
        {
            return FleetResult<CommandAck>.Conflict($"{record.Name} has no assignment to start");
        }

        BotCommand command = new(Guid.NewGuid().ToString("N"), kind, kind == BotCommandKind.Start ? record.Assignment : null);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.CommandTimeout);
        CommandAck ack;
        try
        {
            ack = await agents.Clients.Client(connection).InvokeAsync<CommandAck>(AgentHubMethods.Command, command, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HubException or InvalidOperationException)
        {
            // SignalR reports a cancelled invocation as a HubException, so the tokens tell what happened.
            token.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested
                ? FleetResult<CommandAck>.Timeout($"{record.Name}'s agent did not answer within {options.CommandTimeout.TotalSeconds:0} s")
                : FleetResult<CommandAck>.Conflict($"{record.Name}'s agent could not take the command: {ex.Message}");
        }

        LogCommand(logger, kind, record.Name, ack.Accepted, Printable(ack.Reason ?? ack.RunId ?? string.Empty));
        return ack.Accepted
            ? FleetResult<CommandAck>.Done(ack)
            : FleetResult<CommandAck>.Conflict(ack.Reason ?? $"{record.Name} refused to {kind.ToString().ToLowerInvariant()}");
    }

    private bool TryLive(string connectionId, out string agentId, out LiveBot live)
    {
        if (agentByConnection.TryGetValue(connectionId, out string? id) && online.TryGetValue(id, out LiveBot? found))
        {
            agentId = id;
            live = found;
            return true;
        }

        agentId = string.Empty;
        live = null!;
        return false;
    }

    private BotInfo Info(BotRecord record)
    {
        lock (sync)
        {
            LiveBot? live = online.GetValueOrDefault(record.AgentId);
            return new BotInfo(record.AgentId, record.Name, record.Machine, live is not null, record.FirstSeen, record.LastSeen,
                record.Protocol, record.AgentVersion, live?.Game ?? record.Game, record.ClassProfiles, record.Assignment,
                live?.Run, live?.Latest);
        }
    }

    private static string Clean(string? value, string fallback, string? second = null)
    {
        string chosen = !string.IsNullOrWhiteSpace(value) ? value.Trim()
            : !string.IsNullOrWhiteSpace(fallback) ? fallback.Trim()
            : second ?? string.Empty;
        chosen = string.Concat(chosen.Select(c => char.IsControl(c) ? ' ' : c));
        return chosen.Length > MaxNameLength ? chosen[..MaxNameLength] : chosen;
    }

    // Under the lock: written when it can be, kept in memory either way, as live state is.
    private void Record(BotRecord record)
    {
        try
        {
            registry.Save(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            registry.Keep(record);
            LogNotRecorded(logger, record.Name, ex.Message);
        }
    }

    private static string Cap(string text)
    {
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    // An agent's text in the manager's own log: a line break would forge a line of its own.
    private static string Printable(string text)
    {
        return text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private sealed class LiveBot(string connectionId)
    {
        public string ConnectionId { get; } = connectionId;
        public BotRunInfo? Run { get; set; }
        public AgentGame? Game { get; set; }
        public BotSnapshot? Latest { get; set; }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} ({AgentId}) registered: {Run}")]
    static partial void LogRegistered(ILogger logger, string name, string agentId, string run);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} refused: {What}")]
    static partial void LogRefused(ILogger logger, string agentId, string what);

    [LoggerMessage(Level = LogLevel.Error, Message = "The registry could not be written for {Name}: {Reason}")]
    static partial void LogNotRecorded(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} ({AgentId}) went offline")]
    static partial void LogOffline(ILogger logger, string name, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} to {Name}: accepted={Accepted} {Detail}")]
    static partial void LogCommand(ILogger logger, BotCommandKind kind, string name, bool accepted, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{AgentId}: {Run} ended {Outcome}: {Message}")]
    static partial void LogRunEnded(ILogger logger, string agentId, string run, BotRunOutcome outcome, string message);

    #endregion
}
