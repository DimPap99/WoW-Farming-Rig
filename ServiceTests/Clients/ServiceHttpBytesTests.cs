using ServiceClients;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>A file's bytes, at most <see cref="ServiceHttp.MaxFileBytes"/> (known issue 77): any size was buffered, up to 2 GB.</summary>
public sealed class ServiceHttpBytesTests
{
    [Fact]
    public async Task An_answer_larger_than_a_file_may_be_is_refused_before_it_is_read()
    {
        HttpClient http = new(new Answer(ServiceHttp.MaxFileBytes + 1)) { BaseAddress = new System.Uri("http://localhost:5010") };

        ServiceResult<byte[]> got = await new ServiceHttp(http, "pathing service").GetBytesAsync("/api/v1/sync/paths/files/x.json",
            TestContext.Current.CancellationToken);

        Assert.False(got.Ok);
        Assert.Equal(200, got.Status);
        Assert.Contains("more than a file may be", got.Error);
    }

    // A small body that says it is larger: nothing past the headers may be read.
    private sealed class Answer(long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ByteArrayContent content = new([1, 2, 3]);
            content.Headers.ContentLength = length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
