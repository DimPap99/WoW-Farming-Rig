using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using ServiceClients.Pathing;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The registered pathing client repeats only what a retry can fix (known issue 37): a request that
/// did not reach the service, or a gateway's failure. A busy continent (503) and a planner error
/// (500) are answered at once, and neither shuts the client out of the service.
/// </summary>
public sealed class PathingClientRetryTests
{
    private static readonly RouteRequest Route = new(null, 1429, CoordinateSpace.World,
        new Point3(-8898.32f, -117.35608f, 81.840546f), new Point3(-8779.58f, -106.12012f, 0));

    [Theory]
    [InlineData(HttpStatusCode.OK, 1, RouteStatus.Found)]
    [InlineData(HttpStatusCode.BadRequest, 1, RouteStatus.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, 1, RouteStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, RouteStatus.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, 1 + ServiceClients.ServiceClientRegistration.Retries, RouteStatus.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, 1 + ServiceClients.ServiceClientRegistration.Retries, RouteStatus.Unavailable)]
    public async Task A_route_is_asked_again_only_when_a_retry_can_help(HttpStatusCode status, int attempts, RouteStatus answered)
    {
        StubService service = new(_ => Answer(status));
        await using ServiceProvider provider = Client(service);

        RouteResponse route = await provider.GetRequiredService<IPathingClient>().FindRouteAsync(Route, TestContext.Current.CancellationToken);

        Assert.Equal(attempts, service.Attempts);
        Assert.Equal(answered, route.Status);
    }

    [Fact]
    public async Task A_request_that_did_not_reach_the_service_is_tried_again()
    {
        StubService service = new(_ => throw new HttpRequestException("Connection refused"));
        await using ServiceProvider provider = Client(service);

        RouteResponse route = await provider.GetRequiredService<IPathingClient>().FindRouteAsync(Route, TestContext.Current.CancellationToken);

        Assert.Equal(1 + ServiceClients.ServiceClientRegistration.Retries, service.Attempts);
        Assert.Equal(RouteStatus.Unavailable, route.Status);
        Assert.Contains("Connection refused", route.Detail);
    }

    [Fact]
    public async Task A_busy_continent_does_not_shut_the_client_out()
    {
        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        StubService service = new(_ => Answer(status));
        await using ServiceProvider provider = Client(service);
        IPathingClient client = provider.GetRequiredService<IPathingClient>();

        // Past the breaker's minimum throughput (100), every one of them a 503.
        for (int i = 0; i < 120; i++)
        {
            await client.FindRouteAsync(Route, TestContext.Current.CancellationToken);
        }

        status = HttpStatusCode.OK;
        RouteResponse route = await client.FindRouteAsync(Route, TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Equal(121, service.Attempts);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status)
    {
        RouteResponse body = status switch
        {
            HttpStatusCode.OK => new RouteResponse(RouteStatus.Found, [new Point3(1, 2, 3)], true, 1, null),
            HttpStatusCode.BadRequest => RouteResponse.Failed(RouteStatus.BadRequest, "refused"),
            _ => RouteResponse.Failed(RouteStatus.Unavailable, "busy"),
        };

        return new HttpResponseMessage(status) { Content = JsonContent.Create(body, options: ContractJson.Options) };
    }

    // The client as hosts register it, over a stub, with no wait between attempts.
    private static ServiceProvider Client(StubService service)
    {
        ServiceCollection services = new();
        IHttpClientBuilder builder = services.AddPathingClient(new Uri("http://pathing.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => service);
        services.Configure<HttpStandardResilienceOptions>($"{builder.Name}-standard", options => options.Retry.Delay = TimeSpan.Zero);
        return services.BuildServiceProvider();
    }
}
