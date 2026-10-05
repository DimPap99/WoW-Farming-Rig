using Microsoft.Extensions.Logging;

using ServiceClients.Bots;

using System;
using System.IO;
using System.Text.Json;

namespace BotAgent;

/// <summary>
/// Who this agent is to the Bot Manager: an id created on its first start and kept in a file on its
/// PC (<see cref="AgentOptions.IdentityFile"/>), and a name, the machine's unless configured. The bot
/// is the PC's agent, so this id is the bot's; what account, character and client it plays are what
/// it reports.
/// </summary>
public sealed record AgentIdentity(string AgentId, string Name, string Machine)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The identity kept in <paramref name="file"/>, created there when it has none.</summary>
    public static AgentIdentity LoadOrCreate(string file, string? configuredName, ILogger logger)
    {
        string? id = null;
        if (File.Exists(file))
        {
            try
            {
                id = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), Json)?.AgentId;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "{File} could not be read; this agent takes a new id and the manager sees a new bot", file);
            }
        }

        if (!AssignmentRules.IsAgentId(id))
        {
            id = Guid.NewGuid().ToString("N");
            string? folder = Path.GetDirectoryName(Path.GetFullPath(file));
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(id, DateTimeOffset.UtcNow), Json));
            File.Move(temporary, file, overwrite: true);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("This agent is {AgentId}, kept in {File}", id, Path.GetFullPath(file));
            }
        }

        string machine = Environment.MachineName;
        string name = string.IsNullOrWhiteSpace(configuredName) ? machine : configuredName.Trim();
        return new AgentIdentity(id!, name, machine);
    }

    private sealed record Stored(string? AgentId, DateTimeOffset? Created);
}
