using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTests.Clients;

/// <summary>A service that answers every request with <paramref name="answer"/> and counts the requests.</summary>
internal sealed class StubService(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    private int attempts;

    public int Attempts => Volatile.Read(ref attempts);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref attempts);
        try
        {
            return Task.FromResult(answer(request));
        }
        catch (HttpRequestException ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
