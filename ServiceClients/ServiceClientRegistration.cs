using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using Polly;
using Polly.Timeout;

using ServiceClients.Security;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ServiceClients;

/// <summary>
/// A typed client of one of the rig's services, as every host registers it: the service's address and key,
/// and the resilience every client has. The two clients copied this by hand, and the retry rule of known
/// issue 37 reached only the pathing client's copy, so the Control Center's client of the manager
/// repeated a query the manager answered 500 and counted the manager's own 504 as the manager being down
/// (known issue 46).
/// </summary>
public static class ServiceClientRegistration
{
    /// <summary>One attempt's limit. A first route on a cold continent loads tiles and can take seconds; a command waits for an agent.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The limit for all attempts together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How often a call is repeated, and only for what <see cref="IsWorthRetrying"/> names.</summary>
    public const int Retries = 2;

    /// <summary>
    /// Registers <typeparamref name="TClient"/> for the service at <paramref name="url"/>, sending <paramref name="key"/>.
    /// A call is repeated only for what a retry can fix; with <paramref name="repeatWrites"/> false, as for a service
    /// whose writes are commands (start, stop), a write is never repeated. The breaker opens on what says the
    /// service is out of reach (<see cref="IsOutOfReach"/>), never on what it answered.
    /// </summary>
    public static IHttpClientBuilder AddServiceClient<TClient, TImplementation>(this IServiceCollection services,
        Func<IServiceProvider, Uri> url, Func<IServiceProvider, string?> key, bool repeatWrites)
        where TClient : class
        where TImplementation : class, TClient
    {
        IHttpClientBuilder builder = services.AddHttpClient<TClient, TImplementation>((sp, client) => client.BaseAddress = url(sp));

        // Set on the request itself, so a retry carries it too.
        builder.AddHttpMessageHandler(sp => new ApiKeyHandler(key(sp)));

        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = AttemptTimeout;
            options.TotalRequestTimeout.Timeout = TotalTimeout;
            options.Retry.MaxRetryAttempts = Retries;
            options.Retry.ShouldHandle = args => ValueTask.FromResult(IsWorthRetrying(args.Outcome));
            if (!repeatWrites)
            {
                // Wraps the rule above: a POST, PUT or DELETE is never repeated.
                options.Retry.DisableForUnsafeHttpMethods();
            }

            // One breaker serves every call: a continent that is busy, a file that is bad, an agent that does not
            // answer, must not shut out the rest.
            options.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(IsOutOfReach(args.Outcome));
            // The breaker must sample at least two attempts' worth of time.
            options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2;
        });

        return builder;
    }

    /// <summary>
    /// What a retry can fix: the request did not reach the service (<see cref="HttpRequestException"/>), or a
    /// gateway in front of it failed (502, 504). Not a 500, which comes again (an unreadable file, a planner
    /// exception); not a 503, which a continent answers after its lane already waited 10 s; and not an attempt
    /// that timed out, which would run its search again.
    /// </summary>
    public static bool IsWorthRetrying(Outcome<HttpResponseMessage> outcome)
    {
        return outcome.Exception is HttpRequestException ||
               outcome.Result?.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
    }

    /// <summary>
    /// What says the service itself is out of reach, which the breaker counts: a request that did not reach it,
    /// an attempt that timed out, a gateway that failed (502). Not a 504: the Bot Manager answers one for an
    /// agent that did not answer.
    /// </summary>
    public static bool IsOutOfReach(Outcome<HttpResponseMessage> outcome)
    {
        return outcome.Exception is HttpRequestException or TimeoutRejectedException ||
               outcome.Result?.StatusCode is HttpStatusCode.BadGateway;
    }

    /// <summary>
    /// The service's address from <c>{position}:Url</c>, else <paramref name="defaultUrl"/>: an http or https URL,
    /// or it throws, naming the setting.
    /// </summary>
    public static Uri Url(IConfiguration configuration, string position, string defaultUrl)
    {
        string key = $"{position}:Url";
        string? configured = configuration[key];
        string url = string.IsNullOrWhiteSpace(configured) ? defaultUrl : configured;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"{key} must be an http or https URL, not '{url}'.");
    }

    /// <summary>Whether <c>{position}:Url</c> is set: an agent with none runs on its own, or uses its own copies.</summary>
    public static bool IsConfigured(IConfiguration configuration, string position)
    {
        return !string.IsNullOrWhiteSpace(configuration[$"{position}:Url"]);
    }
}
