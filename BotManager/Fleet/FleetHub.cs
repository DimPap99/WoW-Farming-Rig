using Contracts.Bots;

using Microsoft.AspNetCore.SignalR;

namespace BotManager.Fleet;

/// <summary>
/// The fleet as a stream (<see cref="BotManagerApi.FleetHub"/>): every bot change, snapshot, log
/// batch and ended run goes to every listener (<see cref="FleetHubMethods"/>). A listener reads the
/// current state over REST first and then follows this; nothing is replayed.
/// </summary>
public sealed class FleetHub : Hub;
