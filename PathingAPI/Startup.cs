using Frontend;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

using PathingAPI.Hosting;

using PPather;

using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Logging;

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace PathingAPI;

public sealed class Startup(IConfiguration configuration)
{
    private readonly ServerArguments arguments = ServerArguments.From(configuration);

    public void ConfigureServices(IServiceCollection services)
    {
        AddLogging(services);

        Log.Information(DateTimeOffset.Now.ToString());
        Log.Information("Expansion: {Exp}", arguments.Exp);

        services.AddRazorPages();
        services.AddServerSideBlazor();

        services.AddPathing(configuration, arguments);
        services.AddAnTcpListener(configuration);

        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions);
        services.Configure<JsonOptions>(options => options.SerializerOptions.AddPathingConverters());

        // Pull in the Frontend authoring controllers (road / danger zone) so the
        // cost-zone edit loop works against this host, which needs no game client.
        services.AddControllers()
            .AddApplicationPart(typeof(Frontend.Controllers.RoadController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.AddPathingConverters());

        services.AddSignalR()
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions.WithCompression(MessagePack.MessagePackCompression.Lz4BlockArray);
            });

        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Pathing API", Version = "v1" });

            string xmlDocumentPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xmlDocumentPath))
            {
                c.IncludeXmlComments(xmlDocumentPath);
            }
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        StartRequestedBakes(app.ApplicationServices.GetRequiredService<PPatherService>());

        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "PPather API V1"));

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

        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<WatchHub>(WatchHub.Url);
            endpoints.MapBlazorHub();
            endpoints.MapFallbackToPage("/_Host");
            endpoints.MapControllers();
            endpoints.MapRazorPages();
        });
    }

    private void AddLogging(IServiceCollection services)
    {
        PathingAPILoggerSink sink = new();
        services.AddSingleton(sink);

        // Logging:PatherDebug=true opens the per-query navmesh diagnostics
        // (corridor legs, tile bakes) without recompiling.
        LogEventLevel minimum = configuration.GetValue<bool>("Logging:PatherDebug")
            ? LogEventLevel.Debug
            : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.With<ShortSourceContextEnricher>()
            .WriteTo.Sink(sink)
            .WriteTo.File(new ExpressionTemplate(LogOutputTemplates.Default),
                "out.log",
                rollingInterval: RollingInterval.Day)
            .WriteTo.Debug(new ExpressionTemplate(LogOutputTemplates.Default))
            .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Default, theme: TemplateTheme.Literate))
            .CreateLogger();

        ILoggerFactory logFactory = LoggerFactory.Create(builder => builder.ClearProviders().AddSerilog());
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(logFactory.CreateLogger(nameof(Program)));
    }

    /// <summary>
    /// <c>--bake=&lt;continent&gt;</c> starts a background navmesh bake as the server comes
    /// up (progress on GET api/PPather/Bake/Status); <c>--bake-area=&lt;continent&gt;</c>
    /// extracts the standalone area-id grid so area lookups need no game files.
    /// Either takes <c>all</c> for every continent. The API serves immediately.
    /// </summary>
    private void StartRequestedBakes(PPatherService service)
    {
        if (arguments.Bake is string bake)
        {
            string? continent = ServerArguments.Continent(bake);
            if (service.StartBake(continent, null))
                Log.Information("Startup bake requested: {Scope} (expansion {Exp})", continent ?? "all continents", arguments.Exp);
            else
                Log.Warning("Startup bake could not start - a bake is already running.");
        }

        if (arguments.BakeArea is string bakeArea)
        {
            string? continent = ServerArguments.Continent(bakeArea);
            Log.Information("Startup area-grid bake requested: {Scope}", continent ?? "all continents");
            _ = Task.Run(() => service.BuildAreaGrid(continent));
        }
    }
}
