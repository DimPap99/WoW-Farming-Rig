using Contracts.Bots;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Bots;

/// <summary>
/// <see cref="IBotManagerClient"/> over HTTP. Registered as a typed client by
/// <see cref="BotManagerClientRegistration.AddBotManagerClient"/>, which also sets timeouts and
/// retries queries only: a command is sent once.
/// </summary>
public sealed class BotManagerClient(HttpClient http) : IBotManagerClient
{
    private readonly ServiceHttp service = new(http, "bot manager");

    public Task<ServiceResult<ManagerHealth>> GetHealthAsync(CancellationToken token = default)
    {
        return service.GetAsync<ManagerHealth>(BotManagerApi.Health, token);
    }

    public Task<ServiceResult<BotInfo[]>> ListBotsAsync(CancellationToken token = default)
    {
        return service.GetAsync<BotInfo[]>(BotManagerApi.Bots, token);
    }

    public Task<ServiceResult<BotInfo>> GetBotAsync(string agentId, CancellationToken token = default)
    {
        return service.GetAsync<BotInfo>(BotUri(agentId), token);
    }

    public Task<ServiceResult> ForgetBotAsync(string agentId, CancellationToken token = default)
    {
        return service.SendAsync(HttpMethod.Delete, BotUri(agentId), null, token);
    }

    public Task<ServiceResult> SetAssignmentAsync(string agentId, BotAssignment assignment, CancellationToken token = default)
    {
        return service.SendAsync(HttpMethod.Put, BotUri(agentId, BotManagerApi.Assignment), assignment, token);
    }

    public Task<ServiceResult> ClearAssignmentAsync(string agentId, CancellationToken token = default)
    {
        return service.SendAsync(HttpMethod.Delete, BotUri(agentId, BotManagerApi.Assignment), null, token);
    }

    public Task<ServiceResult<CommandAck>> StartAsync(string agentId, CancellationToken token = default)
    {
        return service.SendAsync<CommandAck>(HttpMethod.Post, BotUri(agentId, BotManagerApi.Start), null, token);
    }

    public Task<ServiceResult<CommandAck>> StopAsync(string agentId, CancellationToken token = default)
    {
        return service.SendAsync<CommandAck>(HttpMethod.Post, BotUri(agentId, BotManagerApi.Stop), null, token);
    }

    public Task<ServiceResult<BotLogLine[]>> GetLogAsync(string agentId, CancellationToken token = default)
    {
        return service.GetAsync<BotLogLine[]>(BotUri(agentId, BotManagerApi.Log), token);
    }

    public Task<ServiceResult<RunRecord[]>> GetRunsAsync(string agentId, CancellationToken token = default)
    {
        return service.GetAsync<RunRecord[]>(BotUri(agentId, BotManagerApi.Runs), token);
    }

    private static string BotUri(string agentId, string? action = null)
    {
        string bot = $"{BotManagerApi.Bots}/{Uri.EscapeDataString(agentId)}";
        return action is null ? bot : $"{bot}/{action}";
    }
}
