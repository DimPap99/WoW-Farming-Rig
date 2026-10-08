using Contracts.Bots;

using System;

namespace BotManager.Registry;

/// <summary>What the manager keeps about a bot between its connections (<see cref="JsonBotRegistry"/>).</summary>
/// <param name="LastSeen">When it last registered or disconnected.</param>
public sealed record BotRecord(string AgentId, string Name, string Machine, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int Protocol, string AgentVersion, AgentGame? Game, string[] ClassProfiles, BotAssignment? Assignment);
