using Core.Goals;
using Core.GOAP;
using Core.Session;

using Game;

using Microsoft.Extensions.DependencyInjection;

using System;

using static Core.BlacklistSourceType;

namespace Core.Forever;

/// <summary>
/// Composes the guarded Forever session. GoalFactory hands over after it has
/// registered what both clients share, so no Classic goal, combat tracker,
/// route navigator or trainer planner is ever constructed on Forever.
/// </summary>
public static class ForeverGoalFactory
{
    public static IServiceProvider Create(IServiceCollection services,
        IServiceProvider sp, ClassConfiguration classConfig)
    {
        if (!ForeverVerifiedSetup.IsProfile(classConfig.FileName))
            throw new NotSupportedException(
                $"Forever profile '{classConfig.FileName}' has not been verified live.");
        if (ForeverProfileRules.FindUnsupportedProfile(classConfig) is { } unsupported)
            throw new NotSupportedException(unsupported);

        // GoapAgent's collaborators, minus the Classic grind session, bag and
        // coin logging, and blacklists.
        services.AddScoped<IGrindSessionHandler, NoGrindSessionHandler>();
        services.AddScoped<LevelChangeTracker>();
        services.AddScoped<IBagChangeTracker, NoBagChangeTracker>();
        services.AddScoped<IMoneyChangeTracker, NoMoneyChangeTracker>();
        services.AddScoped<IBlacklist, NoBlacklist>();
        services.AddKeyedScoped<IBlacklist, NoBlacklist>(TARGET);
        services.AddKeyedScoped<IBlacklist, NoBlacklist>(MOUSE_OVER);

        // GoapAgent also needs a stop-moving and mount handler; neither runs on Forever.
        services.AddScoped<StopMoving>();
        services.AddScoped<IMountHandler, MountHandler>();
        services.AddScoped<ForeverCaster>();

        if (ForeverVerifiedSetup.IsAutoProfile(classConfig.FileName))
        {
            services.AddSingleton(sp.GetService<ForeverRunOptions>() ?? new ForeverRunOptions());
            services.AddScoped<ForeverTargetSelector>();
            services.AddScoped<ForeverNavigator>();
            services.AddScoped<IForeverFacing>(scope => scope.GetRequiredService<ForeverNavigator>());
            services.AddScoped(_ => new MouseCameraInput(sp.GetRequiredService<WowProcess>()));
            services.AddScoped<IMousePointer>(scope => new MousePointerInput(
                scope.GetRequiredService<MouseCameraInput>(),
                scope.GetRequiredService<IMouseInput>(),
                scope.GetRequiredService<IWowScreen>()));
            services.AddScoped<IForeverCombatRoutine, ForeverRotation>();
            services.AddScoped<ForeverCorpses>();
            services.AddScoped<ForeverMobMemory>();
            services.AddScoped<IForeverLootRoutine, ForeverLoot>();

            // The activities the controller's selector picks from, and the
            // agenda whose tasks run when none of the others wants to.
            services.AddScoped<ForeverEngageActivity>();
            services.AddScoped<ForeverThreatActivity>();
            services.AddScoped<ForeverLootActivity>();
            services.AddScoped<ForeverRestActivity>();
            services.AddScoped<ForeverTaskActivity>();
            services.AddScoped(scope => ForeverAgenda.For(scope.GetRequiredService<ForeverRunOptions>()));
            services.AddScoped<GoapGoal, ForeverAutonomousGoal>();
        }
        else
            services.AddScoped<GoapGoal, ForeverTrialGoal>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }
}
