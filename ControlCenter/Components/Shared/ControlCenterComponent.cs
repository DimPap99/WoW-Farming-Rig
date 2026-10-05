using Microsoft.AspNetCore.Components;

using System;
using System.Collections.Generic;
using System.Threading;

namespace ControlCenter.Components.Shared;

/// <summary>
/// The base of the Control Center's components: subscriptions that are undone when the component
/// goes away, a redraw that is safe from any thread, and a <see cref="Token"/> cancelled then.
/// <see cref="Pathing.PathingStatus"/> raises its events on its own thread, which is why
/// <see cref="Redraw"/> marshals onto the renderer.
/// <para>
/// A component that awaits its teardown (JS interop) implements <see cref="IAsyncDisposable"/>
/// and calls <see cref="Dispose"/> itself: Blazor calls only <c>DisposeAsync</c> on a component
/// that has both.
/// </para>
/// </summary>
public abstract class ControlCenterComponent : ComponentBase, IDisposable
{
    private readonly List<Action> cleanup = [];
    private CancellationTokenSource? cts;

    /// <summary>Cancelled when the component goes away, so a page left mid-call stops waiting.</summary>
    protected CancellationToken Token
    {
        get
        {
            if (cts is null)
            {
                // Cancelled but not disposed: a continuation that reads Token after the
                // component is gone gets a cancelled token, not an exception.
                CancellationTokenSource created = new();
                cts = created;
                cleanup.Add(created.Cancel);
            }

            return cts.Token;
        }
    }

    /// <summary>Redraws the component; safe to call from any thread.</summary>
    protected void Redraw()
    {
        _ = InvokeAsync(StateHasChanged);
    }

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
