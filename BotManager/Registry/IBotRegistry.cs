using Contracts.Bots;

using System;
using System.Collections.Generic;

namespace BotManager.Registry;

/// <summary>What the manager keeps about a bot between its connections.</summary>
/// <param name="LastSeen">When it last registered or disconnected.</param>
public sealed record BotRecord(string AgentId, string Name, string Machine, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int Protocol, string AgentVersion, AgentGame? Game, string[] ClassProfiles, BotAssignment? Assignment);

/// <summary>
/// The bots, their assignments and their run history. The manager owns this data; live state
/// (online, snapshots, logs) is memory only. A JSON file store today (<see cref="JsonBotRegistry"/>),
/// SQLite only if it becomes necessary.
/// </summary>
public interface IBotRegistry
{
    IReadOnlyList<BotRecord> List();

    BotRecord? Get(string agentId);

    /// <summary>Creates or replaces the bot's record.</summary>
    void Save(BotRecord bot);

    /// <summary>Forgets the bot and its run history; false when there was none.</summary>
    bool Remove(string agentId);

    /// <summary>The bot's ended runs, newest first.</summary>
    IReadOnlyList<RunReport> Runs(string agentId);

    void AddRun(string agentId, RunReport run);
}
