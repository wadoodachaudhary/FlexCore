using Microsoft.JSInterop;
namespace Fx.ControlKit;

/// <summary>Lazy access to DOM-only capabilities. No application service registration is needed.</summary>
internal sealed class BrowserInterop(IJSRuntime runtime) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    public async ValueTask<T> InvokeAsync<T>(string action, object? element = null, object? data = null, object? callback = null)
    {
        _module ??= await runtime.InvokeAsync<IJSObjectReference>("import", "./_content/FlexCore/browser-capabilities.js");
        return await _module.InvokeAsync<T>("invoke", action, element, data, callback);
    }
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
