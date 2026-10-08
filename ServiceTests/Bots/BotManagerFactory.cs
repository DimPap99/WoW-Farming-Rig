using BotManager.Bots;
using BotManager.Hosting;

using Contracts;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using ServiceHosting;

using ServiceTests.Api;

using System.Globalization;

namespace ServiceTests.Bots;

/// <summary>The Bot Manager in memory, keeping its data in <paramref name="dataFolder"/>.</summary>
internal sealed class BotManagerFactory(string dataFolder, string? apiKey = null, double commandTimeoutSeconds = 2, bool trustLoopback = true)
    : WebApplicationFactory<BotManager.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ServiceLogging.FileKey, string.Empty);
        builder.UseSetting(BotManagerRegistration.DataKey, dataFolder);
        builder.UseSetting(BotFleetOptions.CommandTimeoutKey, commandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        if (apiKey is not null)
        {
            builder.UseSetting(ServiceKey.ConfigKey, apiKey);
        }

        if (!trustLoopback)
        {
            builder.UseSetting(ServiceKey.TrustLoopbackKey, "false");
        }

        builder.ConfigureServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddress>());
    }
}
