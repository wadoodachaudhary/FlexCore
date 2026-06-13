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

    /// <summary>
    /// Fires after any change to the set of selected rows — single-row
    /// click, drag-select (every row crossed during the drag), Select All,
    /// Clear Selection, programmatic toggles. Receives the current
    /// selected-row count so consumers don't have to call back into the
    /// grid. Use this when you need a counter / status label to track
    /// selection in real time; <see cref="RowSelected"/> alone does NOT
    /// fire during drag-select (intentional, to avoid a per-mousemove
    /// flood of per-row callbacks).
    /// </summary>
    [Parameter] public EventCallback<int> SelectionChanged { get; set; }

    /// <summary>
    /// Same timing as <see cref="SelectionChanged"/>, with the current selected
    /// row count plus the interaction source. Consumers can keep mouse-only
    /// adorners (for example cursor-position tooltips) out of keyboard range
    /// selection without losing the normal selected-row count updates.
    /// </summary>
    [Parameter] public EventCallback<GridSelectionChangedArgs> SelectionChangedDetailed { get; set; }

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
    /// <summary>Raised when the user clicks the trailing "…" picker button on a
    /// cell whose column has <see cref="GridColumn.ShowEditButton"/> = true (VB6
    /// VSFlexGrid ComboButton). The host opens a picklist and writes the value
    /// back to the row. Mirrors VB6 gData_CellButtonClick.</summary>
    [Parameter] public EventCallback<CellEditButtonArgs<TValue>> OnEditButtonClick { get; set; }
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

    // Row Resize
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResizing { get; set; }
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResized { get; set; }

    /// <summary>
    /// Fires every time the grid's type-ahead buffer mutates — a digit /
    /// decimal is appended, Backspace shortens it, or Escape / Enter
    /// clears it. Receives the current buffer text so hosts can show a
    /// live "you are typing N" indicator without polling. Empty string
    /// arriving here means the buffer was just committed or cleared.
    /// </summary>
    [Parameter] public EventCallback<string> TypeAheadChanged { get; set; }

    // Type-ahead (multi-select numeric input)
    [Parameter] public EventCallback<TypeAheadCommitArgs<TValue>> OnTypeAheadCommit { get; set; }

    // Lifecycle
    [Parameter] public EventCallback DataBound { get; set; }
}
