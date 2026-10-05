using Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

using PathingService.CostZones;
using PathingService.Paths;
using PathingService.Routes;
using PathingService.Status;

using PPather.Planning;

using SharedLib;

namespace PathingService.Hosting;

public static class PathingServiceRegistration
{
    /// <summary>The configuration key (<c>--exp=som</c>) naming the client whose data is served.</summary>
    public const string ExpKey = "exp";

    /// <summary>Classic is served when no client is named, as PathingAPI does.</summary>
    public const string DefaultExp = "som";

    public const string ApiTitle = "Pathing Service";

    /// <summary>The planner, the stores and the API's plumbing. One client per instance (see CLAUDE.md).</summary>
    public static IServiceCollection AddPathingService(this IServiceCollection services, IConfiguration configuration)
    {
        string exp = string.IsNullOrWhiteSpace(configuration[ExpKey]) ? DefaultExp : configuration[ExpKey]!;

        services.AddSingleton(_ => DataConfig.Load(exp));
        services.AddSingleton<WorldMapAreaDB>();

        services.Configure<NavmeshBakeOptions>(configuration.GetSection(NavmeshBakeOptions.Position));
        services.Configure<NavmeshQueryOptions>(configuration.GetSection(NavmeshQueryOptions.Position));

        services.AddSingleton(sp => new RoutePlanner(
            sp.GetRequiredService<ILogger<RoutePlanner>>(),
            sp.GetRequiredService<DataConfig>(),
            sp.GetRequiredService<WorldMapAreaDB>(),
            sp.GetRequiredService<IOptions<NavmeshBakeOptions>>().Value,
            sp.GetRequiredService<IOptions<NavmeshQueryOptions>>().Value));

        services.AddSingleton<RouteHandler>();
        services.AddSingleton<RecordedPathStore>();
        services.AddSingleton<CostZoneStore>();

        services.ConfigureHttpJsonOptions(options => ContractJson.Apply(options.SerializerOptions));
        services.AddProblemDetails();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(swagger => swagger.SwaggerDoc("v1", new OpenApiInfo { Title = ApiTitle, Version = "v1" }));

        return services;
    }

    public static WebApplication UsePathingService(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.UseSwagger();
        app.UseSwaggerUI(swagger => swagger.SwaggerEndpoint("/swagger/v1/swagger.json", ApiTitle));

        app.MapStatusEndpoints();
        app.MapRouteEndpoints();
        app.MapPathEndpoints();
        app.MapCostZoneEndpoints();

        return app;
    }
}
