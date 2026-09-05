using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    // Structural selection cells have no data-column index. Keep their cursor
    // distinct from ID (0), without adding them to editable cell selections.
    private const int RowSelectionCellIndex = -1;

    private RenderFragment RenderRowSelectionCheckboxCell(TValue item, int rowIndex) => builder =>
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var active = _activeCell == (resolvedRowIndex, RowSelectionCellIndex);
        builder.OpenElement(0, "td");
        builder.AddAttribute(1, "class", "fx-cell fx-checkbox-cell" + (active ? " fx-cell-active" : ""));
        builder.AddAttribute(2, "style", "width:50px;");
        builder.AddAttribute(3, "role", "gridcell");
        builder.AddAttribute(4, "id", GetGridCellDomId(resolvedRowIndex, RowSelectionCellIndex));
        builder.OpenComponent<CheckBoxControl>(5);
        builder.AddAttribute(6, "Checked", _selectedItems.Contains(item));
        builder.AddAttribute(7, "CheckedChanged", EventCallback.Factory.Create<bool>(this, _ => ToggleRowSelection(item, resolvedRowIndex)));
        builder.AddAttribute(8, "TabIndex", -1);
        builder.AddAttribute(9, "StopClickPropagation", true);
        builder.AddAttribute(10, "StopMouseDownPropagation", true);
        builder.AddAttribute(11, "StopKeyDownPropagation", true);
        builder.AddAttribute(12, "PreventKeyDownDefault", true);
        builder.AddAttribute(13, "OnKeyDown", EventCallback.Factory.Create<KeyboardEventArgs>(this, async e =>
        {
            // A pointer checkbox toggle preserves the data-column target for
            // bulk editing. Keyboard navigation from that checkbox owns a
            // separate cursor and must not reuse the previous Item cell.
            SetActiveCell(resolvedRowIndex, RowSelectionCellIndex);
            ClearKeyboardNavigationSource();
            await HandleKeyDown(e);
            await FocusGridHostAsync();
        }));
        builder.CloseComponent();
        builder.CloseElement();
    };

    private async Task<bool> ActivateRowSelectionCellAsync(TValue item, int rowIndex, bool requestRender = true)
    {
        if (!ShowCheckboxColumn)
            return false;
        var committedEditor = _batchEditItem != null;
        if (committedEditor)
        {
            await CommitBatchEdit();
            if (_batchEditItem != null)
                return false; // Validation retained the editor.
        }
        if (!_selectedItems.Contains(item) && EventsRef?.RowSelecting.HasDelegate == true)
        {
            var args = new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex };
            await EventsRef.RowSelecting.InvokeAsync(args);
            if (args.Cancel)
                return false;
        }
        if (IsPagingActive && _pageState.PageSize > 0)
        {
            var displayIndex = GetPassSortedRows().FindIndex(candidate => EqualityComparer<TValue>.Default.Equals(candidate, item));
            var targetPage = displayIndex < 0 ? _pageState.CurrentPage : displayIndex / _pageState.PageSize + 1;
            if (targetPage != _pageState.CurrentPage)
                await GoToPage(targetPage);
        }
        _selectedItems.Clear();
        _selectedItems.Add(item);
        _selectedCells.Clear();
        _lastSelectedItem = item;
        _lastSelectedRowIndex = rowIndex;
        _lastSelectedCell = (rowIndex, RowSelectionCellIndex);
        SetActiveCell(rowIndex, RowSelectionCellIndex);
        ClearKeyboardNavigationSource();
        ResetRowSelectionTypeAheadTarget();
        _focusedGroupPath = null;
        _pendingActiveCellScrollIntoView = requestRender;
        if (EventsRef?.RowSelected.HasDelegate == true)
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex });
        await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        if (committedEditor)
            await FocusGridHostAsync();
        if (requestRender)
            await InvokeAsync(StateHasChanged);
        return true;
    }

    private async Task<bool> TryHandleRowSelectionCellKeyAsync(KeyboardEventArgs e)
    {
        if (!ShowCheckboxColumn || _activeCell?.CellIndex != RowSelectionCellIndex || _isEditing || _batchEditItem != null)
            return false;
        var rowIndex = _activeCell.Value.RowIndex;
        var item = GetItemAtResolvedRowIndex(rowIndex);
        if (item == null)
            return false;
        if (e.Key is " " or "Spacebar" or "Enter" or "NumpadEnter")
        {
            await ToggleRowSelection(item, rowIndex, GridSelectionChangeSource.Keyboard);
            return true;
        }
        if (TryGetHorizontalKeyboardNavigation(e, out var backwards, out var allowRowWrap))
        {
            await NavigateToAdjacentEditTargetAsync(item, rowIndex, RowSelectionCellIndex, backwards, allowRowWrap);
            return true;
        }
        if (TryGetVerticalKeyboardNavigation(e, out var delta))
        {
            var rows = GetKeyboardNavigationRowItems();
            var index = rows.FindIndex(candidate => EqualityComparer<TValue>.Default.Equals(candidate, item));
            var next = index + delta;
            if (index >= 0 && next >= 0 && next < rows.Count)
                await ActivateRowSelectionCellAsync(rows[next], ResolveRowIndex(rows[next], next));
            return true;
        }
        // Selection checkboxes never open or feed a data editor.
        return !e.CtrlKey && !e.MetaKey && !e.AltKey && (e.Key.Length == 1 || e.Key is "Backspace" or "F2");
    }
}
