using BotManager.Agents;
using BotManager.Bots;
using BotManager.Fleet;
using BotManager.Registry;

using Contracts;
using Contracts.Bots;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

using ServiceHosting;

using System;
using System.IO;

namespace BotManager.Hosting;

public static class BotManagerRegistration
{
    /// <summary>The configuration key naming the data folder; relative to the content root.</summary>
    public const string DataKey = "Data";

    public const string DefaultData = "data";

    public const string ApiTitle = "Bot Manager";

    /// <summary>An agent's log batch and hello carry more than SignalR's 32 KB default.</summary>
    public const long MaxAgentMessageBytes = 1024 * 1024;

    /// <summary>The registry, the fleet, the hubs and the API's plumbing.</summary>
    public static IServiceCollection AddBotManager(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            string configured = sp.GetRequiredService<IConfiguration>()[DataKey] is { Length: > 0 } data ? data : DefaultData;
            string folder = Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, configured);
            return new JsonBotRegistry(folder, sp.GetRequiredService<ILogger<JsonBotRegistry>>());
        });
        services.AddSingleton(sp => BotFleetOptions.From(sp.GetRequiredService<IConfiguration>()));
        // The fleet hub's one send loop: no agent waits on a listener.
        services.AddSingleton<HubSendQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<HubSendQueue>());
        services.AddSingleton<BotFleet>();

        services.AddSignalR(options => options.MaximumReceiveMessageSize = MaxAgentMessageBytes)
            .AddMessagePackProtocol();

        services.ConfigureHttpJsonOptions(options => ContractJson.Apply(options.SerializerOptions));
        // A request the manager cannot read is the caller's mistake: 400 with why, in Development too,
        // where it answered 500, which its client repeated (known issue 46).
        services.AddServiceProblems();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(swagger => swagger.SwaggerDoc("v1", new OpenApiInfo { Title = ApiTitle, Version = "v1" }));

        return services;
    }

    public static WebApplication UseBotManager(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Before everything it guards: the API and both hubs.
        app.UseServiceKey();

        app.UseSwagger();
        app.UseSwaggerUI(swagger => swagger.SwaggerEndpoint("/swagger/v1/swagger.json", ApiTitle));

        app.MapBotEndpoints();
        app.MapHub<AgentHub>(BotManagerApi.AgentHub);
        app.MapHub<FleetHub>(BotManagerApi.FleetHub);

        // The registry is read now, so a damaged file is reported at startup, not at the first agent.
        app.Services.GetRequiredService<JsonBotRegistry>();

        return app;
    }
}
