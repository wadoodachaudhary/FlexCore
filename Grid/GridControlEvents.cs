using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public class GridControlEvents<TValue>
{
    public EventCallback<RowSelectEventArgs<TValue>> RowSelecting { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowSelected { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowDeselecting { get; set; }
    public EventCallback<RowSelectEventArgs<TValue>> RowDeselected { get; set; }

    public EventCallback<int> SelectionChanged { get; set; }

    public EventCallback<GridSelectionChangedArgs> SelectionChangedDetailed { get; set; }

    public EventCallback<SortEventArgs> Sorting { get; set; }
    public EventCallback<SortEventArgs> Sorted { get; set; }

    public EventCallback<FilterEventArgs> Filtering { get; set; }
    public EventCallback<FilterEventArgs> Filtered { get; set; }

    public EventCallback<PageChangeEventArgs> PageChanging { get; set; }
    public EventCallback<PageChangeEventArgs> PageChanged { get; set; }

    public EventCallback<RowEditEventArgs<TValue>> OnBeginEdit { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowUpdating { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowUpdated { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowDeleting { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowDeleted { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowCreating { get; set; }
    public EventCallback<RowEditEventArgs<TValue>> RowCreated { get; set; }

    public EventCallback<CellClickEventArgs<TValue>> OnRecordClick { get; set; }
    public EventCallback<CellClickEventArgs<TValue>> OnRecordDoubleClick { get; set; }

    public EventCallback<CellSelectEventArgs<TValue>> CellSelected { get; set; }
    public EventCallback<CellSelectingEventArgs<TValue>> CellSelecting { get; set; }
    public EventCallback<CellEditArgs<TValue>> OnCellEdit { get; set; }
    public EventCallback<CellSaveArgs<TValue>> OnCellSave { get; set; }
    public EventCallback<CellEditButtonArgs<TValue>> OnEditButtonClick { get; set; }
    public EventCallback<CellSavedArgs<TValue>> CellSaved { get; set; }
    public EventCallback<ActionEventArgs<TValue>> OnActionComplete { get; set; }
    public EventCallback<QueryCellInfoEventArgs<TValue>> QueryCellInfo { get; set; }

    public EventCallback<GroupEventArgs> Grouping { get; set; }
    public EventCallback<GroupEventArgs> Grouped { get; set; }
    public EventCallback<GroupEventArgs> Ungrouping { get; set; }
    public EventCallback<GroupEventArgs> Ungrouped { get; set; }

    public EventCallback<ResizeEventArgs> ColumnResizing { get; set; }
    public EventCallback<ResizeEventArgs> ColumnResized { get; set; }

    public EventCallback<RowResizeEventArgs<TValue>> RowResizing { get; set; }
    public EventCallback<RowResizeEventArgs<TValue>> RowResized { get; set; }

    public EventCallback<RowReorderEventArgs<TValue>> RowReordering { get; set; }
    public EventCallback<RowReorderEventArgs<TValue>> RowReordered { get; set; }

    public EventCallback<string> TypeAheadChanged { get; set; }

    public EventCallback<TypeAheadCommitArgs<TValue>> OnTypeAheadCommit { get; set; }

    public EventCallback DataBound { get; set; }
}
