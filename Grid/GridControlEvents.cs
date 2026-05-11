using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Event callbacks for GridControl. Equivalent to SyncFusion's GridEvents.
/// </summary>
public class GridControlEvents<TValue> : ComponentBase
{
    // Selection
    [Parameter] public EventCallback<RowSelectEventArgs<TValue>> RowSelecting { get; set; }
    [Parameter] public EventCallback<RowSelectEventArgs<TValue>> RowSelected { get; set; }
    [Parameter] public EventCallback<RowSelectEventArgs<TValue>> RowDeselecting { get; set; }
    [Parameter] public EventCallback<RowSelectEventArgs<TValue>> RowDeselected { get; set; }

    // Sorting
    [Parameter] public EventCallback<SortEventArgs> Sorting { get; set; }
    [Parameter] public EventCallback<SortEventArgs> Sorted { get; set; }

    // Filtering
    [Parameter] public EventCallback<FilterEventArgs> Filtering { get; set; }
    [Parameter] public EventCallback<FilterEventArgs> Filtered { get; set; }

    // Paging
    [Parameter] public EventCallback<PageChangeEventArgs> PageChanging { get; set; }
    [Parameter] public EventCallback<PageChangeEventArgs> PageChanged { get; set; }

    // Editing
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> OnBeginEdit { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowUpdating { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowUpdated { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowDeleting { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowDeleted { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowCreating { get; set; }
    [Parameter] public EventCallback<RowEditEventArgs<TValue>> RowCreated { get; set; }

    // Click
    [Parameter] public EventCallback<CellClickEventArgs<TValue>> OnRecordClick { get; set; }
    [Parameter] public EventCallback<CellClickEventArgs<TValue>> OnRecordDoubleClick { get; set; }

    // Cell-level events (Syncfusion parity)
    [Parameter] public EventCallback<CellSelectEventArgs<TValue>> CellSelected { get; set; }
    [Parameter] public EventCallback<CellSelectingEventArgs<TValue>> CellSelecting { get; set; }
    [Parameter] public EventCallback<CellEditArgs<TValue>> OnCellEdit { get; set; }
    [Parameter] public EventCallback<CellSaveArgs<TValue>> OnCellSave { get; set; }
    [Parameter] public EventCallback<CellSavedArgs<TValue>> CellSaved { get; set; }
    [Parameter] public EventCallback<ActionEventArgs<TValue>> OnActionComplete { get; set; }
    [Parameter] public EventCallback<QueryCellInfoEventArgs<TValue>> QueryCellInfo { get; set; }

    // Grouping
    [Parameter] public EventCallback<GroupEventArgs> Grouping { get; set; }
    [Parameter] public EventCallback<GroupEventArgs> Grouped { get; set; }
    [Parameter] public EventCallback<GroupEventArgs> Ungrouping { get; set; }
    [Parameter] public EventCallback<GroupEventArgs> Ungrouped { get; set; }

    // Column Resize
    [Parameter] public EventCallback<ResizeEventArgs> ColumnResizing { get; set; }
    [Parameter] public EventCallback<ResizeEventArgs> ColumnResized { get; set; }

    // Type-ahead (multi-select numeric input)
    [Parameter] public EventCallback<TypeAheadCommitArgs<TValue>> OnTypeAheadCommit { get; set; }

    // Lifecycle
    [Parameter] public EventCallback DataBound { get; set; }
}
