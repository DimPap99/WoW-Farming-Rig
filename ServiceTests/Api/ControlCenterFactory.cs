using ControlCenter.Hosting;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ServiceClients.Bots;
using ServiceClients.Pathing;

using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceTests.Api;

/// <summary>
/// The Control Center in memory with both its services down (closed ports), unless a test hands it
/// a <paramref name="manager"/> client to use instead.
/// </summary>
internal sealed class ControlCenterFactory(IBotManagerClient? manager = null) : WebApplicationFactory<ControlCenter.Program>
{
    /// <summary>
    /// Renders <typeparamref name="TComponent"/> as a circuit would once its first loads are done.
    /// Prerendering is off, so a plain GET of a page returns only the shell.
    /// </summary>
    public async Task<string> RenderAsync<TComponent>(IDictionary<string, object?>? parameters = null) where TComponent : IComponent
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
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
        builder.UseSetting(ControlCenterLogging.FileKey, string.Empty);
        builder.UseSetting(PathingServiceOptions.UrlKey, ClosedPort.NewUrl().ToString());
        builder.UseSetting(BotManagerOptions.UrlKey, ClosedPort.NewUrl().ToString());
        if (manager is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(manager));
        }
    }
}
