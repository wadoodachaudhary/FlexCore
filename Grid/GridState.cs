using System.Text.Json;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Serializable snapshot of the user-controlled state of a <c>GridControl&lt;TValue&gt;</c>.
/// The model contains no row instances or delegates. Row-based state is represented by
/// <see cref="GridStateItemKey"/> values produced from the grid's ItemKeySelector.
/// </summary>
public sealed class GridState
{
    /// <summary>Schema version for durable application-owned persistence.</summary>
    public int Version { get; set; } = 1;

    public GridSettings ColumnSettings { get; set; } = new();
    public List<GridSortDescriptor> Sorts { get; set; } = new();
    public List<GridFilterDescriptor> Filters { get; set; } = new();
    public string? SearchText { get; set; }
    public string? ExpressionFilterText { get; set; }
    public bool CaseSensitiveFiltering { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public List<GridStateItemKey> SelectedRowKeys { get; set; } = new();
    public List<GridStateCellKey> SelectedCells { get; set; } = new();
    public GridStateCellKey? ActiveCell { get; set; }
    public List<GridStateItemKey> ExpandedRowKeys { get; set; } = new();
    public List<string> CollapsedGroupPaths { get; set; } = new();

    /// <summary>Serializes this state with System.Text.Json web defaults.</summary>
    public string ToJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(this, options ?? GridStateJson.DefaultOptions);

    /// <summary>Deserializes a state previously produced by <see cref="ToJson"/>.</summary>
    public static GridState FromJson(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<GridState>(json, options ?? GridStateJson.DefaultOptions)
        ?? throw new JsonException("The JSON payload did not contain a grid state.");
}

/// <summary>
/// JSON representation of an application-defined item identity. TypeName prevents values
/// with the same JSON spelling but different CLR types (for example 1 and "1") from colliding.
/// </summary>
public sealed record GridStateItemKey(string TypeName, string Json);

/// <summary>Serializable cell identity: stable row key plus column field.</summary>
public sealed record GridStateCellKey(GridStateItemKey RowKey, string Field);

/// <summary>Identifies the user operation which produced a state notification.</summary>
public enum GridStateChangeKind
{
    Unknown,
    StateApplied,
    Sorting,
    Filtering,
    Search,
    Paging,
    Selection,
    Expansion,
    Grouping,
    Columns
}

/// <summary>Event payload for GridControl's state-change callback and CLR event.</summary>
public sealed class GridStateChangedEventArgs : EventArgs
{
    public required GridState State { get; init; }
    public GridStateChangeKind ChangeKind { get; init; }
}

internal static class GridStateJson
{
    internal static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.Web);
}
