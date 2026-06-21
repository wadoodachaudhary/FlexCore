namespace Fx.ControlKit.Grid;

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

public class PageState
{
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRecords { get; set; }
    public int TotalPages => PageSize > 0
        ? Math.Max(1, (int)Math.Ceiling((double)TotalRecords / PageSize))
        : 1;
}

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

public class SortEventArgs
{
    public string Field { get; set; } = "";
    public SortDirection Direction { get; set; }
    public bool Cancel { get; set; }
}

public class FilterEventArgs
{
    public string Field { get; set; } = "";
    public string? Value { get; set; }
    public bool Cancel { get; set; }
}

public class PageChangeEventArgs
{
    public int PreviousPage { get; set; }
    public int CurrentPage { get; set; }
    public bool Cancel { get; set; }
}

public class RowEditEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public bool Cancel { get; set; }
}

public class CellClickEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public string Column { get; set; } = "";
    public int RowIndex { get; set; }
}

public class CellSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int CellIndex { get; set; }
    public object? CurrentValue { get; set; }
    public bool IsCtrlPressed { get; set; }
    public bool IsShiftPressed { get; set; }
}

public class CellSelectingEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int CellIndex { get; set; }
    public bool Cancel { get; set; }
}

public class CellEditArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";

    public bool Cancel { get; set; }
}

public class CellSaveArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public object? Value { get; set; }
}

public class CellEditButtonArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public GridColumn Column { get; set; } = default!;
}

public class CellSavedArgs<TValue>
{
    public TValue Data { get; set; } = default!;
    public string ColumnName { get; set; } = "";
    public object? Value { get; set; }
}

public class ActionEventArgs<TValue>
{
    public GridAction RequestType { get; set; } = GridAction.Unknown;
    public TValue? Data { get; set; }
}

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

public class GridCommandModel
{
    public string Type { get; set; } = ""; // "Edit", "Delete", "Save", "Cancel"
    public string ButtonOption { get; set; } = "";
}

public class GroupDescriptor
{
    public string Field { get; set; } = "";
    public string HeaderText { get; set; } = "";
}

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
    public Dictionary<string, object?> Aggregates { get; set; } = new();
}

public class GroupEventArgs
{
    public string Field { get; set; } = "";
    public bool Cancel { get; set; }
}

public class ResizeEventArgs
{
    public string Field { get; set; } = "";
    public double OldWidth { get; set; }
    public double NewWidth { get; set; }
    public bool Cancel { get; set; }
}

public class RowResizeEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public double OldHeight { get; set; }
    public double NewHeight { get; set; }
    public bool Cancel { get; set; }
}

public class RowReorderEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public TValue? TargetData { get; set; }
    public int OldIndex { get; set; }
    public int NewIndex { get; set; }
    public bool Cancel { get; set; }
}

public class AggregateColumn
{
    public string Field { get; set; } = "";
    public AggregateType Type { get; set; } = AggregateType.Sum;
    public string? Format { get; set; }
    public string FooterTemplate { get; set; } = "{value}";
    public string? GroupFooterTemplate { get; set; }
    public string? GroupCaptionTemplate { get; set; }
}

public class AggregateRow
{
    public List<AggregateColumn> Columns { get; set; } = new();
    public bool ShowInGroupFooter { get; set; } = true;
    public bool ShowInFooter { get; set; }
    public bool ShowInGroupCaption { get; set; }
    public bool ShowInGroupHeader { get; set; }
}

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
