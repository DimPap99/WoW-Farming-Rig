using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Polly;
using Polly.Timeout;

using ServiceClients.Security;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ServiceClients.Pathing;

public static class PathingClientRegistration
{
    /// <summary>One attempt's limit. A first route on a cold continent loads tiles and can take seconds.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The limit for all attempts together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How often a call is repeated, and only for what <see cref="IsWorthRetrying"/> names. Routes are
    /// queries, saves replace and deletes are idempotent, so repeating them is safe; starting a bake or
    /// an area grid is not quite: when the answer to one the service accepted is lost on the way back,
    /// the repeat answers 409, a job is running.
    /// </summary>
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
            options.Retry.ShouldHandle = args => ValueTask.FromResult(IsWorthRetrying(args.Outcome));
            // One breaker serves every call: a continent that is busy, or a file that is bad, must not
            // shut out the rest. It opens on what says the service itself is out of reach.
            options.CircuitBreaker.ShouldHandle = args =>
                ValueTask.FromResult(IsWorthRetrying(args.Outcome) || args.Outcome.Exception is TimeoutRejectedException);
            // The breaker must sample at least two attempts' worth of time.
            options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2;
        });

        return builder;
    }

    /// <summary>
    /// What a retry can fix: the request did not reach the service (<see cref="HttpRequestException"/>),
    /// or a gateway in front of it failed (502, 504). Not a 500, which comes again (an unreadable
    /// file, a planner exception); not a 503, which a continent answers after its lane already waited
    /// 10 s; and not an attempt that timed out, which would run its search again.
    /// </summary>
    public static bool IsWorthRetrying(Outcome<HttpResponseMessage> outcome)
    {
        return outcome.Exception is HttpRequestException ||
               outcome.Result?.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
    }
}
