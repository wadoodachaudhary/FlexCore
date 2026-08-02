namespace Fx.ControlKit.Reports;

/// <summary>
/// Host-provided persistence for the <c>ReportWriterControl</c>'s
/// per-user viewer preferences (zoom level, etc.). Decouples the
/// generic viewer from any specific storage — HomeFront wires a
/// <c>tblAppOptions</c>-backed adapter, GhostWriter uses local-storage,
/// FlexCore.Showcase falls back to the in-memory default that ships
/// with this library.
///
/// <para>
/// Implementations should be safe to call concurrently and tolerate
/// missing rows by returning the default (zoom = 100).
/// </para>
/// </summary>
public interface IReportViewerSettings
{
    /// <summary>
    /// Returns the last-saved zoom percentage for the current user, or
    /// 100 when no value has been stored yet. Special values:
    /// <c>0</c> = "Fit Page", <c>-1</c> = "Fit Width".
    /// </summary>
    Task<int> GetZoomLevelAsync();

    /// <summary>
    /// Persists the user's chosen zoom level. Called every time the
    /// user picks a value in the zoom dropdown / clicks +/-.
    /// </summary>
    Task SetZoomLevelAsync(int zoomLevel);
}

/// <summary>
/// Default in-memory implementation registered when no host adapter is
/// supplied. State lives for the duration of the circuit / process —
/// zoom resets to 100 on next app restart. Plenty for FlexCore.Showcase
/// and any consumer that doesn't care about persisting per-user state.
/// </summary>
public sealed class InMemoryReportViewerSettings : IReportViewerSettings
{
    private int _zoom = 100;
    public Task<int> GetZoomLevelAsync() => Task.FromResult(_zoom);
    public Task SetZoomLevelAsync(int zoomLevel) { _zoom = zoomLevel; return Task.CompletedTask; }
}
