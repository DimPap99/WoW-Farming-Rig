using BotManager.Bots;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;

using System;
using System.Threading.Tasks;

namespace BotManager.Agents;

/// <summary>
/// The hub agents dial out to (<see cref="BotManagerApi.AgentHub"/>). An agent registers on every
/// connection, then streams snapshots, log lines and run reports; the manager reaches it only over
/// this connection (<see cref="AgentHubMethods.Assign"/>, <see cref="AgentHubMethods.Command"/>).
/// Anything an agent sends before it registers is ignored.
/// </summary>
public sealed class AgentHub(BotFleet fleet) : Hub
{
    [HubMethodName(AgentHubMethods.Register)]
    public Task<AgentWelcome> Register(AgentHello hello)
    {
        // Closing this connection is how a newer one from the same PC takes over from it.
        return fleet.RegisterAsync(Context.ConnectionId, hello, Context.Abort);
    }

    [HubMethodName(AgentHubMethods.Snapshot)]
    public Task Snapshot(BotSnapshot snapshot)
    {
        return fleet.SnapshotAsync(Context.ConnectionId, snapshot);
    }

    [HubMethodName(AgentHubMethods.Log)]
    public Task Log(BotLogLine[] lines)
    {
        return fleet.LogAsync(Context.ConnectionId, lines);
    }

    [HubMethodName(AgentHubMethods.RunEnded)]
    public Task RunEnded(RunReport report)
    {
        return fleet.RunEndedAsync(Context.ConnectionId, report);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        return fleet.DisconnectedAsync(Context.ConnectionId);
    }
}
