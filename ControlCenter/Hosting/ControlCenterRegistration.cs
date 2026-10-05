using ControlCenter.Components;
using ControlCenter.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ServiceClients.Pathing;

namespace ControlCenter.Hosting;

public static class ControlCenterRegistration
{
    public const string ErrorPage = "/error";

    /// <summary>The pages, the Pathing Service's client and the forwarding to it.</summary>
    public static IServiceCollection AddControlCenter(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Registers PathingServiceOptions too (PathingService:Url).
        services.AddPathingClient();
        services.AddHttpForwarder();

        services.AddSingleton<PathingStatus>();
        services.AddHostedService(sp => sp.GetRequiredService<PathingStatus>());
        services.AddSingleton<WorldCache>();

        return services;
    }

    public static WebApplication UseControlCenter(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(ErrorPage, createScopeForErrors: true);
        }

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapServiceProxy();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
