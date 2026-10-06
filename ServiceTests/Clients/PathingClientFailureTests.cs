using Contracts;
using Contracts.Pathing;

using ServiceClients.Pathing;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The pathing client reads a route only from what the route endpoint writes (known issue 36): a
/// refusal from in front of it, or a body it cannot read, is a failure with the service's words,
/// never a route and never "could not be reached".
/// </summary>
public sealed class PathingClientFailureTests
{
    private static readonly RouteRequest Route = new(null, 1429, CoordinateSpace.World,
        new Point3(-8898.32f, -117.35608f, 81.840546f), new Point3(-8779.58f, -106.12012f, 0));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"title":"An error occurred","status":500,"detail":"the planner threw"}""", "the planner threw")]
    [InlineData(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401,"detail":"this machine needs the key"}""", "needs the key")]
    public async Task A_refusal_from_in_front_of_the_route_is_unavailable_with_its_words(HttpStatusCode status, string problem, string words)
    {
        RouteResponse route = await Find(Answer(status, "application/problem+json", problem));

        Assert.Equal(RouteStatus.Unavailable, route.Status);
        Assert.Contains(words, route.Detail);
        Assert.Empty(route.Points);
    }

    [Fact]
    public async Task A_route_this_client_cannot_read_is_a_failure_with_the_body()
    {
        RouteResponse unknownStatus = await Find(Answer(HttpStatusCode.OK, "application/json",
            """{"status":"Teleported","points":[],"smoothed":false,"elapsedMs":1,"detail":null}"""));
        RouteResponse noStatus = await Find(Answer(HttpStatusCode.OK, "application/json", """{"title":"odd"}"""));
        RouteResponse foundNothing = await Find(Answer(HttpStatusCode.OK, "application/json",
            """{"status":"Found","points":[],"smoothed":true,"elapsedMs":1,"detail":null}"""));

        Assert.Equal(RouteStatus.Unavailable, unknownStatus.Status);
        Assert.Contains("Teleported", unknownStatus.Detail);
        Assert.DoesNotContain("could not be reached", unknownStatus.Detail);
        Assert.Equal(RouteStatus.Unavailable, noStatus.Status);
        Assert.Contains("odd", noStatus.Detail);
        Assert.Equal(RouteStatus.Unavailable, foundNothing.Status);
    }

    [Fact]
    public async Task A_route_the_endpoint_wrote_is_read_whatever_its_status()
    {
        RouteResponse busy = await Find(Answer(HttpStatusCode.ServiceUnavailable, "application/json",
            """{"status":"Unavailable","points":[],"smoothed":false,"elapsedMs":10000,"detail":"Azeroth stayed busy for 10 s"}"""));

        Assert.Equal(RouteStatus.Unavailable, busy.Status);
        Assert.Equal("Azeroth stayed busy for 10 s", busy.Detail);
    }

    [Fact]
    public async Task A_point_that_is_not_a_number_is_a_bad_request_and_is_not_sent()
    {
        StubService service = new(_ => throw new InvalidOperationException("nothing should be sent"));
        PathingClient client = new(new HttpClient(service) { BaseAddress = new Uri("http://pathing.test/") });

        RouteResponse nan = await client.FindRouteAsync(Route with { From = new Point3(float.NaN, 0, 0) }, TestContext.Current.CancellationToken);
        RouteResponse jitter = await client.FindRouteAsync(Route with { Tuning = new RouteTuning(float.PositiveInfinity) },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteStatus.BadRequest, nan.Status);
        Assert.Equal(RouteStatus.BadRequest, jitter.Status);
        Assert.Equal(0, service.Attempts);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Answer(HttpStatusCode status, string mediaType, string body)
    {
        return _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    private static Task<RouteResponse> Find(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        PathingClient client = new(new HttpClient(new StubService(answer)) { BaseAddress = new Uri("http://pathing.test/") });
        return client.FindRouteAsync(Route, TestContext.Current.CancellationToken);
    }
}
