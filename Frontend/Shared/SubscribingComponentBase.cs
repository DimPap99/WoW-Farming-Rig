using Microsoft.AspNetCore.Components;

namespace Frontend.Shared;

/// <summary>
/// A component that redraws on events raised by the bot's readers and services.
/// Those fire on their own threads, so <see cref="Redraw"/> marshals onto the
/// renderer, and <see cref="Subscribe"/> pairs every subscription with its
/// removal, which runs when the component is disposed. Hand-written pairs went
/// wrong: a component with a <c>Dispose</c> method but no <c>@implements
/// IDisposable</c> is never disposed, and each visit to its page left a handler
/// attached for the life of the server.
/// <para>
/// A component that awaits its teardown (JS interop) implements
/// <see cref="IAsyncDisposable"/> itself instead of inheriting this: Blazor
/// calls only <c>DisposeAsync</c> on a component that has both, so the cleanup
/// here would never run.
/// </para>
/// </summary>
public abstract class SubscribingComponentBase : ComponentBase, IDisposable
{
    private readonly List<Action> cleanup = [];

    /// <summary>Redraws the component; safe to call from any thread.</summary>
    protected void Redraw() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Runs <paramref name="subscribe"/> now and <paramref name="unsubscribe"/> when the component goes away.</summary>
    protected void Subscribe(Action subscribe, Action unsubscribe)
    {
        subscribe();
        cleanup.Add(unsubscribe);
    }

    /// <summary>Runs <paramref name="action"/> when the component goes away, the last added first.</summary>
    protected void OnDispose(Action action)
    {
        cleanup.Add(action);
    }

    public void Dispose()
    {
        for (int i = cleanup.Count - 1; i >= 0; i--)
        {
            cleanup[i]();
        }
        cleanup.Clear();
        GC.SuppressFinalize(this);
    }
}
