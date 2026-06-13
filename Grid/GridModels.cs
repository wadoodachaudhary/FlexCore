namespace Fx.ControlKit.Grid;

/// <summary>
/// Internal state for a column's sort/filter status.
/// </summary>
public class ColumnState
{
    public string Field { get; set; } = "";
    public SortDirection? SortDirection { get; set; }
    public string? FilterValue { get; set; }
    public TextFilterOperator FilterOperator { get; set; } = TextFilterOperator.Contains;
    public HashSet<string> CheckedFilterValues { get; set; } = new();
    public bool UseCheckedFilter { get; set; }
    public HashSet<string> CheckedNumericRangeKeys { get; set; } = new();
    public bool UseNumericRangeFilter { get; set; }
    public decimal? NumericFilterMin { get; set; }
    public decimal? NumericFilterMax { get; set; }
    public bool UseNumericBoundsFilter { get; set; }
    public bool FilterActive =>
        !string.IsNullOrEmpty(FilterValue)
        || UseCheckedFilter
        || UseNumericRangeFilter
        || UseNumericBoundsFilter;
}

/// <summary>
/// Describes one page of data for the pager.
/// </summary>
public class PageState
{
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRecords { get; set; }
    public int TotalPages => PageSize > 0
        ? Math.Max(1, (int)Math.Ceiling((double)TotalRecords / PageSize))
        : 1;
}

/// <summary>
/// Event args for row selection events.
/// </summary>
public class RowSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public bool Cancel { get; set; }
}

public class RowExpandedEventArgs<TValue>
{
    public TValue? Data { get; set; }
}

public class RowCollapsedEventArgs<TValue>
{
    public TValue? Data { get; set; }
}

/// <summary>
/// Event args for sort events.
/// </summary>
public class SortEventArgs
{
    public string Field { get; set; } = "";
    public SortDirection Direction { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for filter events.
/// </summary>
public class FilterEventArgs
{
    public string Field { get; set; } = "";
    public string? Value { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for page change events.
/// </summary>
public class PageChangeEventArgs
{
    public int PreviousPage { get; set; }
    public int CurrentPage { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for row editing events.
/// </summary>
public class RowEditEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for cell click events.
/// </summary>
public class CellClickEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public string Column { get; set; } = "";
    public int RowIndex { get; set; }
}

/// <summary>
/// Event args for cell selection events.
/// </summary>
public class CellSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int CellIndex { get; set; }
    public object? CurrentValue { get; set; }
    public bool IsCtrlPressed { get; set; }
    public bool IsShiftPressed { get; set; }
}

/// <summary>
/// Event args for cell selecting events.
/// </summary>
public class CellSelectingEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int CellIndex { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for cell edit events. Raised by the grid just before a cell
/// enters edit mode; set <see cref="Cancel"/> to <c>true</c> to veto the
/// edit (e.g. per-row, per-column gating like VB6 gData_BeforeEdit).
/// </summary>
public class CellEditArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";

    /// <summary>When set to <c>true</c> by a handler, the grid aborts the
    /// edit and the cell stays read-only. Defaults to <c>false</c>.</summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for cell save events.
/// </summary>
public class CellSaveArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public object? Value { get; set; }
}

/// <summary>
/// Event args for the in-cell edit/picklist button.
/// </summary>
public class CellEditButtonArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public GridColumn Column { get; set; } = default!;
}

/// <summary>
/// Event args for cell saved events.
/// </summary>
public class CellSavedArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public object? Value { get; set; }
}

/// <summary>
/// Event args for action completion events.
/// </summary>
public class ActionEventArgs<TValue>
{
    public GridAction RequestType { get; set; } = GridAction.Unknown;
    public TValue? Data { get; set; }
}

/// <summary>
/// Event args for query cell info events.
/// </summary>
public class QueryCellInfoEventArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public GridColumn Column { get; set; } = default!;
}

public enum GridAction
{
    Unknown,
    Grouping,
    Ungrouping,
    Sorting,
    Filtering,
    Paging,
    Refresh
}

/// <summary>
/// Describes a command button in a column (Edit, Delete, Save, Cancel).
/// </summary>
public class GridCommandModel
{
    public string Type { get; set; } = ""; // "Edit", "Delete", "Save", "Cancel"
    public string ButtonOption { get; set; } = "";
}

/// <summary>
/// Represents a grouping level — a field that data is grouped by.
/// </summary>
public class GroupDescriptor
{
    public string Field { get; set; } = "";
    public string HeaderText { get; set; } = "";
}

/// <summary>
/// Represents one group of rows sharing a common value for the grouped field.
/// </summary>
public class GroupResult<TValue>
{
    public string Field { get; set; } = "";
    public string HeaderText { get; set; } = "";
    public object? Key { get; set; }
    public string GroupPath { get; set; } = "";
    public int Count { get; set; }
    public IEnumerable<TValue> Items { get; set; } = Enumerable.Empty<TValue>();
    public IEnumerable<GroupResult<TValue>> SubGroups { get; set; } = Enumerable.Empty<GroupResult<TValue>>();
    public bool IsCollapsed { get; set; }
    /// <summary>Aggregates: field → computed value.</summary>
    public Dictionary<string, object?> Aggregates { get; set; } = new();
}

/// <summary>
/// Event args for grouping events.
/// </summary>
public class GroupEventArgs
{
    public string Field { get; set; } = "";
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for column resize.
/// </summary>
public class ResizeEventArgs
{
    public string Field { get; set; } = "";
    public double OldWidth { get; set; }
    public double NewWidth { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Event args for row resize. Enabled only when GridControl.AllowRowResizing is true.
/// </summary>
public class RowResizeEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public double OldHeight { get; set; }
    public double NewHeight { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>
/// Defines an aggregate column — a field with a computation type (Sum, Avg, etc.)
/// to show in group footers and/or the grid footer.
/// </summary>
public class AggregateColumn
{
    /// <summary>The data field to aggregate.</summary>
    public string Field { get; set; } = "";
    /// <summary>The aggregate operation.</summary>
    public AggregateType Type { get; set; } = AggregateType.Sum;
    /// <summary>.NET format string for the result (e.g. "C2", "N2").</summary>
    public string? Format { get; set; }
    /// <summary>Optional label template. Use {value} as placeholder for the computed value.
    /// Example: "Sum: {value}" or "Total: {value}"</summary>
    public string FooterTemplate { get; set; } = "{value}";
    /// <summary>Optional label template for group footers. Falls back to FooterTemplate.</summary>
    public string? GroupFooterTemplate { get; set; }
    /// <summary>Optional label template for group captions. Falls back to FooterTemplate.</summary>
    public string? GroupCaptionTemplate { get; set; }
}

/// <summary>
/// Represents one aggregate row definition containing multiple aggregate columns.
/// </summary>
public class AggregateRow
{
    public List<AggregateColumn> Columns { get; set; } = new();
    /// <summary>Show in group footer.</summary>
    public bool ShowInGroupFooter { get; set; } = true;
    /// <summary>Show in grid footer (after all data).</summary>
    public bool ShowInFooter { get; set; }
    /// <summary>Show aggregate value in group caption header row (inline text appended to the group label).</summary>
    public bool ShowInGroupCaption { get; set; }
    /// <summary>Render an extra row at the TOP of each group with column-aligned
    /// aggregate values — visually mirrors <see cref="ShowInGroupFooter"/> but
    /// appears immediately under the group label, before the detail rows.
    /// All cells are empty except the columns that have an <see cref="AggregateColumn"/>
    /// configured for this aggregate row.</summary>
    public bool ShowInGroupHeader { get; set; }
}

/// <summary>
/// Event args for type-ahead commit when user types a value while multiple rows are selected.
/// </summary>
public class TypeAheadCommitArgs<TValue>
{
    public List<TValue> SelectedItems { get; set; } = new();
    public string ColumnName { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum GridSelectionChangeSource
{
    Unknown,
    Pointer,
    MouseDrag,
    Keyboard,
    Programmatic
}

public class GridSelectionChangedArgs
{
    public int Count { get; set; }
    public GridSelectionChangeSource Source { get; set; } = GridSelectionChangeSource.Unknown;
}
