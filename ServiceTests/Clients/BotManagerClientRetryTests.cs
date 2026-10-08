using Contracts;
using Contracts.Bots;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The registered client of the Bot Manager, on the same stack as the pathing client's (known issue 46): it
/// repeated a query the manager answered 500, and its breaker counted the manager's own 504 ("the agent did
/// not answer") as the manager being out of reach. A command is never repeated.
/// </summary>
public sealed class BotManagerClientRetryTests
{
    private const string AgentId = "3f2c9a1e5b7d4c6a8e0f1a2b3c4d5e6f";

    [Theory]
    [InlineData(HttpStatusCode.OK, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData(HttpStatusCode.BadGateway, 1 + ServiceClientRegistration.Retries)]
    public async Task A_query_is_asked_again_only_when_a_retry_can_help(HttpStatusCode status, int attempts)
    {
        StubService service = new(_ => Answer(status, Array.Empty<BotInfo>()));
        await using ServiceProvider provider = Client(service);

        await provider.GetRequiredService<IBotManagerClient>().ListBotsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(attempts, service.Attempts);
    }

    [Fact]
    public async Task A_command_is_never_repeated()
    {
        StubService service = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using ServiceProvider provider = Client(service);

        await provider.GetRequiredService<IBotManagerClient>().StopAsync(AgentId, TestContext.Current.CancellationToken);

        Assert.Equal(1, service.Attempts);
    }

    [Fact]
    public async Task Agents_that_do_not_answer_do_not_shut_the_client_out()
    {
        HttpStatusCode status = HttpStatusCode.GatewayTimeout;
        StubService service = new(_ => Answer(status, Array.Empty<BotInfo>()));
        await using ServiceProvider provider = Client(service);
        IBotManagerClient client = provider.GetRequiredService<IBotManagerClient>();

        // Past the breaker's minimum throughput (100), every one of them the manager's 504.
        for (int i = 0; i < 120; i++)
        {
            await client.StopAsync(AgentId, TestContext.Current.CancellationToken);
        }

        status = HttpStatusCode.OK;
        ServiceResult<BotInfo[]> listed = await client.ListBotsAsync(TestContext.Current.CancellationToken);

        Assert.True(listed.Ok, listed.Error);
    }

    private static HttpResponseMessage Answer<T>(HttpStatusCode status, T body)
    {
        return new HttpResponseMessage(status) { Content = JsonContent.Create(body, options: ContractJson.Options) };
    }

    // The client as the Control Center registers it, over a stub, with no wait between attempts.
    private static ServiceProvider Client(StubService service)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BotManagerOptions.UrlKey] = "http://manager.test/" }).Build());
        IHttpClientBuilder builder = services.AddBotManagerClient().ConfigurePrimaryHttpMessageHandler(() => service);
        services.Configure<HttpStandardResilienceOptions>($"{builder.Name}-standard", options => options.Retry.Delay = TimeSpan.Zero);
        return services.BuildServiceProvider();
    }
}
