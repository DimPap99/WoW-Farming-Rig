using Core.Database;

using ForeverCasc;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PathingAPI.AnTcp;
using PathingAPI.Catalog;
using PathingAPI.Pathing;
using PathingAPI.Status;
using PathingAPI.Watch;

using PPather;
using PPather.Storage;

using Serilog;

using SharedLib;
using SharedLib.Converters;

using System;
using System.IO;
using System.Text.Json;

namespace PathingAPI.Hosting;

public static class PathingServiceCollectionExtensions
{
    public const string ForeverCascSection = "ForeverCasc";
    public const string DefaultForeverProduct = "wow_classic_beta";

    /// <summary>The AnTCP port the bot's shipped <c>Pathing:portv3</c> expects; AmeisenNavigation's own default is 47110.</summary>
    public const int DefaultAnTcpPort = 47111;

    /// <summary>
    /// The engine, the gate every caller shares, the watch stream and the UI's data.
    /// </summary>
    public static IServiceCollection AddPathing(this IServiceCollection services,
        IConfiguration configuration, ServerArguments arguments)
    {
        services.AddSingleton(_ => DataConfig.Load(arguments.Exp));
        services.AddSingleton<WorldMapAreaDB>();

        services.Configure<NavmeshBakeOptions>(configuration.GetSection(NavmeshBakeOptions.Position));
        services.Configure<NavmeshQueryOptions>(configuration.GetSection(NavmeshQueryOptions.Position));
        services.Configure<SplineFollowerOptions>(configuration.GetSection(SplineFollowerOptions.Position));

        Func<IGameFileSource>? gameFiles = ForeverGameFiles(configuration, arguments.Exp);

        services.AddSingleton(sp =>
        {
            PPatherService service = new(
                sp.GetRequiredService<ILogger<PPatherService>>(),
                sp.GetRequiredService<DataConfig>(),
                sp.GetRequiredService<WorldMapAreaDB>(),
                sp.GetRequiredService<IOptions<NavmeshBakeOptions>>(),
                sp.GetRequiredService<IOptions<NavmeshQueryOptions>>(),
                gameFiles);

            if (arguments.Engine is PathingEngine engine)
                service.Engine = engine;

            return service;
        });

        services.AddSingleton<PatherGate>();
        services.AddSingleton<RouteSearch>();

        services.AddSingleton<WatchSettings>();
        services.AddSingleton<WatchBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<WatchBroadcaster>());

        services.AddSingleton<ServerStatus>();
        services.AddSingleton<LocationCatalog>();
        services.AddSingleton<RecordedPaths>();

        // AreaDB's loader thread stops on this token.
        services.AddSingleton<System.Threading.CancellationTokenSource>();
        services.AddSingleton<FactionTemplateDB>();
        services.AddSingleton<CreatureDB>();
        services.AddSingleton<AreaDB>();

        return services;
    }

    /// <summary>
    /// Speaks AmeisenNavigation's binary protocol so a <c>RemoteV3</c> bot can use this
    /// server. Off by default: it opens a socket, which a plain HTTP install should not.
    /// </summary>
    public static IServiceCollection AddAnTcpListener(this IServiceCollection services, IConfiguration configuration)
    {
        if (!bool.TryParse(configuration["Pathing:AnTcp:Enabled"], out bool enabled) || !enabled)
            return services;

        string ip = configuration["Pathing:AnTcp:Ip"] ?? "127.0.0.1";
        int port = int.TryParse(configuration["Pathing:AnTcp:Port"], out int p) ? p : DefaultAnTcpPort;

        services.AddSingleton<IHostedService>(sp => new AnTcpPathServer(
            sp.GetRequiredService<ILogger<AnTcpPathServer>>(),
            sp.GetRequiredService<PPatherService>(),
            sp.GetRequiredService<RouteSearch>(),
            sp.GetRequiredService<PatherGate>(),
            ip, port));

        Log.Information("AnTCP path server enabled on {Ip}:{Port}", ip, port);
        return services;
    }

    /// <summary>The converters both the minimal JSON options and MVC's need.</summary>
    public static JsonSerializerOptions AddPathingConverters(this JsonSerializerOptions options)
    {
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new Vector3Converter());
        options.Converters.Add(new Vector2Converter());
        options.Converters.Add(new Vector4Converter());
        return options;
    }

    /// <summary>
    /// WoW: Forever geometry comes from the installed client's local CASC storage, for
    /// on-demand bakes. Without an install the engine serves baked tiles only.
    /// </summary>
    private static Func<IGameFileSource>? ForeverGameFiles(IConfiguration configuration, string exp)
    {
        if (!exp.Equals("forever", StringComparison.OrdinalIgnoreCase))
            return null;

        IConfigurationSection casc = configuration.GetSection(ForeverCascSection);
        string? install = casc["Install"];
        string? listfile = casc["Listfile"];

        if (string.IsNullOrWhiteSpace(install) != string.IsNullOrWhiteSpace(listfile))
            throw new InvalidOperationException("ForeverCasc:Install and ForeverCasc:Listfile must be configured together.");

        if (string.IsNullOrWhiteSpace(install))
        {
            Log.Information("Forever CASC geometry is not configured; navmesh queries will use baked tiles on disk");
            return null;
        }

        string product = string.IsNullOrWhiteSpace(casc["Product"]) ? DefaultForeverProduct : casc["Product"]!;
        string cache = string.IsNullOrWhiteSpace(casc["Cache"])
            ? Path.Combine(Path.GetTempPath(), "WoWFarmingRig", "forever-casc")
            : casc["Cache"]!;

        Log.Information("Forever CASC geometry enabled from {Install}", install);
        return () => new CascGameFileSource(install, product, listfile!, cache);
    }
}
