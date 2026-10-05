using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PathingService.Hosting;

namespace PathingService.Navmesh;

/// <summary>
/// <c>--bake=&lt;continent&gt;</c> starts a navmesh bake as the service comes up, and
/// <c>--bake-area=&lt;continent&gt;</c> an area-grid extraction; <c>all</c> means every continent.
/// The service answers at once; progress is on <see cref="PathingApi.Bake"/>.
/// </summary>
public static class StartupBakes
{
    public const string BakeKey = "bake";

    public const string BakeAreaKey = "bake-area";

    public const string AllContinents = "all";

    public static WebApplication StartRequestedBakes(this WebApplication app)
    {
        string? bake = app.Configuration[BakeKey];
        string? bakeArea = app.Configuration[BakeAreaKey];
        if (string.IsNullOrWhiteSpace(bake) && string.IsNullOrWhiteSpace(bakeArea))
        {
            return app;
        }

        BakeAdmin admin = app.Services.GetRequiredService<BakeAdmin>();
        ILogger logger = app.Services.GetRequiredService<ILogger<BakeAdmin>>();

        if (!string.IsNullOrWhiteSpace(bake))
        {
            StoreResult<BakeStatus> started = admin.Start(new BakeRequest(Continent(bake)));
            Report(logger, "Startup bake", bake, started.Outcome, started.Error);
        }

        if (!string.IsNullOrWhiteSpace(bakeArea))
        {
            StoreResult started = admin.StartAreaGrid(Continent(bakeArea));
            Report(logger, "Startup area-grid extraction", bakeArea, started.Outcome, started.Error);
        }

        return app;
    }

    private static string? Continent(string scope)
    {
        return scope is AllContinents or "*" ? null : scope;
    }

    private static void Report(ILogger logger, string what, string scope, StoreOutcome outcome, string? error)
    {
        if (outcome == StoreOutcome.Done)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{What} started: {Scope}", what, scope);
            }
        }
        else if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning("{What} for {Scope} did not start: {Error}", what, scope, error);
        }
    }
}
