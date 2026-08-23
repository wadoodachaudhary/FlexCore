using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Builds <see cref="EventCallback{T}"/>s that run their handler WITHOUT
/// Blazor's implicit post-event <c>StateHasChanged</c>, regardless of how the
/// handler is written.
/// <para>
/// Why this exists: <c>EventCallback.Factory.Create(receiver, handler)</c>
/// resolves the receiver as <c>handler.Target as IHandleEvent ?? receiver</c>.
/// A method group, or a lambda that captures only <c>this</c>, has the owning
/// component as <c>Delegate.Target</c>, so the supplied non-rendering marker
/// receiver is ignored and the component re-renders after every event anyway —
/// silently, and depending on lambda shape and what else sits in the same
/// scope. Here the handler is stored in a private receiver object that
/// implements <see cref="IHandleEvent"/> and simply forwards the call, and the
/// delegate handed to Blazor targets that receiver, so the outcome no longer
/// depends on the caller's delegate shape (Microsoft's documented
/// "AsNonRenderingEventHandler" pattern).
/// </para>
/// Use it for bookkeeping/arming handlers (mouseup, focusout, selection
/// notifications) that render explicitly where they act; keep normal
/// component-receiver callbacks for handlers that must repaint.
/// </summary>
public static class NonRenderingEventHandler
{
    public static EventCallback<T> Create<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var receiver = new SyncReceiver<T>(handler);
        return EventCallback.Factory.Create<T>(receiver, (Action<T>)receiver.Invoke);
    }

    public static EventCallback<T> Create<T>(Func<T, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var receiver = new AsyncReceiver<T>(handler);
        return EventCallback.Factory.Create<T>(receiver, (Func<T, Task>)receiver.Invoke);
    }

    private abstract class ReceiverBase : IHandleEvent
    {
        public Task HandleEventAsync(EventCallbackWorkItem item, object? arg) => item.InvokeAsync(arg);
    }

    private sealed class SyncReceiver<T> : ReceiverBase
    {
        private readonly Action<T> _handler;
        public SyncReceiver(Action<T> handler) => _handler = handler;
        public void Invoke(T arg) => _handler(arg);
    }

    private sealed class AsyncReceiver<T> : ReceiverBase
    {
        private readonly Func<T, Task> _handler;
        public AsyncReceiver(Func<T, Task> handler) => _handler = handler;
        public Task Invoke(T arg) => _handler(arg);
    }
}
