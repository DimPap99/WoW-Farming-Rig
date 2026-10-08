using Contracts;
using Contracts.Bots;

using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace BotManager.Registry;

/// <summary>
/// The bots, their assignments and their run history, which the manager owns; live state (online,
/// snapshots, logs) is memory only. JSON files under one folder: <c>bots.json</c> holds every bot,
/// and <c>runs/&lt;agentId&gt;.json</c> each bot's last <see cref="MaxRuns"/> runs. A file is written
/// whole or not at all (<see cref="AtomicFile"/>), so a crash never leaves half of one, and before
/// memory changes, so a write that fails changes nothing. A file that does not parse is kept aside
/// and the store starts without it; one that cannot be read (a lock, its rights) stops the start
/// instead: starting empty would write over every other bot at the first registration (known issue
/// 72). SQLite only if it becomes necessary.
/// </summary>
public sealed class JsonBotRegistry
{
    public const string BotsFile = "bots.json";
    public const string RunsFolder = "runs";
    public const int MaxRuns = BotManagerApi.KeptRuns;

    private static readonly JsonSerializerOptions FileJson = new(ContractJson.Options) { WriteIndented = true };

    private readonly Lock sync = new();
    private readonly string folder;
    private readonly ILogger logger;
    private readonly Dictionary<string, BotRecord> bots;

    public JsonBotRegistry(string folder, ILogger<JsonBotRegistry> logger)
    {
        this.folder = Path.GetFullPath(folder);
        this.logger = logger;
        Directory.CreateDirectory(Path.Combine(this.folder, RunsFolder));
        bots = Read<BotRecord[]>(Path.Combine(this.folder, BotsFile))?
            .Where(bot => AssignmentRules.IsAgentId(bot.AgentId))
            .ToDictionary(bot => bot.AgentId) ?? [];
    }

    public IReadOnlyList<BotRecord> List()
    {
        lock (sync)
        {
            return [.. bots.Values];
        }
    }

    public BotRecord? Get(string agentId)
    {
        lock (sync)
        {
            return bots.GetValueOrDefault(agentId);
        }
    }

    /// <summary>Creates or replaces the bot's record, on disk first: a write that fails throws and changes nothing.</summary>
    public void Save(BotRecord bot)
    {
        if (!AssignmentRules.IsAgentId(bot.AgentId))
        {
            throw new ArgumentException($"'{bot.AgentId}' is not an agent id", nameof(bot));
        }

        lock (sync)
        {
            WriteBots(bots.Values.Where(other => other.AgentId != bot.AgentId).Append(bot));
            bots[bot.AgentId] = bot;
        }
    }

    /// <summary>
    /// Creates or replaces the bot's record in memory only, for a write that failed while the record must
    /// count anyway (an agent that registered); the next save that succeeds writes it too.
    /// </summary>
    public void Keep(BotRecord bot)
    {
        if (!AssignmentRules.IsAgentId(bot.AgentId))
        {
            throw new ArgumentException($"'{bot.AgentId}' is not an agent id", nameof(bot));
        }

        lock (sync)
        {
            bots[bot.AgentId] = bot;
        }
    }

    /// <summary>Forgets the bot and its run history, on disk first; false when there was none.</summary>
    public bool Remove(string agentId)
    {
        lock (sync)
        {
            if (!bots.ContainsKey(agentId))
            {
                return false;
            }

            WriteBots(bots.Values.Where(bot => bot.AgentId != agentId));
            bots.Remove(agentId);
            File.Delete(RunsFile(agentId));
            return true;
        }
    }

    /// <summary>The bot's ended runs, newest first.</summary>
    public IReadOnlyList<RunReport> Runs(string agentId)
    {
        if (!AssignmentRules.IsAgentId(agentId))
        {
            return [];
        }

        lock (sync)
        {
            return Read<RunReport[]>(RunsFile(agentId)) ?? [];
        }
    }

    /// <summary>Adds an ended run, newest first; a run reported again replaces its first report.</summary>
    public void AddRun(string agentId, RunReport run)
    {
        if (!AssignmentRules.IsAgentId(agentId))
        {
            throw new ArgumentException($"'{agentId}' is not an agent id", nameof(agentId));
        }

        lock (sync)
        {
            // Reports are sent at least once: a report whose acknowledgement was lost comes again.
            RunReport[] runs = [run, .. (Read<RunReport[]>(RunsFile(agentId)) ?? [])
                .Where(kept => kept.Run.RunId != run.Run.RunId).Take(MaxRuns - 1)];
            Write(RunsFile(agentId), runs);
        }
    }

    private string RunsFile(string agentId)
    {
        return Path.Combine(folder, RunsFolder, agentId + ".json");
    }

    private void WriteBots(IEnumerable<BotRecord> all)
    {
        Write(Path.Combine(folder, BotsFile), all.OrderBy(bot => bot.AgentId, StringComparer.Ordinal).ToArray());
    }

    // An I/O error (a lock, the file's rights) throws: only a file that does not parse is damaged.
    private T? Read<T>(string file) where T : class
    {
        if (!File.Exists(file))
        {
            return null;
        }

        string text = File.ReadAllText(file);
        try
        {
            return JsonSerializer.Deserialize<T>(text, FileJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            string aside = $"{file}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "{File} is damaged; it is kept as {Aside} and the manager goes on without it", file, aside);
            try
            {
                File.Move(file, aside, overwrite: true);
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                logger.LogError(move, "{File} could not be kept aside: the next write replaces it", file);
            }

            return null;
        }
    }

    private static void Write<T>(string file, T value)
    {
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(value, FileJson));
    }
}
