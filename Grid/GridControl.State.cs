using Microsoft.AspNetCore.Components;
using System.Text.Json;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Blazor callback raised after user-controlled grid state changes. The payload is a
    /// detached serializable snapshot rather than a reference to mutable internal state.
    /// </summary>
    [Parameter] public EventCallback<GridStateChangedEventArgs> OnStateChanged { get; set; }

    /// <summary>
    /// CLR counterpart to <see cref="OnStateChanged"/> for imperative consumers.
    /// </summary>
    public event EventHandler<GridStateChangedEventArgs>? StateChanged;

    private bool _isApplyingGridState;
    private readonly Dictionary<string, IReadOnlyList<GridNumericRangeDescriptor>> _restoredProviderNumericRanges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a detached, serializable snapshot of public grid state. Local
    /// row identity state is complete when stable keys are available; provider
    /// cell/detail identity is limited to rows that are currently loaded.
    /// </summary>
    public GridState GetState()
    {
        var query = BuildProviderQueryDescriptor();
        var state = new GridState
        {
            ColumnSettings = CloneGridSettings(BuildCurrentSnapshot()),
            Sorts = query.Sorts.ToList(),
            Filters = query.Filters.ToList(),
            SearchText = SearchText,
            ExpressionFilterText = _expressionFilterText,
            CaseSensitiveFiltering = FilterSettingsRef?.EnableCaseSensitivity == true,
            CurrentPage = _pageState.CurrentPage,
            PageSize = _pageState.PageSize,
            CollapsedGroupPaths = _collapsedGroupPaths
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        foreach (var item in _selectedItems)
        {
            if (TryCreateStateItemKey(item, out var key))
                state.SelectedRowKeys.Add(key);
        }

        foreach (var cell in _selectedCells.OrderBy(cell => cell.RowIndex).ThenBy(cell => cell.CellIndex))
        {
            var item = GetItemAtResolvedRowIndex(cell.RowIndex);
            var columns = VisibleColumns.ToList();
            if (item != null
                && cell.CellIndex >= 0
                && cell.CellIndex < columns.Count
                && TryCreateStateItemKey(item, out var key))
            {
                state.SelectedCells.Add(new GridStateCellKey(key, columns[cell.CellIndex].Field));
            }
        }

        if (_activeCell is { } activeCell)
        {
            var item = GetItemAtResolvedRowIndex(activeCell.RowIndex);
            var columns = VisibleColumns.ToList();
            if (item != null
                && activeCell.CellIndex >= 0
                && activeCell.CellIndex < columns.Count
                && TryCreateStateItemKey(item, out var key))
            {
                state.ActiveCell = new GridStateCellKey(key, columns[activeCell.CellIndex].Field);
            }
        }

        foreach (var item in _expandedRows)
        {
            if (TryCreateStateItemKey(item, out var key))
                state.ExpandedRowKeys.Add(key);
        }

        return state;
    }

    /// <summary>Async convenience counterpart to <see cref="GetState"/>.</summary>
    public Task<GridState> GetStateAsync() => Task.FromResult(GetState());

    /// <summary>
    /// Applies a snapshot produced by <see cref="GetState"/>. Unknown/missing columns are
    /// ignored so a saved state can survive additive schema changes. Provider grids reload
    /// their first range using the newly-applied query before row keys are restored.
    /// </summary>
    public Task SetStateAsync(GridState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return InvokeAsync(() => ApplyGridStateAsync(state));
    }

    private async Task ApplyGridStateAsync(GridState state)
    {
        if (state.Version > 1)
        {
            throw new NotSupportedException(
                $"Grid state schema {state.Version} is newer than the supported schema 1.");
        }

        // Parse before mutating anything so a malformed expression does not leave a
        // partially-applied state behind.
        ExpressionFilterNode? parsedExpression = null;
        var expressionText = string.IsNullOrWhiteSpace(state.ExpressionFilterText)
            ? null
            : state.ExpressionFilterText.Trim();
        if (expressionText != null)
            parsedExpression = new ExpressionFilterParser(expressionText, BuildExpressionFilterColumnAliases()).Parse();

        _isApplyingGridState = true;
        try
        {
            _searchCts?.Cancel();
            _pendingSearchText = state.SearchText;
            SearchText = state.SearchText;
            FilterSettingsRef ??= new FilterSettings();
            FilterSettingsRef.EnableCaseSensitivity = state.CaseSensitiveFiltering;

            _expressionFilterText = expressionText;
            _expressionFilterDraft = expressionText;
            _expressionFilterRoot = parsedExpression;
            _expressionFilterError = null;

            _filterPopupField = null;
            _activeFilterPopupField = null;
            _simpleColumnFilters.Clear();
            foreach (var pending in _filterRowDebounce.Values)
            {
                pending.Cancel();
                pending.Dispose();
            }
            _filterRowDebounce.Clear();
            _filterRowDrafts.Clear();
            _filterRowOperators.Clear();
            _columnAdvancedFilters.Clear();
            _columnCheckboxFilters.Clear();
            _numericFilterMinText.Clear();
            _numericFilterMaxText.Clear();
            _restoredProviderNumericRanges.Clear();
            _columnStates.Clear();
            _sortPriorityFields.Clear();
            var knownFields = EffectiveColumns
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => column.Field)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Re-add sort fields in snapshot order: _sortPriorityFields is the
            // shared priority used by both local ThenBy and provider descriptors.
            foreach (var sort in state.Sorts ?? new List<GridSortDescriptor>())
            {
                if (string.IsNullOrWhiteSpace(sort.Field) || !knownFields.Contains(sort.Field))
                    continue;
                GetColumnState(sort.Field).SortDirection = sort.Direction;
                EnsureSortPriority(sort.Field);
            }

            foreach (var filter in state.Filters ?? new List<GridFilterDescriptor>())
            {
                if (filter != null
                    && !string.IsNullOrWhiteSpace(filter.Field)
                    && knownFields.Contains(filter.Field))
                    ApplyStateFilter(filter);
            }

            var columnSettings = state.ColumnSettings ?? new GridSettings();
            // SetStateAsync is replacement semantics. ApplyLoadedSettings is
            // also used for additive/legacy persistence reads, so clear the
            // mutable presentation overlays here before applying this complete
            // snapshot; otherwise restoring an earlier state with null/empty
            // Visibility or HeaderOverrides would leave later user changes in
            // place.
            _visibilityOverrides.Clear();
            _headerOverrides.Clear();
            _groupDescriptors.Clear();
            foreach (var column in EffectiveColumns)
            {
                column.RuntimeWidth = null;
                column.ClearRuntimeFrozenOverride();
            }
            var appliedColumnSettings = CloneGridSettings(columnSettings);
            ApplyLoadedSettings(appliedColumnSettings);
            if (_gridSettingsLoaded)
            {
                // A later host-driven column rebuild replays this cache. Keep
                // it aligned with the programmatic state instead of allowing
                // an older persistence snapshot to silently undo SetStateAsync.
                _lastAppliedSettings = CloneGridSettings(appliedColumnSettings);
                _lastAppliedColumnSignature = ComputeColumnSignature();
            }

            _pageState.PageSize = Math.Max(0, state.PageSize);
            _pageState.CurrentPage = Math.Max(1, state.CurrentPage);

            _collapsedGroupPaths.Clear();
            foreach (var path in state.CollapsedGroupPaths ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _collapsedGroupPaths.Add(path);
            }
            _allGroupsCollapsed = false;
            _expandAllGroups = false;

            ClearTransientSelectionState(clearRows: true);
            _expandedRows.Clear();

            ClearPassViewMemos();
            InvalidateBlazorServerOptimizationCaches();

            if (UsesItemsProvider)
            {
                CaptureCurrentProviderIdentity(forceNewGeneration: true);
                if (UsesProviderGrouping)
                {
                    // During state restore, absence from CollapsedGroupPaths
                    // means expanded. ProviderGroupsInitiallyCollapsed cannot
                    // be allowed to collapse those groups again while their
                    // lazy hierarchy is being rebuilt.
                    _restoreProviderGroupExpansionFromState = true;
                    try
                    {
                        await LoadAndCommitProviderRootGroupsAsync();
                    }
                    finally
                    {
                        _restoreProviderGroupExpansionFromState = false;
                    }
                }
                else
                {
                    await LoadAndCommitProviderWindowAsync(
                        startIndex: 0,
                        count: Math.Max(1, _winCount),
                        includeTotalCount: true,
                        resetScrollPosition: true);
                }
            }

            RestoreStateItemKeys(state);
            if (UsesItemsProvider)
                EnsureCurrentPageInRange();
            else
                _ = PagedData.ToList(); // refresh TotalRecords before page clamping
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            _isApplyingGridState = false;
        }

        await NotifyGridStateChangedAsync(GridStateChangeKind.StateApplied);
    }

    private void ApplyStateFilter(GridFilterDescriptor filter)
    {
        if (filter == null || string.IsNullOrWhiteSpace(filter.Field))
            return;

        switch (filter.Source)
        {
            case GridProviderFilterSource.FilterRow:
                var filterRowValue = filter.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(filterRowValue)
                    || IsValueOptionalFilterOperator(filter.Operator))
                {
                    _simpleColumnFilters[filter.Field] = filterRowValue;
                }
                _filterRowDrafts[filter.Field] = filterRowValue;
                _filterRowOperators[filter.Field] = filter.Operator;
                return;

            case GridProviderFilterSource.AdvancedColumn:
                var criteria = new ColumnAdvancedFilterCriteria
                {
                    Operator1 = filter.AdvancedOperator ?? MapTextFilterOperator(filter.Operator),
                    Value1 = filter.Value,
                    LogicalOperator = filter.LogicalOperator,
                    Operator2 = filter.SecondAdvancedOperator
                        ?? MapTextFilterOperator(filter.SecondOperator ?? TextFilterOperator.Contains),
                    Value2 = filter.SecondValue
                };
                if (IsAdvancedFilterCriteriaActive(criteria))
                    _columnAdvancedFilters[filter.Field] = criteria;
                return;

            case GridProviderFilterSource.ColumnCheckBox:
                _columnCheckboxFilters[filter.Field] = new HashSet<string>(
                    filter.Values ?? Array.Empty<string>(),
                    FilterTextComparer);
                return;
        }

        var columnState = GetColumnState(filter.Field);
        switch (filter.Kind)
        {
            case GridProviderFilterKind.Text:
                columnState.FilterOperator = filter.Operator;
                columnState.FilterValue = filter.Value;
                columnState.SecondFilterOperator = filter.SecondOperator ?? TextFilterOperator.Contains;
                columnState.SecondFilterValue = filter.SecondValue;
                columnState.LogicalFilterOperator = filter.LogicalOperator;
                columnState.BlankRowFilter = filter.BlankRowFilter;
                break;

            case GridProviderFilterKind.CheckedValues:
                columnState.CheckedFilterValues = new HashSet<string>(
                    filter.Values ?? Array.Empty<string>(),
                    FilterTextComparer);
                columnState.UseCheckedFilter = true;
                break;

            case GridProviderFilterKind.NumericBounds:
                columnState.NumericFilterMin = filter.Minimum;
                columnState.NumericFilterMax = filter.Maximum;
                columnState.UseNumericBoundsFilter = filter.Minimum.HasValue || filter.Maximum.HasValue;
                if (filter.Minimum.HasValue)
                    _numericFilterMinText[filter.Field] = FormatNumericFilterInputValue(filter.Minimum.Value);
                if (filter.Maximum.HasValue)
                    _numericFilterMaxText[filter.Field] = FormatNumericFilterInputValue(filter.Maximum.Value);
                break;

            case GridProviderFilterKind.NumericRanges:
                var serializedRanges = filter.NumericRanges?.ToArray()
                    ?? Array.Empty<GridNumericRangeDescriptor>();
                columnState.CheckedNumericRangeKeys = new HashSet<string>(
                    serializedRanges.Select(range => range.Key)
                        .Concat(filter.Values ?? Array.Empty<string>()),
                    StringComparer.Ordinal);
                columnState.UseNumericRangeFilter = columnState.CheckedNumericRangeKeys.Count > 0;
                if (serializedRanges.Length > 0)
                    _restoredProviderNumericRanges[filter.Field] = serializedRanges;
                break;
        }
    }

    private static GridFilterOperator MapTextFilterOperator(TextFilterOperator filterOperator) =>
        filterOperator switch
        {
            TextFilterOperator.Equals => GridFilterOperator.Equals,
            TextFilterOperator.DoesNotEqual => GridFilterOperator.NotEquals,
            TextFilterOperator.BeginsWith => GridFilterOperator.StartsWith,
            TextFilterOperator.EndsWith => GridFilterOperator.EndsWith,
            TextFilterOperator.DoesNotContain => GridFilterOperator.DoesNotContain,
            TextFilterOperator.GreaterThan => GridFilterOperator.GreaterThan,
            TextFilterOperator.GreaterThanOrEqual => GridFilterOperator.GreaterThanOrEquals,
            TextFilterOperator.LessThan => GridFilterOperator.LessThan,
            TextFilterOperator.LessThanOrEqual => GridFilterOperator.LessThanOrEquals,
            TextFilterOperator.IsEmpty => GridFilterOperator.IsEmpty,
            TextFilterOperator.IsNotEmpty => GridFilterOperator.IsNotEmpty,
            _ => GridFilterOperator.Contains
        };

    private void RestoreStateItemKeys(GridState state)
    {
        var keyedRows = new Dictionary<GridStateItemKey, TValue>();
        foreach (var item in GetRowsAvailableForStateRestore())
        {
            if (TryCreateStateItemKey(item, out var key))
                keyedRows.TryAdd(key, item);
        }

        foreach (var key in state.SelectedRowKeys ?? new List<GridStateItemKey>())
        {
            if (keyedRows.TryGetValue(key, out var item))
                _selectedItems.Add(item);
        }

        var visibleColumns = VisibleColumns.ToList();
        foreach (var cell in state.SelectedCells ?? new List<GridStateCellKey>())
        {
            if (!keyedRows.TryGetValue(cell.RowKey, out var item))
                continue;
            var columnIndex = visibleColumns.FindIndex(column =>
                string.Equals(column.Field, cell.Field, StringComparison.OrdinalIgnoreCase));
            var rowIndex = ResolveRowIndex(item, -1);
            if (rowIndex >= 0 && columnIndex >= 0)
                _selectedCells.Add((rowIndex, columnIndex));
        }

        if (state.ActiveCell is { } activeCell
            && keyedRows.TryGetValue(activeCell.RowKey, out var activeItem))
        {
            var columnIndex = visibleColumns.FindIndex(column =>
                string.Equals(column.Field, activeCell.Field, StringComparison.OrdinalIgnoreCase));
            var rowIndex = ResolveRowIndex(activeItem, -1);
            if (rowIndex >= 0 && columnIndex >= 0)
            {
                _activeCell = (rowIndex, columnIndex);
                _lastSelectedCell = _activeCell;
                _lastSelectedItem = activeItem;
                _lastSelectedRowIndex = rowIndex;
            }
        }

        foreach (var key in state.ExpandedRowKeys ?? new List<GridStateItemKey>())
        {
            if (keyedRows.TryGetValue(key, out var item))
                _expandedRows.Add(item);
        }
    }

    private IEnumerable<TValue> GetRowsAvailableForStateRestore() =>
        UsesProviderGrouping
            ? EnumerateLoadedProviderRows()
            : UsesItemsProvider
                ? _providerWindowItems
                : DataSource ?? Enumerable.Empty<TValue>();

    private bool TryCreateStateItemKey(TValue item, out GridStateItemKey key)
    {
        key = default!;
        object? value;
        try
        {
            value = ItemKeySelector != null
                ? ItemKeySelector(item)
                : CanUseItemAsImplicitStateKey(item) ? item : null;
        }
        catch
        {
            return false;
        }

        if (value == null)
            return false;

        var type = value.GetType();
        try
        {
            key = new GridStateItemKey(
                type.FullName ?? type.Name,
                JsonSerializer.Serialize(value, type, GridStateJson.DefaultOptions));
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CanUseItemAsImplicitStateKey(TValue item)
    {
        if (item == null)
            return false;
        var type = item.GetType();
        return type.IsValueType || item is string;
    }

    private static GridSettings CloneGridSettings(GridSettings settings) => new()
    {
        ColumnOrder = settings.ColumnOrder?.ToList(),
        Visibility = settings.Visibility == null
            ? null
            : new Dictionary<string, bool>(settings.Visibility, StringComparer.OrdinalIgnoreCase),
        Widths = settings.Widths == null
            ? null
            : new Dictionary<string, double>(settings.Widths, StringComparer.OrdinalIgnoreCase),
        FrozenPositions = settings.FrozenPositions == null
            ? null
            : new Dictionary<string, FrozenColumnPosition?>(settings.FrozenPositions, StringComparer.OrdinalIgnoreCase),
        HeaderOverrides = settings.HeaderOverrides == null
            ? null
            : new Dictionary<string, string>(settings.HeaderOverrides, StringComparer.OrdinalIgnoreCase),
        GroupColumns = settings.GroupColumns?.ToList()
    };

    private async Task NotifyGridStateChangedAsync(GridStateChangeKind changeKind)
    {
        if (_isApplyingGridState || (!OnStateChanged.HasDelegate && StateChanged == null))
            return;

        var args = new GridStateChangedEventArgs
        {
            State = GetState(),
            ChangeKind = changeKind
        };

        StateChanged?.Invoke(this, args);
        if (OnStateChanged.HasDelegate)
            await OnStateChanged.InvokeAsync(args);
    }
}
