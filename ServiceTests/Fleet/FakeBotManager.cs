using Contracts.Bots;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTests.Fleet;

/// <summary>
/// The Bot Manager as the Control Center's client sees it, answering from what a test gives it:
/// its bots, one log and one run history for all of them. Changes are recorded, never applied.
/// </summary>
internal sealed class FakeBotManager(BotInfo[] bots, BotLogLine[]? log = null, RunRecord[]? runs = null) : IBotManagerClient
{
    public const string Unreachable = "the bot manager could not be reached: the test took it away";

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The manager stops answering its health, as one that went down would.</summary>
    public bool Down { get; set; }

    public Task<ServiceResult<ManagerHealth>> GetHealthAsync(CancellationToken token = default)
    {
        return Down
            ? Task.FromResult(new ServiceResult<ManagerHealth>(null, 0, Unreachable))
            : Answer(new ManagerHealth(BotManagerApi.Version, BotManagerApi.AgentProtocol, bots.Length, bots.Count(bot => bot.Online)));
    }

    public Task<ServiceResult<BotInfo[]>> ListBotsAsync(CancellationToken token = default)
    {
        return Answer(bots);
    }

    public Task<ServiceResult<BotInfo>> GetBotAsync(string agentId, CancellationToken token = default)
    {
        return bots.FirstOrDefault(bot => bot.AgentId == agentId) is { } bot
            ? Answer(bot)
            : Task.FromResult(new ServiceResult<BotInfo>(null, 404, "404 Not Found"));
    }

    public Task<ServiceResult> ForgetBotAsync(string agentId, CancellationToken token = default)
    {
        return Record($"forget {agentId}");
    }

    public Task<ServiceResult> SetAssignmentAsync(string agentId, BotAssignment assignment, CancellationToken token = default)
    {
        return Record($"assign {agentId}: {assignment}");
    }

    public Task<ServiceResult> ClearAssignmentAsync(string agentId, CancellationToken token = default)
    {
        return Record($"clear {agentId}");
    }

    public Task<ServiceResult<CommandAck>> StartAsync(string agentId, CancellationToken token = default)
    {
        Calls.Enqueue($"start {agentId}");
        return Answer(new CommandAck(true, null, "run-1"));
    }

    public Task<ServiceResult<CommandAck>> StopAsync(string agentId, CancellationToken token = default)
    {
        Calls.Enqueue($"stop {agentId}");
        return Answer(new CommandAck(true, null, "run-1"));
    }

    public Task<ServiceResult<BotLogLine[]>> GetLogAsync(string agentId, CancellationToken token = default)
    {
        return Answer(log ?? []);
    }

    public Task<ServiceResult<RunRecord[]>> GetRunsAsync(string agentId, CancellationToken token = default)
    {
        return Answer(runs ?? []);
    }

    private static Task<ServiceResult<T>> Answer<T>(T value)
    {
        return Task.FromResult(new ServiceResult<T>(value, 200, null));
    }

    private Task<ServiceResult> Record(string call)
    {
        Calls.Enqueue(call);
        return Task.FromResult(new ServiceResult(204, null));
    }
}
