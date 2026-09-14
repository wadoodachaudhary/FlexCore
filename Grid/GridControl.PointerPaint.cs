using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    private readonly HashSet<Task> _pendingPointerSelections = [];
    private long _pointerPaintAcknowledgedGeneration;
    private bool _pointerPaintRenderPending;

    private async Task TrackPointerSelectionAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPointerSelections.Add(completion.Task);
        try
        {
            await action();
        }
        finally
        {
            _pendingPointerSelections.Remove(completion.Task);
            completion.SetResult();
        }
    }

    // Sent after the native click dispatch. Await its existing handlers, including
    // host validation, then acknowledge the resulting render without reselecting.
    [JSInvokable]
    public async Task AcknowledgePointerSelectionPaintAsync(long generation)
    {
        if (generation <= _pointerPaintAcknowledgedGeneration) return;
        if (_pendingPointerSelections.Count > 0)
            await Task.WhenAll(_pendingPointerSelections.ToArray());

        if (generation <= _pointerPaintAcknowledgedGeneration) return;
        _pointerPaintAcknowledgedGeneration = generation;
        _pointerPaintRenderPending = true;
        await InvokeAsync(StateHasChanged);
    }
}
