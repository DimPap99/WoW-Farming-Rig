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

    /// <summary>
    /// The largest message a browser may send the circuit. The world view sends a shape the pointer
    /// edited whole, and a recorded path opened as waypoints can have a few thousand points: more
    /// than the default 32 KB, which would end the circuit.
    /// </summary>
    public const int MaxCircuitMessageBytes = 1024 * 1024;

    /// <summary>The pages, the clients of the Pathing Service and the Bot Manager, and the forwarding to the service.</summary>
    public static IServiceCollection AddControlCenter(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(o => o.MaximumReceiveMessageSize = MaxCircuitMessageBytes);

        // Registers PathingServiceOptions too (PathingService:Url).
        services.AddPathingClient();
        services.AddHttpForwarder();

        services.AddSingleton<PathingStatus>();
        services.AddHostedService(sp => sp.GetRequiredService<PathingStatus>());
        services.AddSingleton<WorldCache>();
        // Connected only while a page watches the routes the service plans.
        services.AddSingleton<IWatchConnectionFactory, WatchConnectionFactory>();
        services.AddSingleton<WatchFeed>();

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
