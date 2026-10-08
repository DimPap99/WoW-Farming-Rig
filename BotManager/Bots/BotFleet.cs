using BotManager.Agents;
using BotManager.Fleet;
using BotManager.Hosting;
using BotManager.Registry;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;

using ServiceHosting;

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
/// on to the fleet hub's listeners (the Control Center), posted under the lock to one send loop
/// (<see cref="HubSendQueue"/>), so they get the changes in the order they happened and no agent
/// waits on a listener (known issue 73). An agent is reached only over the connection it opened; a
/// bot whose agent is not connected is offline, and commands to it are refused rather than queued.
/// </summary>
public sealed partial class BotFleet(JsonBotRegistry registry, IHubContext<AgentHub> agents, IHubContext<FleetHub> fleet,
    HubSendQueue sends, BotFleetOptions options, TimeProvider clock, ILogger<BotFleet> logger)
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
    private readonly Dictionary<string, List<BotLogLine>> logs = [];

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

    /// <summary>
    /// An agent's hello on <paramref name="connectionId"/>: refused with why, or the bot online over it.
    /// An id online from another machine is refused, naming it: two PCs with one id (a copied folder)
    /// would take the bot's place from each other, the one displaced playing on unseen (known issue 74).
    /// From the same machine the newer connection takes over, and the older one is closed
    /// (<paramref name="close"/>).
    /// </summary>
    public Task<AgentWelcome> RegisterAsync(string connectionId, AgentHello hello, Action close)
    {
        if (hello.Protocol != BotManagerApi.AgentProtocol)
        {
            LogRefused(logger, Printable(Cap(hello.AgentId ?? string.Empty)), $"protocol {hello.Protocol}");
            return Task.FromResult(new AgentWelcome(false,
                $"this manager speaks agent protocol {BotManagerApi.AgentProtocol} and the agent {hello.Protocol}: update the older one"));
        }

        if (!AssignmentRules.IsAgentId(hello.AgentId))
        {
            LogRefused(logger, Printable(Cap(hello.AgentId ?? string.Empty)), "its id");
            return Task.FromResult(new AgentWelcome(false, $"'{hello.AgentId}' is not an agent id: letters, digits, - and _"));
        }

        DateTimeOffset now = clock.GetUtcNow();
        string machine = Clean(hello.Machine, string.Empty);
        BotRecord record;
        LiveBot? displaced = null;
        lock (sync)
        {
            if (agentByConnection.TryGetValue(connectionId, out string? already) && already != hello.AgentId)
            {
                LogRefused(logger, hello.AgentId, $"its connection is agent {already} already");
                return Task.FromResult(new AgentWelcome(false, $"this connection is agent {already} already: one connection, one agent"));
            }

            if (online.TryGetValue(hello.AgentId, out LiveBot? other) && other.ConnectionId != connectionId &&
                !string.Equals(other.Machine, machine, StringComparison.OrdinalIgnoreCase))
            {
                LogRefused(logger, hello.AgentId, $"its id is online from {other.Machine}");
                return Task.FromResult(new AgentWelcome(false,
                    $"this agent's id is online from {other.Machine}: two PCs share one {BotManagerApi.AgentIdentityFile} (a copied folder). " +
                    $"Delete the file on one of them, which then takes a new id"));
            }

            BotRecord? known = registry.Get(hello.AgentId);
            // A hello without a client says only that none runs now: the last one seen is kept.
            record = new BotRecord(hello.AgentId, Clean(hello.Name, hello.Machine, hello.AgentId), machine, known?.FirstSeen ?? now, now,
                hello.Protocol, Clean(hello.AgentVersion, string.Empty), hello.Game ?? known?.Game,
                [.. (hello.ClassProfiles ?? []).Where(LibraryName.IsRelativeName).Take(MaxClassProfiles)], known?.Assignment);
            // An agent that registers is online whether or not its record could be written.
            Record(record);

            // A reconnect may reach the manager before the old connection's end does: the newest wins, and the old one is closed.
            if (online.Remove(hello.AgentId, out LiveBot? stale) && stale.ConnectionId != connectionId)
            {
                agentByConnection.Remove(stale.ConnectionId);
                displaced = stale;
            }

            online[hello.AgentId] = new LiveBot(connectionId, machine, close) { Run = hello.Run, Game = hello.Game };
            agentByConnection[connectionId] = hello.AgentId;
            Broadcast(FleetHubMethods.BotChanged, InfoOf(record));
        }

        displaced?.Close();

        LogRegistered(logger, record.Name, record.AgentId, Printable(hello.Run?.Summary ?? "idle"));
        return Task.FromResult(new AgentWelcome(true, null));
    }

    public Task SnapshotAsync(string connectionId, BotSnapshot snapshot)
    {
        lock (sync)
        {
            if (!TryLive(connectionId, out string agentId, out LiveBot? live))
            {
                return Task.CompletedTask;
            }

            // The connection says who sent it, whatever the snapshot claims.
            BotSnapshot latest = snapshot with { AgentId = agentId };
            live.Latest = latest;
            live.Run = latest.Run;
            live.Game = latest.Game ?? live.Game;
            Broadcast(FleetHubMethods.Snapshot, latest);
        }

        return Task.CompletedTask;
    }

    public Task LogAsync(string connectionId, BotLogLine[] lines)
    {
        lock (sync)
        {
            if (!TryLive(connectionId, out string agentId, out _) || lines is null || lines.Length == 0)
            {
                return Task.CompletedTask;
            }

            if (!logs.TryGetValue(agentId, out List<BotLogLine>? log))
            {
                logs[agentId] = log = [];
            }

            // A batch sent again (its acknowledgement lost, a host built again) is kept and passed on once.
            List<BotLogLine> fresh = [];
            BotLogLine? last = log.Count > 0 ? log[^1] : null;
            foreach (BotLogLine line in lines.TakeLast(KeptLogLines))
            {
                if (line is not null && (last is null || line.Follows(last)))
                {
                    fresh.Add(line with { Message = Cap(line.Message ?? string.Empty) });
                    last = line;
                }
            }

            if (fresh.Count == 0)
            {
                return Task.CompletedTask;
            }

            log.AddRange(fresh);
            if (log.Count > KeptLogLines)
            {
                log.RemoveRange(0, log.Count - KeptLogLines);
            }

            Broadcast(FleetHubMethods.Log, agentId, fresh.ToArray());
        }

        return Task.CompletedTask;
    }

    public Task RunEndedAsync(string connectionId, RunReport report)
    {
        report = report with { Message = Cap(report.Message), Run = report.Run with { Summary = Cap(report.Run.Summary) } };
        string agentId;
        lock (sync)
        {
            if (!TryLive(connectionId, out agentId, out LiveBot? live))
            {
                // Not taken: the agent keeps the report and sends it once it is registered again.
                throw new HubException("this connection is not a registered agent's: the report was not kept");
            }

            // A write that fails throws to the agent, which keeps the report and sends it again.
            registry.AddRun(agentId, report);
            if (live.Run?.RunId == report.Run.RunId)
            {
                live.Run = null;
            }

            Broadcast(FleetHubMethods.RunEnded, new RunRecord(agentId, report));
        }

        LogRunEnded(logger, agentId, Printable(report.Run.Summary), report.Outcome, Printable(report.Message));
        return Task.CompletedTask;
    }

    public Task DisconnectedAsync(string connectionId)
    {
        BotRecord? record;
        lock (sync)
        {
            if (!agentByConnection.Remove(connectionId, out string? agentId) ||
                !online.TryGetValue(agentId, out LiveBot? live) || live.ConnectionId != connectionId)
            {
                return Task.CompletedTask;
            }

            online.Remove(agentId);
            record = registry.Get(agentId);
            if (record is not null)
            {
                record = record with { LastSeen = clock.GetUtcNow(), Game = live.Game ?? record.Game };
                Record(record);
                Broadcast(FleetHubMethods.BotChanged, InfoOf(record));
            }
        }

        if (record is not null)
        {
            LogOffline(logger, record.Name, record.AgentId);
        }

        return Task.CompletedTask;
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

    /// <summary>Sets what the bot runs when it is told to start: kept here, and carried by the start command.</summary>
    public ApiResult Assign(string agentId, BotAssignment? assignment)
    {
        if (assignment is not null && AssignmentRules.Problem(assignment) is string problem)
        {
            return ApiResult.Invalid(problem);
        }

        lock (sync)
        {
            if (registry.Get(agentId) is not { } record)
            {
                return ApiResult.NotFound;
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
                return ApiResult.Unreadable($"the assignment was not saved: {ex.Message}");
            }

            Broadcast(FleetHubMethods.BotChanged, InfoOf(record));
        }

        return ApiResult.Done;
    }

    public Task<ApiResult<CommandAck>> StartAsync(string agentId, CancellationToken token)
    {
        return CommandAsync(agentId, BotCommandKind.Start, token);
    }

    public Task<ApiResult<CommandAck>> StopAsync(string agentId, CancellationToken token)
    {
        return CommandAsync(agentId, BotCommandKind.Stop, token);
    }

    public Task<ApiResult> ForgetAsync(string agentId)
    {
        lock (sync)
        {
            if (online.ContainsKey(agentId))
            {
                return Task.FromResult(ApiResult.Conflict("the bot is online; stop its agent first"));
            }

            try
            {
                if (!registry.Remove(agentId))
                {
                    return Task.FromResult(ApiResult.NotFound);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNotRecorded(logger, agentId, ex.Message);
                return Task.FromResult(ApiResult.Unreadable($"the bot was not forgotten: {ex.Message}"));
            }

            logs.Remove(agentId);
            Broadcast(FleetHubMethods.BotRemoved, agentId);
        }

        return Task.FromResult(ApiResult.Done);
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

            return logs.TryGetValue(agentId, out List<BotLogLine>? log) ? [.. log] : [];
        }
    }

    /// <summary>The bot's ended runs, newest first; null for a bot the manager does not know.</summary>
    public RunRecord[]? Runs(string agentId)
    {
        return registry.Get(agentId) is null ? null : [.. registry.Runs(agentId).Select(run => new RunRecord(agentId, run))];
    }

    private async Task<ApiResult<CommandAck>> CommandAsync(string agentId, BotCommandKind kind, CancellationToken token)
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
            return ApiResult<CommandAck>.NotFound;
        }

        if (connection is null)
        {
            return ApiResult<CommandAck>.Conflict($"{record.Name} is offline: its agent is not connected");
        }

        if (kind == BotCommandKind.Start && record.Assignment is null)
        {
            return ApiResult<CommandAck>.Conflict($"{record.Name} has no assignment to start");
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
                ? ApiResult<CommandAck>.Timeout($"{record.Name}'s agent did not answer within {options.CommandTimeout.TotalSeconds:0} s")
                : ApiResult<CommandAck>.Conflict($"{record.Name}'s agent could not take the command: {ex.Message}");
        }

        LogCommand(logger, kind, record.Name, ack.Accepted, Printable(ack.Reason ?? ack.RunId ?? string.Empty));
        return ack.Accepted
            ? ApiResult<CommandAck>.Done(ack)
            : ApiResult<CommandAck>.Conflict(ack.Reason ?? $"{record.Name} refused to {kind.ToString().ToLowerInvariant()}");
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
            return InfoOf(record);
        }
    }

    // Under the lock: the bot as the registry keeps it and as it is live now.
    private BotInfo InfoOf(BotRecord record)
    {
        LiveBot? live = online.GetValueOrDefault(record.AgentId);
        return new BotInfo(record.AgentId, record.Name, record.Machine, live is not null, record.FirstSeen, record.LastSeen,
            record.Protocol, record.AgentVersion, live?.Game ?? record.Game, record.ClassProfiles, record.Assignment,
            live?.Run, live?.Latest);
    }

    // Under the lock, so the listeners get the changes in the order they happened.
    private void Broadcast(string method, params object?[] args)
    {
        sends.Post(fleet.Clients.All, method, args);
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

    private sealed class LiveBot(string connectionId, string machine, Action close)
    {
        public string ConnectionId { get; } = connectionId;
        public string Machine { get; } = machine;
        public Action Close { get; } = close;
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
