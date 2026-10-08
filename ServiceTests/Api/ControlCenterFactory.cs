using ControlCenter.Hosting;

using Contracts;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Bots;
using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceTests.Api;

/// <summary>
/// The Control Center in memory with both its services down (closed ports), unless a test hands it
/// a <paramref name="manager"/> or <paramref name="pathing"/> client to use instead, or the address
/// of a Pathing Service to forward to (<paramref name="pathingUrl"/>, with <paramref name="pathingKey"/>).
/// A request carrying <see cref="RemoteAddress.Header"/> comes from that address.
/// </summary>
internal sealed class ControlCenterFactory(IBotManagerClient? manager = null, IPathingClient? pathing = null,
    Uri? pathingUrl = null, string? pathingKey = null) : WebApplicationFactory<ControlCenter.Program>
{
    private const string BaseUri = "http://localhost/";

    /// <summary>
    /// Renders <typeparamref name="TComponent"/> as a circuit would once its first loads are done.
    /// Prerendering is off, so a plain GET of a page returns only the shell.
    /// </summary>
    public async Task<string> RenderAsync<TComponent>(IDictionary<string, object?>? parameters = null, string uri = "/") where TComponent : IComponent
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        // A page that reads its address ([SupplyParameterFromQuery]) needs one, as a circuit gives it.
        if (scope.ServiceProvider.GetRequiredService<NavigationManager>() is IHostEnvironmentNavigationManager navigation)
        {
            navigation.Initialize(BaseUri, new Uri(new Uri(BaseUri), uri).ToString());
        }

        await using HtmlRenderer renderer = new(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ServiceHosting.ServiceLogging.FileKey, string.Empty);
        builder.UseSetting(PathingServiceOptions.UrlKey, (pathingUrl ?? ClosedPort.NewUrl()).ToString());
        builder.UseSetting(BotManagerOptions.UrlKey, ClosedPort.NewUrl().ToString());
        if (pathingKey is not null)
        {
            builder.UseSetting($"{PathingServiceOptions.Position}:{ServiceKey.ConfigKey}", pathingKey);
        }

        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, RemoteAddress>());
        if (manager is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(manager));
        }

        if (pathing is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(pathing));
        }
    }
}
