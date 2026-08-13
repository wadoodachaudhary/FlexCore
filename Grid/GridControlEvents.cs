using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Event callbacks for GridControl. Equivalent to SyncFusion's GridEvents.
/// Plain config object assigned in C# and passed to GridControl via its
/// <c>EventsRef</c> parameter — it is never rendered as a component, so its
/// callbacks carry no <c>[Parameter]</c> attribute (which would otherwise trip
/// BL0005 on every host that wires them up).
/// </summary>
public class GridControlEvents<TValue>
{
    // Selection
    public EventCallback<RowSelectEventArgs<TValue>> RowSelecting { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowSelected { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowDeselecting { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowDeselected { get; set; }

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
    public EventCallback<int> SelectionChanged { get; set; }

    /// <summary>
    /// Same timing as <see cref="SelectionChanged"/>, with the current selected
    /// row count plus the interaction source. Consumers can keep mouse-only
    /// adorners (for example cursor-position tooltips) out of keyboard range
    /// selection without losing the normal selected-row count updates.
    /// </summary>
    public EventCallback<GridSelectionChangedArgs> SelectionChangedDetailed { get; set; }

    // Sorting
    public EventCallback<SortEventArgs> Sorting { get; set; }
    public EventCallback<SortEventArgs> Sorted { get; set; }

    // Filtering
    public EventCallback<FilterEventArgs> Filtering { get; set; }
    public EventCallback<FilterEventArgs> Filtered { get; set; }

    // Paging
    public EventCallback<PageChangeEventArgs> PageChanging { get; set; }
    public EventCallback<PageChangeEventArgs> PageChanged { get; set; }

    // Editing
    public EventCallback<RowEditEventArgs<TValue>> OnBeginEdit { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowUpdating { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowUpdated { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowDeleting { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowDeleted { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowCreating { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowCreated { get; set; }

    // Click
    public EventCallback<CellClickEventArgs<TValue>> OnRecordClick { get; set; }
    public EventCallback<CellClickEventArgs<TValue>> OnRecordDoubleClick { get; set; }

    // Cell-level events (Syncfusion parity)
    public EventCallback<CellSelectEventArgs<TValue>> CellSelected { get; set; }
    public EventCallback<CellSelectingEventArgs<TValue>> CellSelecting { get; set; }
    public EventCallback<CellEditArgs<TValue>> OnCellEdit { get; set; }
    public EventCallback<CellSaveArgs<TValue>> OnCellSave { get; set; }
    /// <summary>Raised when the user clicks the trailing "…" picker button on a
    /// cell whose column has <see cref="GridColumn.ShowEditButton"/> = true (VB6
    /// VSFlexGrid ComboButton). The host opens a picklist and writes the value
    /// back to the row. Mirrors VB6 gData_CellButtonClick.</summary>
    public EventCallback<CellEditButtonArgs<TValue>> OnEditButtonClick { get; set; }
    public EventCallback<CellSavedArgs<TValue>> CellSaved { get; set; }
    public EventCallback<ActionEventArgs<TValue>> OnActionComplete { get; set; }
    public EventCallback<QueryCellInfoEventArgs<TValue>> QueryCellInfo { get; set; }

    // Grouping
    public EventCallback<GroupEventArgs> Grouping { get; set; }
    public EventCallback<GroupEventArgs> Grouped { get; set; }
    public EventCallback<GroupEventArgs> Ungrouping { get; set; }
    public EventCallback<GroupEventArgs> Ungrouped { get; set; }

    // Column Resize
    public EventCallback<ResizeEventArgs> ColumnResizing { get; set; }
    public EventCallback<ResizeEventArgs> ColumnResized { get; set; }

    // Row Resize
    public EventCallback<RowResizeEventArgs<TValue>> RowResizing { get; set; }
    public EventCallback<RowResizeEventArgs<TValue>> RowResized { get; set; }

    // Row Reorder
    public EventCallback<RowReorderEventArgs<TValue>> RowReordering { get; set; }
    public EventCallback<RowReorderEventArgs<TValue>> RowReordered { get; set; }

    /// <summary>
    /// Fires every time the grid's type-ahead buffer mutates — a digit /
    /// decimal is appended, Backspace shortens it, or Escape / Enter
    /// clears it. Receives the current buffer text so hosts can show a
    /// live "you are typing N" indicator without polling. Empty string
    /// arriving here means the buffer was just committed or cleared.
    /// </summary>
    public EventCallback<string> TypeAheadChanged { get; set; }

    // Type-ahead (multi-select numeric input)
    public EventCallback<TypeAheadCommitArgs<TValue>> OnTypeAheadCommit { get; set; }

    // Lifecycle
    public EventCallback DataBound { get; set; }
}
