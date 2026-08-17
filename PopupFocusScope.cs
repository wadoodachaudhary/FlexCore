namespace Fx.ControlKit;

/// <summary>Lets a popup host hand the opening keyboard focus to one descendant
/// control instead of applying its own generic first-focusable pass.</summary>
public sealed class PopupFocusScope
{
    private Func<bool, Task>? _claimed;

    internal PopupFocusScope(bool autoFocusContent)
    {
        AutoFocusContent = autoFocusContent;
    }

    public bool AutoFocusContent { get; }

    public bool HostFocusPassRan { get; private set; }

    internal bool HasClaim => _claimed != null;

    /// <summary>First caller wins for one open; later callers get false. The
    /// argument tells the claimant whether it also takes DOM focus or only
    /// prepares its keyboard state.</summary>
    public bool TryClaim(Func<bool, Task> takeFocus)
    {
        if (!AutoFocusContent || HostFocusPassRan || _claimed != null)
            return false;

        _claimed = takeFocus;
        return true;
    }

    internal void MarkHostFocusPass() => HostFocusPassRan = true;

    internal async Task ApplyClaimedFocusAsync(bool takeDomFocus)
    {
        HostFocusPassRan = true;
        if (_claimed != null)
            await _claimed(takeDomFocus);
    }
}
