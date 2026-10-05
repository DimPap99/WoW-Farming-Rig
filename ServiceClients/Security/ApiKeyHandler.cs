using Contracts;

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceClients.Security;

/// <summary>Sends the service's key (<see cref="ServiceKey.Header"/>) with every request, when there is one.</summary>
public sealed class ApiKeyHandler(string? key) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Remove(ServiceKey.Header);
            request.Headers.TryAddWithoutValidation(ServiceKey.Header, key);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
