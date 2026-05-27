namespace Fx.ControlKit.Grid;

/// <summary>
/// Snapshot of every user-modifiable aspect of a <see cref="GridControl{T}"/>'s
/// presentation: column order, visibility, widths, header renames, and active
/// group columns. Persisted via <see cref="IGridSettingsStore"/> when the
/// caller passes a <c>PersistenceKey</c>; reloaded on the next render so the
/// last session's manipulation comes back.
/// </summary>
public sealed class GridSettings
{
    /// <summary>Ordered list of column <c>Field</c>s. Fields not in the list
    /// are appended at the end (matches <see cref="GridColumnsBase.ReorderColumns"/>).</summary>
    public List<string>? ColumnOrder { get; set; }

    /// <summary>Per-field visibility override (true = visible). Overrides the
    /// <c>Visible</c> [Parameter] declared in markup.</summary>
    public Dictionary<string, bool>? Visibility { get; set; }

    /// <summary>Per-field pixel width from the user's last drag-resize. Applied
    /// to <c>GridColumn.RuntimeWidth</c>. Null when the user hasn't resized.</summary>
    public Dictionary<string, double>? Widths { get; set; }

    /// <summary>Per-field header-text override from the "Rename this column"
    /// context-menu item.</summary>
    public Dictionary<string, string>? HeaderOverrides { get; set; }

    /// <summary>Field names of currently-active group columns, in the order
    /// the user dragged them onto the grouping bar.</summary>
    public List<string>? GroupColumns { get; set; }
}

/// <summary>
/// Persists <see cref="GridSettings"/> for a grid identified by a string
/// <c>PersistenceKey</c>. The host app supplies the implementation (typically
/// a thin wrapper over its existing user-preferences store), registered as
/// scoped or singleton in DI. When no implementation is registered the
/// GridControl simply doesn't persist — opt-in by registering and supplying
/// <c>PersistenceKey</c> on the GridControl.
/// </summary>
public interface IGridSettingsStore
{
    /// <summary>Reads the saved settings for <paramref name="key"/>, or
    /// null if nothing has been saved yet.</summary>
    Task<GridSettings?> LoadAsync(string key);

    /// <summary>Writes <paramref name="settings"/> for <paramref name="key"/>.
    /// Implementations should overwrite any prior value at this key.</summary>
    Task SaveAsync(string key, GridSettings settings);
}
