using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ServiceClients.Bots;

/// <summary>
/// The Bot Manager's client as every host registers it (<see cref="ServiceClientRegistration.AddServiceClient{TClient, TImplementation}"/>):
/// queries are repeated for what a retry can fix, and a command (start, stop, an assignment) never.
/// </summary>
public static class BotManagerClientRegistration
{
    /// <summary>Registers <see cref="BotManagerOptions"/>, read from the host's configuration when first needed.</summary>
    public static IServiceCollection AddBotManagerOptions(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => BotManagerOptions.From(sp.GetRequiredService<IConfiguration>()));
        return services;
    }

    /// <summary>Registers <see cref="IBotManagerClient"/> for the manager the host's configuration names, and its options.</summary>
    public static IHttpClientBuilder AddBotManagerClient(this IServiceCollection services)
    {
        services.AddBotManagerOptions();
        return services.AddServiceClient<IBotManagerClient, BotManagerClient>(sp => sp.GetRequiredService<BotManagerOptions>().Url,
            sp => sp.GetRequiredService<BotManagerOptions>().ApiKey, repeatWrites: false);
    }
}
