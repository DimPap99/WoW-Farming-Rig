using ControlCenter.Bots;
using ControlCenter.Components;
using ControlCenter.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ServiceClients.Bots;
using ServiceClients.Pathing;

namespace ControlCenter.Hosting;

public static class ControlCenterRegistration
{
    public const string ErrorPage = "/error";

    /// <summary>The pages, the clients of the Pathing Service and the Bot Manager, and the forwarding to the service.</summary>
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

        // The Bot Manager is called from here only, never forwarded: /api/v1 is the Pathing
        // Service's on this host. Registers BotManagerOptions too (BotManager:Url).
        services.AddBotManagerClient();
        services.AddSingleton<BotManagerStatus>();
        services.AddHostedService(sp => sp.GetRequiredService<BotManagerStatus>());
        services.AddSingleton<IFleetConnectionFactory, FleetConnectionFactory>();
        services.AddSingleton<FleetFeed>();
        services.AddHostedService(sp => sp.GetRequiredService<FleetFeed>());

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
