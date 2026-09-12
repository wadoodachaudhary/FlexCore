using Microsoft.JSInterop;
namespace Fx.ControlKit;

/// <summary>Lazy access to DOM-only capabilities. No application service registration is needed.</summary>
internal sealed class BrowserInterop(IJSRuntime runtime) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    public async ValueTask<T> InvokeAsync<T>(string action, object? element = null, object? data = null, object? callback = null)
    {
        // Teardown must not import a module that never loaded successfully.
        if (action == "dispose" && _module is null) return default!;
        // The path follows the assembly (FlexKit or FlexCore) — a literal 404s in the
        // other library, and the failure is silent.
        _module ??= await runtime.InvokeAsync<IJSObjectReference>(
            "import", $"./_content/{typeof(BrowserInterop).Assembly.GetName().Name}/browser-capabilities.js");
        try { return await _module.InvokeAsync<T>("invoke", action, element, data, callback); }
        catch (Exception error) when (action == "dispose" && error is JSException or JSDisconnectedException or TaskCanceledException)
        {
            return default!;
        }
    }
    public async ValueTask DisposeAsync()
    {
        var module = _module;
        _module = null;
        if (module is null) return;
        try { await module.DisposeAsync(); }
        catch (Exception error) when (error is JSException or JSDisconnectedException or TaskCanceledException) { }
    }
}
