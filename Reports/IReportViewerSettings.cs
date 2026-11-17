namespace Fx.ControlKit.Reports;

/// <summary>
/// Host-provided persistence for the <c>ReportWriterControl</c>'s
/// per-user viewer preferences (zoom level, etc.). Decouples the
/// generic viewer from host-specific storage. Hosts may persist tab
/// appearance in their per-user preference store or use the scoped
/// in-memory implementation supplied with this library.
///
/// <para>
/// Implementations should be safe to call concurrently and tolerate
/// missing rows by returning the default (zoom = 100, 100%).
/// </para>
/// </summary>
public interface IReportViewerSettings
{
    /// <summary>
    /// Returns the last-saved zoom percentage for the current user, or
    /// 100 (100%) when no value has been stored yet. Special values:
    /// <c>0</c> = "Fit Page", <c>-1</c> = "Fit Width".
    /// </summary>
    Task<int> GetZoomLevelAsync();

    /// <summary>
    /// Persists the user's chosen zoom level. Called every time the
    /// user picks a value in the zoom dropdown / clicks +/-.
    /// </summary>
    Task SetZoomLevelAsync(int zoomLevel);

    /// <summary>Null preserves the viewer's configured default. Default methods keep existing hosts compatible.</summary>
    Task<TabOrientation?> GetTabOrientationAsync() => Task.FromResult<TabOrientation?>(null);
    Task SetTabOrientationAsync(TabOrientation orientation) => Task.CompletedTask;
    Task<TabTheme?> GetTabThemeAsync() => Task.FromResult<TabTheme?>(null);
    Task SetTabThemeAsync(TabTheme theme) => Task.CompletedTask;
}

/// <summary>
/// Default in-memory implementation registered when no host adapter is
/// supplied. State lives for the duration of the circuit / process —
/// zoom resets to Fit Width on next app restart. Plenty for FlexCore.Showcase
/// and any consumer that doesn't care about persisting per-user state.
/// </summary>
public sealed class InMemoryReportViewerSettings : IReportViewerSettings
{
    private int _zoom = 100;
    private TabOrientation? _tabOrientation;
    private TabTheme? _tabTheme;
    public Task<TabTheme?> GetTabThemeAsync() => Task.FromResult(_tabTheme);
    public Task SetTabThemeAsync(TabTheme theme) { _tabTheme = theme; return Task.CompletedTask; }
    public Task<TabOrientation?> GetTabOrientationAsync() => Task.FromResult(_tabOrientation);
    public Task SetTabOrientationAsync(TabOrientation orientation) { _tabOrientation = orientation; return Task.CompletedTask; }
    public Task<int> GetZoomLevelAsync() => Task.FromResult(_zoom);
    public Task SetZoomLevelAsync(int zoomLevel) { _zoom = zoomLevel; return Task.CompletedTask; }
}
