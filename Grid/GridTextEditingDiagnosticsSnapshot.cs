namespace Fx.ControlKit.Grid;

/// <summary>
/// Instance-scoped counters used by performance harnesses to compare the
/// server-backed and browser-buffered text editor paths.
/// </summary>
public readonly record struct GridTextEditingDiagnosticsSnapshot(
    long ServerInputCallbacks,
    long ServerValueChangedCallbacks,
    long ServerKeyDownCallbacks,
    long ServerBlurCallbacks,
    long ClientBufferedCommitCallbacks)
{
    public long TotalServerCallbacks =>
        ServerInputCallbacks
        + ServerValueChangedCallbacks
        + ServerKeyDownCallbacks
        + ServerBlurCallbacks;
}
