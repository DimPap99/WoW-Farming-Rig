using Microsoft.JSInterop;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Components.Shared;

/// <summary>The browser's own dialogs.</summary>
public static class Dialogs
{
    /// <summary>Asks a yes or no question; no when the page went away before an answer.</summary>
    public static async Task<bool> ConfirmAsync(this IJSRuntime js, string question, CancellationToken token)
    {
        try
        {
            return await js.InvokeAsync<bool>("confirm", token, question);
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException or OperationCanceledException)
        {
            return false;
        }
    }
}
