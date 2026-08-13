namespace Fx.ControlKit.Grid;

/// <summary>EventCallback receiver that is NOT IHandleEvent: callbacks created
/// on it skip Blazor's implicit post-event StateHasChanged. Used for pointer
/// handlers that only arm state (mousedown / mouseup), so one physical click
/// costs ONE authoritative render (the click) instead of three. Handlers that
/// do change the DOM on these events render explicitly.</summary>
public sealed class NonRenderingEventReceiver
{
    public static readonly NonRenderingEventReceiver Instance = new();

    private NonRenderingEventReceiver()
    {
    }
}
