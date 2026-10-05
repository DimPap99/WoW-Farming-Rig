using Frontend.Shared;

using System.Threading;

#nullable enable

namespace PathingAPI;

/// <summary>
/// The base of every PathingAPI component: Frontend's subscription and redraw
/// plumbing plus a <see cref="Token"/> cancelled when the component goes away.
/// A component that awaits JS teardown implements <c>IAsyncDisposable</c> and must
/// call <see cref="SubscribingComponentBase.Dispose"/> itself (Blazor then calls
/// only <c>DisposeAsync</c>).
/// </summary>
public abstract class ComponentWithCancellationToken : SubscribingComponentBase
{
    private CancellationTokenSource? cts;

    protected CancellationToken Token
    {
        get
        {
            if (cts == null)
            {
                // Cancelled but not disposed: a continuation that reads Token after
                // the component is gone gets a cancelled token, not an exception.
                CancellationTokenSource created = new();
                cts = created;
                OnDispose(created.Cancel);
            }

            return cts.Token;
        }
    }
}
