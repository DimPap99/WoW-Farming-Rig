using ControlCenter.Hosting;

using Contracts.ControlCenter;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

using ServiceHosting;

namespace ControlCenter;

/// <summary>
/// The entry point. A class rather than top-level statements, so a test can host the Control
/// Center beside the Pathing Service, whose own entry point is the global <c>Program</c>.
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
            builder.WebHost.UseUrls(ControlCenterDefaults.DefaultUrl);
        }

        builder.AddServiceLogging();
        builder.Services.AddControlCenter();

        WebApplication app = builder.Build();
        app.UseControlCenter();
        app.Run();
    }
}
