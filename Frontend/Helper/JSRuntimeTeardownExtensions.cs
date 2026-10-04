using Microsoft.JSInterop;

namespace Frontend;

public static class JSRuntimeTeardownExtensions
{
    /// <summary>
    /// Calls a JS cleanup function while a component goes away. There is nothing
    /// left to clean when the circuit is already gone (the tab was closed) or never
    /// connected (the component was only prerendered), so those failures are ignored.
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
