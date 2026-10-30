using Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

using PathingService.Catalog;
using PathingService.CostZones;
using PathingService.Forever;
using PathingService.Navmesh;
using PathingService.Paths;
using PathingService.Routes;
using PathingService.Status;
using PathingService.Sync;
using PathingService.Watch;
using PathingService.World;

using PPather.Planning;

using SharedLib;

namespace PathingService.Hosting;

public static class PathingServiceRegistration
{
    /// <summary>The configuration key (<c>--exp=som</c>) naming the client whose data is served.</summary>
    public const string ExpKey = "exp";

    /// <summary>Classic is served when no client is named.</summary>
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

        services.AddSingleton<ClientContinents>();
        services.AddSingleton<RouteHandler>();
        services.AddSingleton<RecordedPathStore>();
        services.AddSingleton<ForeverRouteLibrary>();
        services.AddSingleton<CostZoneStore>();
        services.AddSingleton<SyncLibrary>();
        services.AddSingleton<CoverageCache>();
        services.AddSingleton<WorldCatalog>();
        services.AddSingleton<QuestCatalog>();
        services.AddSingleton<LocationCatalog>();
        services.AddSingleton<TestRoutes>();
        services.AddSingleton<BakeAdmin>();

        services.AddSingleton<WatchBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<WatchBroadcaster>());

        // No LZ4: a reader without MessagePack-CSharp's compression extension, a browser's for one,
        // gets garbage, and routes are small.
        services.AddSignalR().AddMessagePackProtocol();

        services.ConfigureHttpJsonOptions(options => ContractJson.Apply(options.SerializerOptions));
        // A request the service cannot read (a body that is not the contract's JSON, a query that is
        // not a number) is the caller's mistake: 400 with why. Minimal APIs answer so themselves in
        // Production, but in Development, where run.sh runs, they throw, and the exception handler
        // would answer 500, which a client takes for the service's own failure.
        services.AddExceptionHandler(options => options.StatusCodeSelector = ex =>
            ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            if (context.Exception is BadHttpRequestException bad)
            {
                context.ProblemDetails.Detail = bad.Message;
            }
        });
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(swagger => swagger.SwaggerDoc("v1", new OpenApiInfo { Title = ApiTitle, Version = "v1" }));

        return services;
    }

    public static WebApplication UsePathingService(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Before everything it guards: the API, the hub and the world data.
        app.UseServiceKey();

        app.UseSwagger();
        app.UseSwaggerUI(swagger => swagger.SwaggerEndpoint("/swagger/v1/swagger.json", ApiTitle));

        app.UseWorldData();

        app.MapStatusEndpoints();
        app.MapRouteEndpoints();
        app.MapPathEndpoints();
        app.MapForeverRouteEndpoints();
        app.MapCostZoneEndpoints();
        app.MapSyncEndpoints();
        app.MapWorldEndpoints();
        app.MapCatalogEndpoints();
        app.MapNavmeshEndpoints();
        app.MapWatchEndpoints();

        app.StartRequestedBakes();

        return app;
    }
}
