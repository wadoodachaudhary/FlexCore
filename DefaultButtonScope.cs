namespace Fx.ControlKit;

/// <summary>Cascaded by a dialog with a default button (<see cref="DialogControl.OnEnter"/>). A control
/// that keeps a plain Enter for itself (an entry it will not commit, a popup it opens instead) calls
/// <see cref="ConsumeEnter"/>, and the default button skips that key. A control that decides
/// asynchronously hands its Enter task to <see cref="Defer"/> so the default button waits for it.</summary>
public sealed class DefaultButtonScope
{
    private readonly List<Task> _pending = new();
    private long _consumedAt;

    /// <summary>This Enter belongs to the calling control; the default button does not fire for it.</summary>
    public void ConsumeEnter() => _consumedAt = Environment.TickCount64;

    /// <summary>The default button waits for <paramref name="decision"/> before reading <see cref="ConsumeEnter"/>.</summary>
    public void Defer(Task decision)
    {
        if (!decision.IsCompleted)
            _pending.Add(decision);
    }

    internal async Task<bool> TakeConsumedEnterAsync()
    {
        while (_pending.Count > 0)
        {
            var pending = _pending.ToArray();
            _pending.Clear();
            try { await Task.WhenAll(pending); } catch { /* the control reports its own failure */ }
        }

        // A consume older than the key it belonged to (a control that kept an Enter the dialog
        // never saw, inside a grid say) must not swallow a later one.
        var consumed = _consumedAt != 0 && Environment.TickCount64 - _consumedAt < 2000;
        _consumedAt = 0;
        return consumed;
    }
}
