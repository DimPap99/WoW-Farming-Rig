using Contracts;
using Contracts.Bots;

using Microsoft.Extensions.Logging;

using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace BotManager.Registry;

/// <summary>
/// <see cref="IBotRegistry"/> in JSON files under one folder: <c>bots.json</c> holds every bot, and
/// <c>runs/&lt;agentId&gt;.json</c> each bot's last <see cref="MaxRuns"/> runs. Files are written
/// to a temporary name and renamed, so a crash never leaves half a file; a file that cannot be read
/// is kept aside and the store starts without it rather than refusing to start.
/// </summary>
public sealed class JsonBotRegistry : IBotRegistry
{
    public const string BotsFile = "bots.json";
    public const string RunsFolder = "runs";
    public const int MaxRuns = 200;

    private const string TemporaryExtension = ".tmp";

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

    public void Save(BotRecord bot)
    {
        if (!AssignmentRules.IsAgentId(bot.AgentId))
        {
            throw new ArgumentException($"'{bot.AgentId}' is not an agent id", nameof(bot));
        }

        lock (sync)
        {
            bots[bot.AgentId] = bot;
            WriteBots();
        }
    }

    public bool Remove(string agentId)
    {
        lock (sync)
        {
            if (!bots.Remove(agentId))
            {
                return false;
            }

            WriteBots();
            File.Delete(RunsFile(agentId));
            return true;
        }
    }

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

    public void AddRun(string agentId, RunReport run)
    {
        if (!AssignmentRules.IsAgentId(agentId))
        {
            throw new ArgumentException($"'{agentId}' is not an agent id", nameof(agentId));
        }

        lock (sync)
        {
            RunReport[] runs = [run, .. (Read<RunReport[]>(RunsFile(agentId)) ?? []).Take(MaxRuns - 1)];
            Write(RunsFile(agentId), runs);
        }
    }

    private string RunsFile(string agentId)
    {
        return Path.Combine(folder, RunsFolder, agentId + ".json");
    }

    private void WriteBots()
    {
        Write(Path.Combine(folder, BotsFile), bots.Values.OrderBy(bot => bot.AgentId, StringComparer.Ordinal).ToArray());
    }

    private T? Read<T>(string file) where T : class
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), FileJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string aside = $"{file}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "{File} could not be read; it is kept as {Aside} and the manager goes on without it", file, aside);
            try
            {
                File.Move(file, aside, overwrite: true);
            }
            catch (IOException)
            {
            }
            return null;
        }
    }

    private static void Write<T>(string file, T value)
    {
        string temporary = $"{file}.{Guid.NewGuid():N}{TemporaryExtension}";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, FileJson));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
