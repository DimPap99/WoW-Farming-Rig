using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

using ServiceClients.Security;

using System;

namespace ServiceClients.Bots;

public static class BotManagerClientRegistration
{
    /// <summary>One attempt's limit: a command waits for the agent's answer, which the manager bounds below this.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The limit for all attempts together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Queries are retried this often; a command (start, stop, an assignment) is never repeated.</summary>
    public const int Retries = 2;

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
        IHttpClientBuilder builder = services.AddHttpClient<IBotManagerClient, BotManagerClient>(
            (sp, client) => client.BaseAddress = sp.GetRequiredService<BotManagerOptions>().Url);

        builder.AddHttpMessageHandler(sp => new ApiKeyHandler(sp.GetRequiredService<BotManagerOptions>().ApiKey));
        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = AttemptTimeout;
            options.TotalRequestTimeout.Timeout = TotalTimeout;
            options.Retry.MaxRetryAttempts = Retries;
            options.Retry.DisableForUnsafeHttpMethods();
            // The breaker must sample at least two attempts' worth of time.
            options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2;
        });

        return builder;
    }
}
