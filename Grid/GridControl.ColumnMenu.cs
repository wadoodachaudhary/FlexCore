using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Adds extra sort, filter, visibility, auto-fit, and left/right locking
    /// commands to the right-click context menu. Does not add header buttons.
    /// </summary>
    [Parameter] public bool ShowColumnMenu { get; set; }

    /// <summary>
    /// Opt-in three-dot shortcut on each column header. Off by default;
    /// the standard grid context menu is opened by right-clicking a header.
    /// </summary>
    [Parameter] public bool ShowColumnMenuButton { get; set; }

    private bool CanColumnMenuSort =>
        CurrentHeaderColumn is { AllowSorting: true }
        && AllowSorting
        && !string.IsNullOrWhiteSpace(CurrentHeaderColumn.Field);

    private bool CanColumnMenuFilter =>
        CurrentHeaderColumn is { AllowFiltering: true }
        && AllowFiltering
        && !string.IsNullOrWhiteSpace(CurrentHeaderColumn.Field);

    private bool CurrentColumnHasFilter =>
        CurrentHeaderColumn is { } column && HasActiveColumnFilter(column.Field);

    private bool CurrentColumnHasSort =>
        CurrentHeaderColumn is { } column && GetColumnState(column.Field).SortDirection.HasValue;

    private bool CurrentColumnIsFrozenLeft =>
        CurrentHeaderColumn is { EffectiveIsFrozen: true, EffectiveFrozenPosition: FrozenColumnPosition.Left };

    private bool CurrentColumnIsFrozenRight =>
        CurrentHeaderColumn is { EffectiveIsFrozen: true, EffectiveFrozenPosition: FrozenColumnPosition.Right };

    private async Task HeaderMenuSortAscending() =>
        await SetHeaderMenuSortAsync(SortDirection.Ascending);

    private async Task HeaderMenuSortDescending() =>
        await SetHeaderMenuSortAsync(SortDirection.Descending);

    private async Task HeaderMenuClearSort() =>
        await SetHeaderMenuSortAsync(null);

    private async Task SetHeaderMenuSortAsync(SortDirection? direction)
    {
        var column = CurrentHeaderColumn;
        CloseHeaderContextMenu();
        if (column == null || !CanSortColumnFromMenu(column))
            return;

        var state = GetColumnState(column.Field);
        if (EventsRef?.Sorting.HasDelegate == true && direction.HasValue)
        {
            var args = new SortEventArgs { Field = column.Field, Direction = direction.Value };
            await EventsRef.Sorting.InvokeAsync(args);
            if (args.Cancel)
                return;
        }

        if (!AllowMultiSorting && direction.HasValue)
        {
            foreach (var pair in _columnStates)
                if (!string.Equals(pair.Key, column.Field, StringComparison.OrdinalIgnoreCase))
                    pair.Value.SortDirection = null;
            _sortPriorityFields.RemoveAll(field =>
                !string.Equals(field, column.Field, StringComparison.OrdinalIgnoreCase));
        }

        state.SortDirection = direction;
        if (direction.HasValue)
            EnsureSortPriority(column.Field);
        else
            RemoveSortPriority(column.Field);

        _pageState.CurrentPage = 1;
        if (EventsRef?.Sorted.HasDelegate == true)
        {
            await EventsRef.Sorted.InvokeAsync(new SortEventArgs
            {
                Field = column.Field,
                Direction = direction ?? SortDirection.Ascending
            });
        }

        if (UsesItemsProvider)
            await ReloadItemsAsync();

        _pendingFirstRowSelection = false;
        await SelectFirstVisibleRowAsync(force: true);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Sorting);
        await InvokeAsync(StateHasChanged);
    }

    private bool CanSortColumnFromMenu(GridColumn column) =>
        AllowSorting && column.AllowSorting && !string.IsNullOrWhiteSpace(column.Field);

    private async Task HeaderMenuOpenFilter()
    {
        var column = CurrentHeaderColumn;
        var x = _headerContextMenuX;
        var y = _headerContextMenuY;
        CloseHeaderContextMenu();
        if (column == null || !AllowFiltering || !column.AllowFiltering)
            return;

        _filterPopupField = column.Field;
        SeedFilterPopupDraft(column.Field);
        _filterPopupX = x;
        _filterPopupY = y;
        await InvokeAsync(StateHasChanged);
        await LoadProviderFilterValuesAsync(column.Field);
    }

    private async Task HeaderMenuClearFilter()
    {
        var column = CurrentHeaderColumn;
        CloseHeaderContextMenu();
        if (column == null || !HasActiveColumnFilter(column.Field))
            return;

        ClearFilter(column.Field);
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private Task HeaderMenuFreezeLeft() =>
        SetHeaderMenuFrozenAsync(FrozenColumnPosition.Left);

    private Task HeaderMenuFreezeRight() =>
        SetHeaderMenuFrozenAsync(FrozenColumnPosition.Right);

    private Task HeaderMenuUnfreeze() => SetHeaderMenuFrozenAsync(null);

    private async Task SetHeaderMenuFrozenAsync(FrozenColumnPosition? position)
    {
        var column = CurrentHeaderColumn;
        CloseHeaderContextMenu();
        if (column == null)
            return;

        column.SetRuntimeFrozenPosition(position);
        UpdateFrozenColumnOffsets();
        _columnVirtualizationRenderLayout = null;
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
        await InvokeAsync(StateHasChanged);
    }
}
