using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;

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

    /// <summary>
    /// The identity kept in <paramref name="file"/>, created there when it has none, or holds no id. A file
    /// that cannot be read (a lock, its rights) throws: a new id there would make the manager see a new bot
    /// (known issue 76).
    /// </summary>
    public static AgentIdentity LoadOrCreate(string file, string? configuredName, ILogger logger)
    {
        string? id = null;
        if (File.Exists(file))
        {
            string text = File.ReadAllText(file);
            try
            {
                id = JsonSerializer.Deserialize<Stored>(text, Json)?.AgentId;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "{File} is damaged; this agent takes a new id and the manager sees a new bot", file);
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

            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(new Stored(id, DateTimeOffset.UtcNow), Json));
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("This agent is {AgentId}, kept in {File}", id, Path.GetFullPath(file));
            }
        }

        string machine = Environment.MachineName;
        string name = string.IsNullOrWhiteSpace(configuredName) ? machine : configuredName.Trim();
        return new AgentIdentity(id!, name, machine);
    }

    /// <summary>
    /// Holds this id on this PC until disposed, or null when another process holds it: two agents
    /// with one id, as a second start from one folder makes, take the bot's place in the manager from
    /// each other (known issue 74). A process that ends lets go of it, however it ended. Machine-wide
    /// (<c>Global\</c>): a second agent in another session, or started detached on Linux, counts too.
    /// </summary>
    public IDisposable? Claim()
    {
        Mutex mutex = new(initiallyOwned: true, $@"Global\WoWFarmingRig.Agent.{AgentId}", out bool created);
        if (created)
        {
            return new Held(mutex);
        }

        mutex.Dispose();
        return null;
    }

    private sealed record Stored(string? AgentId, DateTimeOffset? Created);

    private sealed class Held(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread than the one that took it: closing it lets it go too.
            }

            mutex.Dispose();
        }
    }
}
