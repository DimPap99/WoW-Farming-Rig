using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Registers <see cref="IPathingClient"/> for the service at <paramref name="baseAddress"/>.</summary>
    public static IHttpClientBuilder AddPathingClient(this IServiceCollection services, Uri baseAddress)
    {
        IHttpClientBuilder builder = services.AddHttpClient<IPathingClient, PathingClient>(client => client.BaseAddress = baseAddress);

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
