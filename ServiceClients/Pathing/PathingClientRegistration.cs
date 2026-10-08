using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using System;

namespace ServiceClients.Pathing;

/// <summary>
/// The pathing client as every host registers it (<see cref="ServiceClientRegistration.AddServiceClient{TClient, TImplementation}"/>).
/// Routes are queries, saves replace and deletes are idempotent, so a write is repeated too, for what a
/// retry can fix; starting a bake or an area grid is not quite: when the answer to one the service accepted
/// is lost on the way back, the repeat answers 409, a job is running.
/// </summary>
public static class PathingClientRegistration
{
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
        return services.AddServiceClient<IPathingClient, PathingClient>(sp => sp.GetRequiredService<PathingServiceOptions>().Url,
            sp => sp.GetRequiredService<PathingServiceOptions>().ApiKey, repeatWrites: true);
    }

    /// <summary>Registers <see cref="IPathingClient"/> for the service at <paramref name="baseAddress"/>, sending <paramref name="apiKey"/>.</summary>
    public static IHttpClientBuilder AddPathingClient(this IServiceCollection services, Uri baseAddress, string? apiKey = null)
    {
        return services.AddServiceClient<IPathingClient, PathingClient>(_ => baseAddress, _ => apiKey, repeatWrites: true);
    }
}
