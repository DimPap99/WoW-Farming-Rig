using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using PathingService.Hosting;

using ServiceHosting;

namespace ServiceTests.Api;

/// <summary>
/// The Pathing Service in memory, serving <paramref name="dataRoot"/> for <paramref name="client"/> instead
/// of its data_config.json.
/// </summary>
internal sealed class PathingServiceFactory(string dataRoot, string client = TestData.Client) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ServiceLogging.FileKey, string.Empty);
        builder.ConfigureTestServices(services =>
            services.AddSingleton(new DataConfig { Root = dataRoot, Exp = client }));
    }
}
