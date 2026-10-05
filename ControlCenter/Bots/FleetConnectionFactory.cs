using Contracts;
using Contracts.Bots;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using ServiceClients.Bots;

using System;

namespace ControlCenter.Bots;

/// <summary>Builds the <see cref="FleetFeed"/>'s connection to the manager's fleet hub; a test builds one over its in-memory server.</summary>
public interface IFleetConnectionFactory
{
    HubConnection Create();
}

/// <summary>The fleet hub of the manager at <c>BotManager:Url</c>: MessagePack, with the manager's key.</summary>
public sealed class FleetConnectionFactory(BotManagerOptions options) : IFleetConnectionFactory
{
    public HubConnection Create()
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(options.Url, BotManagerApi.FleetHub), http =>
            {
                if (!string.IsNullOrEmpty(options.ApiKey))
                {
                    http.Headers[ServiceKey.Header] = options.ApiKey;
                }
            })
            .AddMessagePackProtocol()
            .Build();
    }
}
