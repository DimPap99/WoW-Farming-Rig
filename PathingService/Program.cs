using Contracts.Pathing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

using PathingService.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Every registration is checked at startup, on the container that is actually used.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls(PathingApi.DefaultUrl);
}

builder.AddPathingLogging();
builder.Services.AddPathingService(builder.Configuration);

WebApplication app = builder.Build();
app.UsePathingService();
app.Run();

/// <summary>The entry point; public so the tests' WebApplicationFactory can start the service.</summary>
public partial class Program;
