using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ServiceClients.Security;

using System;

namespace ServiceClients.Pathing;

public static class PathingClientRegistration
{
    /// <summary>One attempt's limit. A first route on a cold continent loads tiles and can take seconds.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The limit for all attempts together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Every call is safe to repeat: routes are queries, saves replace, deletes are idempotent.</summary>
    public const int Retries = 2;

    /// <summary>
    /// Registers <see cref="PathingServiceOptions"/>, read from the host's configuration when first
    /// needed, so settings a host adds after registering services still count.
    /// </summary>
    public static IServiceCollection AddPathingServiceOptions(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => PathingServiceOptions.From(sp.GetRequiredService<IConfiguration>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IPathingClient"/> for the service the host's configuration names
    /// (<see cref="PathingServiceOptions"/>), and the options themselves.
    /// </summary>
    public static IHttpClientBuilder AddPathingClient(this IServiceCollection services)
    {
        services.AddPathingServiceOptions();
        return services.AddPathingClient((sp, client) => client.BaseAddress = sp.GetRequiredService<PathingServiceOptions>().Url,
            sp => sp.GetRequiredService<PathingServiceOptions>().ApiKey);
    }

    /// <summary>Registers <see cref="IPathingClient"/> for the service at <paramref name="baseAddress"/>, sending <paramref name="apiKey"/>.</summary>
    public static IHttpClientBuilder AddPathingClient(this IServiceCollection services, Uri baseAddress, string? apiKey = null)
    {
        return services.AddPathingClient((_, client) => client.BaseAddress = baseAddress, _ => apiKey);
    }

    private static IHttpClientBuilder AddPathingClient(this IServiceCollection services, Action<IServiceProvider, System.Net.Http.HttpClient> configure,
        Func<IServiceProvider, string?> apiKey)
    {
        IHttpClientBuilder builder = services.AddHttpClient<IPathingClient, PathingClient>(configure);

        // Set on the request itself, so a retry carries it too.
        builder.AddHttpMessageHandler(sp => new ApiKeyHandler(apiKey(sp)));

        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = AttemptTimeout;
            options.TotalRequestTimeout.Timeout = TotalTimeout;
            options.Retry.MaxRetryAttempts = Retries;
            // The breaker must sample at least two attempts' worth of time.
            options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2;
        });

        return builder;
    }
}
