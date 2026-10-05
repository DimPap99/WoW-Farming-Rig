using Contracts.Bots;

using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Bots;

/// <summary>
/// The Bot Manager's REST API, as the Control Center sees it. Nothing here throws for a manager
/// that is down or refuses: every call answers a <see cref="ServiceResult"/>. Only the caller's own
/// cancellation surfaces as an exception. The live stream is the fleet hub (<see cref="BotManagerApi.FleetHub"/>).
/// </summary>
public interface IBotManagerClient
{
    Task<ServiceResult<ManagerHealth>> GetHealthAsync(CancellationToken token = default);

    Task<ServiceResult<BotInfo[]>> ListBotsAsync(CancellationToken token = default);

    Task<ServiceResult<BotInfo>> GetBotAsync(string agentId, CancellationToken token = default);

    /// <summary>Forgets an offline bot; refused (409) while it is online.</summary>
    Task<ServiceResult> ForgetBotAsync(string agentId, CancellationToken token = default);

    /// <summary>Sets what the bot runs when it starts; refused (400) with why when the assignment is incomplete.</summary>
    Task<ServiceResult> SetAssignmentAsync(string agentId, BotAssignment assignment, CancellationToken token = default);

    Task<ServiceResult> ClearAssignmentAsync(string agentId, CancellationToken token = default);

    /// <summary>Starts the bot's assignment: the agent's answer, or 409 with why (offline, no assignment, refused).</summary>
    Task<ServiceResult<CommandAck>> StartAsync(string agentId, CancellationToken token = default);

    /// <summary>Stops the bot's run, which releases input first.</summary>
    Task<ServiceResult<CommandAck>> StopAsync(string agentId, CancellationToken token = default);

    Task<ServiceResult<BotLogLine[]>> GetLogAsync(string agentId, CancellationToken token = default);

    Task<ServiceResult<RunRecord[]>> GetRunsAsync(string agentId, CancellationToken token = default);
}
