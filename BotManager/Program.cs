using BotManager.Hosting;

using Contracts.Bots;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

using ServiceHosting;

namespace BotManager;

/// <summary>
/// The entry point. A class in a namespace rather than top-level statements, so a test can host the
/// manager beside the Pathing Service, whose own entry point is the global <c>Program</c>.
/// </summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Every registration is checked at startup, on the container that is actually used.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
        {
            builder.WebHost.UseUrls(BotManagerApi.DefaultUrl);
        }

        builder.AddServiceLogging();
        builder.Services.AddBotManager();

        WebApplication app = builder.Build();
        app.UseBotManager();
        app.Run();
    }
}
