namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    private bool _showSortDialog;
    private bool _sortDialogApplying;
    private string? _sortDialogError;
    private readonly List<SortDialogLevel> _sortDialogLevels = new();
    private SortDialogLevel? _selectedSortDialogLevel;

    private sealed class SortDialogLevel(string field, SortDirection direction)
    {
        public string Field { get; set; } = field;
        public SortDirection Direction { get; set; } = direction;
    }

    private sealed record SortDialogColumn(string Field, string Header, ColumnType Type);
    private sealed record SortDialogOrder(SortDirection Direction, string Label);

    private IReadOnlyList<SortDialogColumn> SortDialogColumns => EffectiveColumns
        .Where(column => column.AllowSorting && !string.IsNullOrWhiteSpace(column.Field))
        .GroupBy(column => column.Field, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(column => new SortDialogColumn(column.Field, HeaderColumnDisplay(column), column.Type))
        .ToArray();

    private bool CanOpenSortDialog => AllowSorting && AllowMultiSorting && SortDialogColumns.Count > 0;
    private int SelectedSortDialogIndex => _selectedSortDialogLevel == null
        ? -1 : _sortDialogLevels.IndexOf(_selectedSortDialogLevel);
    private bool CanAddSortDialogLevel => !_sortDialogApplying
        && SortDialogColumns.Any(column => !_sortDialogLevels.Any(level =>
            string.Equals(level.Field, column.Field, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Opens the Excel-style custom sort editor. Requires AllowSorting and
    /// AllowMultiSorting. Draft changes do not affect rows until Apply.
    /// The same editor is available from the grid's header context menu.
    /// </summary>
    public Task OpenSortDialogAsync() => InvokeAsync(() =>
    {
        if (!CanOpenSortDialog || _sortDialogApplying)
            return;

        var initialColumn = SortDialogColumns.FirstOrDefault(column =>
            string.Equals(column.Field, _headerContextMenuField, StringComparison.OrdinalIgnoreCase))
            ?? SortDialogColumns[0];
        var knownFields = SortDialogColumns.Select(column => column.Field)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sortDialogLevels.Clear();
        _sortDialogLevels.AddRange(GetActiveSortDescriptors()
            .Where(sort => knownFields.Contains(sort.Field))
            .Select(sort => new SortDialogLevel(sort.Field, sort.Direction)));
        if (_sortDialogLevels.Count == 0)
            _sortDialogLevels.Add(new SortDialogLevel(initialColumn.Field, SortDirection.Ascending));

        _selectedSortDialogLevel = _sortDialogLevels[0];
        _sortDialogError = null;
        CloseHeaderContextMenu();
        CloseCellContextMenu();
        CloseFilterPopup();
        _showSortDialog = true;
        StateHasChanged();
    });

    private void CloseSortDialog()
    {
        if (_sortDialogApplying)
            return;
        _showSortDialog = false;
        _sortDialogLevels.Clear();
        _selectedSortDialogLevel = null;
        _sortDialogError = null;
    }

    private IEnumerable<SortDialogColumn> GetSortDialogColumnChoices(SortDialogLevel current) =>
        SortDialogColumns.Where(column => !_sortDialogLevels.Any(level =>
            !ReferenceEquals(level, current)
            && string.Equals(level.Field, column.Field, StringComparison.OrdinalIgnoreCase)));

    private IReadOnlyList<SortDialogOrder> GetSortDialogOrderChoices(SortDialogLevel level) =>
        SortDialogColumns.FirstOrDefault(column =>
            string.Equals(column.Field, level.Field, StringComparison.OrdinalIgnoreCase))?.Type switch
        {
            ColumnType.Number =>
                [new(SortDirection.Ascending, "Smallest to Largest"), new(SortDirection.Descending, "Largest to Smallest")],
            ColumnType.Date =>
                [new(SortDirection.Ascending, "Oldest to Newest"), new(SortDirection.Descending, "Newest to Oldest")],
            ColumnType.Boolean or ColumnType.CheckBox =>
                [new(SortDirection.Ascending, "False to True"), new(SortDirection.Descending, "True to False")],
            _ => [new(SortDirection.Ascending, "A to Z"), new(SortDirection.Descending, "Z to A")]
        };

    private void SelectSortDialogLevel(SortDialogLevel level)
    {
        if (!_sortDialogApplying)
            _selectedSortDialogLevel = level;
    }

    private void SetSortDialogColumn(SortDialogLevel level, string? field)
    {
        if (_sortDialogApplying || string.IsNullOrWhiteSpace(field))
            return;
        level.Field = field;
        _selectedSortDialogLevel = level;
        _sortDialogError = null;
    }

    private void SetSortDialogOrder(SortDialogLevel level, SortDirection direction)
    {
        if (_sortDialogApplying)
            return;
        level.Direction = direction;
        _selectedSortDialogLevel = level;
        _sortDialogError = null;
    }

    private void AddSortDialogLevel()
    {
        if (!CanAddSortDialogLevel)
            return;
        var column = SortDialogColumns.First(column => !_sortDialogLevels.Any(level =>
            string.Equals(level.Field, column.Field, StringComparison.OrdinalIgnoreCase)));
        _selectedSortDialogLevel = new SortDialogLevel(column.Field, SortDirection.Ascending);
        _sortDialogLevels.Add(_selectedSortDialogLevel);
        _sortDialogError = null;
    }

    private void DeleteSortDialogLevel()
    {
        var index = SelectedSortDialogIndex;
        if (_sortDialogApplying || index < 0)
            return;
        _sortDialogLevels.RemoveAt(index);
        _selectedSortDialogLevel = _sortDialogLevels.Count == 0
            ? null : _sortDialogLevels[Math.Min(index, _sortDialogLevels.Count - 1)];
        _sortDialogError = null;
    }

    private void MoveSortDialogLevel(int offset)
    {
        var index = SelectedSortDialogIndex;
        var target = index + offset;
        if (_sortDialogApplying || index < 0 || target < 0 || target >= _sortDialogLevels.Count)
            return;
        (_sortDialogLevels[index], _sortDialogLevels[target]) = (_sortDialogLevels[target], _sortDialogLevels[index]);
    }

    private void ClearSortDialogLevels()
    {
        if (_sortDialogApplying)
            return;
        _sortDialogLevels.Clear();
        _selectedSortDialogLevel = null;
        _sortDialogError = null;
    }

    private bool ValidateSortDialogLevels(IReadOnlyList<GridSortDescriptor> sorts)
    {
        _sortDialogError = null;
        if (!AllowSorting || !AllowMultiSorting)
            _sortDialogError = "Multi-column sorting is no longer enabled for this grid.";
        else if (sorts.Any(sort => !SortDialogColumns.Any(column =>
                     string.Equals(column.Field, sort.Field, StringComparison.OrdinalIgnoreCase))))
            _sortDialogError = "A sort column is no longer available. Select another column.";
        else if (sorts.Select(sort => sort.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorts.Count)
            _sortDialogError = "Choose each column only once.";
        else if (sorts.Any(sort => sort.Direction is not (SortDirection.Ascending or SortDirection.Descending)))
            _sortDialogError = "Choose a valid order for each sort level.";
        return _sortDialogError == null;
    }

    private async Task ApplySortDialogAsync()
    {
        if (!_showSortDialog || _sortDialogApplying)
            return;
        var sorts = _sortDialogLevels.Select(level => new GridSortDescriptor(level.Field, level.Direction)).ToArray();
        if (!ValidateSortDialogLevels(sorts))
            return;

        _sortDialogApplying = true;
        try
        {
            // Validate every cancellable callback before replacing any sort.
            // Applying N levels must never expose N intermediate row orders or
            // send N separate provider requests.
            if (EventsRef?.Sorting.HasDelegate == true)
            {
                foreach (var sort in sorts)
                {
                    var args = new SortEventArgs { Field = sort.Field, Direction = sort.Direction };
                    await EventsRef.Sorting.InvokeAsync(args);
                    if (args.Cancel)
                    {
                        var column = FindColumnByField(sort.Field);
                        var header = column == null ? sort.Field : HeaderColumnDisplay(column);
                        _sortDialogError = $"The sort change for {header} was canceled.";
                        return;
                    }
                }
            }
            if (!ValidateSortDialogLevels(sorts))
                return;

            foreach (var state in _columnStates.Values)
                state.SortDirection = null;
            _sortPriorityFields.Clear();
            foreach (var sort in sorts)
            {
                GetColumnState(sort.Field).SortDirection = sort.Direction;
                _sortPriorityFields.Add(sort.Field);
            }
            _pageState.CurrentPage = 1;
            ClearPassViewMemos();
            InvalidateBlazorServerOptimizationCaches();

            if (UsesItemsProvider)
                await ReloadItemsAsync();
            _pendingFirstRowSelection = false;
            await SelectFirstVisibleRowAsync(force: true);

            _showSortDialog = false;
            if (EventsRef?.Sorted.HasDelegate == true)
                foreach (var sort in sorts)
                    await EventsRef.Sorted.InvokeAsync(new SortEventArgs { Field = sort.Field, Direction = sort.Direction });
            await NotifyGridStateChangedAsync(GridStateChangeKind.Sorting);
        }
        finally
        {
            _sortDialogApplying = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
