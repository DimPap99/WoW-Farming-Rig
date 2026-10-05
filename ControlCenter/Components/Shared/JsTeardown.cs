using Microsoft.JSInterop;

using System;
using System.Threading.Tasks;

namespace ControlCenter.Components.Shared;

public static class JsTeardown
{
    /// <summary>
    /// Calls a JS cleanup function while a component goes away. There is nothing left to clean when
    /// the circuit is already gone (the tab was closed), so that failure is ignored.
    /// </summary>
    public static async ValueTask InvokeTeardownAsync(this IJSRuntime js, string identifier)
    {
        try
        {
            await js.InvokeVoidAsync(identifier);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
