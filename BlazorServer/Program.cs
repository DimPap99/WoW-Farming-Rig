using Contracts;
using Contracts.Pathing;

using Core;

using Frontend;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Serilog;

using ServiceClients.Pathing;
using ServiceClients.Security;

using ServiceHosting;

using SharedLib.Logging;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Converters;

using System;
using System.IO;
using System.Threading;

using Yarp.ReverseProxy.Transforms;

namespace BlazorServer;

public static class Program
{
    public static void Main(string[] args)
    {
        while (true)
        {
            bool shutdownRequested = false;

            try
            {
                Log.Information("[Program          ] Starting blazor server");
                var host = CreateApp(args);
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                lifetime.ApplicationStopping.Register(() =>
                {
                    shutdownRequested = true;
                    Log.Warning("[Program          ] Graceful shutdown requested");
                });

                host.Run();
            }
            catch (Exception ex)
            {
                if (shutdownRequested)
                {
                    // We were stopping anyway; don't restart-loop just because Dispose threw.
                    Log.Error(ex, "[Program          ] Exception during shutdown; exiting without restart");
                    break;
                }

                Log.Fatal(ex, "[Program          ] Host crashed – restarting in 3s");
                Thread.Sleep(3000);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }

    private static WebApplication CreateApp(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders().AddSerilog();

        ConfigureServices(builder.Configuration, builder.Services);

        return ConfigureApp(builder, builder.Environment);
    }

    private static void ConfigureServices(IConfiguration configuration, IServiceCollection services)
    {
        ILoggerFactory logFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders().AddSerilog();
        });

        services.AddLogging(builder =>
        {
            LoggerSink sink = new();
            builder.Services.AddSingleton(sink);

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Enrich.FromLogContext()
                .Enrich.With<ShortSourceContextEnricher>()
                .WriteTo.Sink(sink)
                .WriteTo.File(new ExpressionTemplate(LogOutputTemplates.Default),
                    "out.log",
                    rollingInterval: RollingInterval.Day)
                .WriteTo.Debug(new ExpressionTemplate(LogOutputTemplates.Default))
                .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Default, theme: TemplateTheme.Literate))
                .CreateLogger();

            builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(logFactory.CreateLogger(string.Empty));
        });

        Microsoft.Extensions.Logging.ILogger log = logFactory.CreateLogger("Program");

        if (log.IsEnabled(LogLevel.Information))
        {
            log.LogInformation("{Language} {Timestamp}",
                Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName,
                DateTimeOffset.Now);
        }

        services.AddStartupConfigurations(configuration);

        services.AddWoWProcess(log, out bool isForever);

        services.AddCoreBase(log);

        // Forever uses a read-only visual channel; it does not have classic
        // addon/frame configuration files to validate.
        if (isForever || (AddonConfig.Exists() && FrameConfig.Exists()))
        {
            services.AddCoreNormal(log);
        }
        else
        {
            services.AddCoreConfiguration(log);
        }

        services.AddFrontend();

        // Forever: the server stays read-only and starts runs as a child process.
        if (isForever)
        {
            services.AddSingleton<ForeverRunHost>();
        }

        services.AddCoreFrontend();

        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions);

        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.Converters.Add(new Vector3Converter());
            options.SerializerOptions.Converters.Add(new Vector2Converter());
            options.SerializerOptions.Converters.Add(new Vector4Converter());
        });

        services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = TimeSpan.FromSeconds(1);
        });

        // Register mDNS advertising service for http://wowbot.local access
        if(Environment.GetEnvironmentVariable("USE_MDNS") != null)
        {
            services.AddHostedService<MdnsAdvertisingService>();
        }

        // The map authors recorded paths and cost zones through the Pathing Service's API,
        // forwarded so the map's script stays same-origin (see ConfigureApp).
        services.AddPathingServiceOptions();
        services.AddHttpForwarder();

        services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static WebApplication ConfigureApp(WebApplicationBuilder builder, IWebHostEnvironment env)
    {
        WebApplication app = builder.Build();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
        }

        app.UseStaticFiles();

        app.UseCustomStaticFiles(env);

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(Frontend._Imports).Assembly);

        app.UseRouting();

        app.UseAntiforgery();

        // The dashboard drives the bot for whoever reaches it: not a page of another site that a DNS
        // rebinding pointed at this machine (known issue 75).
        app.UseHostCheck();
        MapPathingForward(app);

        return app;
    }

    // The map's calls to the Pathing Service (/api/v1), forwarded. No service running answers 502,
    // which the map reports as "offline". The service's key, when it has one, goes on every forwarded
    // request, so the browser never holds it - which makes this host a door to the service: the
    // service serves this machine and its key whoever stands behind it, and this host listens on the
    // LAN. So the door applies the rules to its own caller first (ForwardPolicy): this machine gets
    // anything; another machine reads, and authors what the map authors (paths, roads, danger zones),
    // but deletes no tiles and starts no bake without the key; no page on another site writes at all.
    private static void MapPathingForward(WebApplication app)
    {
        PathingServiceOptions pathing;
        try
        {
            pathing = app.Services.GetRequiredService<PathingServiceOptions>();
        }
        catch (InvalidOperationException ex)
        {
            // A bad PathingService:Url must not keep the bot from starting: the map says why it is offline.
            app.Logger.LogError("The map's Pathing Service is not forwarded: {Reason}", ex.Message);
            app.Map($"{PathingApi.Root}/{{**catch-all}}", () => Results.Problem(detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway, title: "Pathing Service not configured"));
            return;
        }

        // Another machine authors what the map authors (paths, roads, danger zones) besides reads.
        app.UseForwardGuard(path => path.StartsWithSegments(PathingApi.Root), pathing.ApiKey, ForwardPolicy.LeafletMapAuthors);
        app.MapForwarder($"{PathingApi.Root}/{{**catch-all}}", pathing.Url.GetLeftPart(UriPartial.Authority),
            transforms => ForwardGuard.Forwarding(transforms, pathing.ApiKey));
    }

}
