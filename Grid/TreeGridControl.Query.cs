using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class TreeGridControl<TValue>
{
    [Parameter] public string AriaLabel { get; set; } = "Tree grid";
    [Parameter] public bool AllowMultiSorting { get; set; } = true;
    [Parameter] public EventCallback<IReadOnlyList<GridSortDescriptor>> SortChanged { get; set; }
    [Parameter] public TreeGridFilterHierarchyMode FilterHierarchyMode { get; set; } = TreeGridFilterHierarchyMode.Both;
    [Parameter] public bool FilterMatchCase { get; set; }
    [Parameter] public bool AllowPaging { get; set; }
    [Parameter] public int PageSize { get; set; } = 50;
    [Parameter] public EventCallback<int> PageChanged { get; set; }
    [Parameter] public bool ShowHeaderContextMenu { get; set; } = true;
    private List<GridSortDescriptor> _sorts = [];
    private int _page = 1;
    private bool _sortDialog, _headerMenu;
    private double _menuX, _menuY;
    private List<SortLevel> _sortDraft = [];
    private string? _sortError;
    private TextFilterOperator _filterOperatorDraft = TextFilterOperator.Contains, _secondOperatorDraft = TextFilterOperator.Contains;
    private string _secondFilterDraft = "";
    private LogicalFilterOperator _logicalDraft;
    private sealed class SortLevel { public string Field { get; set; } = ""; public SortDirection Direction { get; set; } }
    private sealed record Choice<T>(T Value, string Text);
    private static readonly Choice<TextFilterOperator>[] FilterOperators = Enum.GetValues<TextFilterOperator>()
        .Where(v => v != TextFilterOperator.ChooseOne).Select(v => new Choice<TextFilterOperator>(v, v.ToString())).ToArray();
    private IReadOnlyList<Choice<string>> SortColumns => _columns.Where(c => c.AllowSorting && !string.IsNullOrEmpty(c.Field))
        .Select(c => new Choice<string>(c.Field, c.DisplayHeader)).ToArray();
    private static bool HasFilter(ColumnState state) => state.FilterActive ||
        TreeGridQuery.IsActive(state.SecondFilterOperator, state.SecondFilterValue);
    private HashSet<object> _filteredParentIds = [];
    private bool DisplayExpanded(TreeNode<TValue> node) => HasActiveFilters ?
        node.Id is not null && _filteredParentIds.Contains(node.Id) : node.IsExpanded;
    public int CurrentPage => Math.Clamp(_page, 1, PageCount);
    public int PageCount => Math.Max(1, (VisibleNodes.Count() + Math.Max(1, PageSize) - 1) / Math.Max(1, PageSize));
    private int PageOffset => AllowPaging ? (CurrentPage - 1) * PageSize : 0;
    private IEnumerable<TreeNode<TValue>> RenderedNodes => AllowPaging ? VisibleNodes.Skip(PageOffset).Take(PageSize) : VisibleNodes;
    public IReadOnlyList<GridSortDescriptor> GetSorts() => _sorts.ToArray();
    public IReadOnlyList<TreeGridFilter> GetFilters() => _columnStates.Values.Where(HasFilter)
        .Select(s => new TreeGridFilter(s.Field, s.FilterOperator, s.FilterValue, s.SecondFilterOperator, s.SecondFilterValue, s.LogicalFilterOperator)).ToArray();

    public async Task GoToPageAsync(int page)
    {
        if (!await FinishActiveEditorAsync()) return;
        _page = Math.Clamp(page, 1, PageCount);
        await PageChanged.InvokeAsync(_page);
        await InvokeAsync(StateHasChanged);
    }

    public async Task SetSortsAsync(IEnumerable<GridSortDescriptor> sorts)
    {
        if (!await FinishActiveEditorAsync()) return;
        var next = sorts.ToList();
        if (next.GroupBy(s => s.Field, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Choose each sort column once.");
        if (!AllowMultiSorting && next.Count > 1) throw new InvalidOperationException("Multiple sorting is disabled.");
        next = next.Select(sort =>
        {
            if (!Enum.IsDefined(sort.Direction)) throw new ArgumentException("Invalid sort direction.");
            return sort with { Field = ValidateField(sort.Field, sorting: true).Field };
        }).ToList();
        _sorts = next;
        foreach (var state in _columnStates.Values) state.SortDirection = null;
        foreach (var sort in _sorts) GetColumnState(sort.Field).SortDirection = sort.Direction;
        RebuildPreservingExpansion(); _page = 1;
        await SortChanged.InvokeAsync(GetSorts());
        await InvokeAsync(StateHasChanged);
    }

    private TreeGridColumn ValidateField(string field, bool sorting = false)
    {
        var column = _columns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        if (column is null || (sorting ? !column.AllowSorting : !column.AllowFiltering))
            throw new ArgumentException("Unknown or disabled column: " + field);
        return column;
    }

    public async Task SetFilterAsync(TreeGridFilter filter)
    {
        if (!await FinishActiveEditorAsync()) return;
        var column = ValidateField(filter.Field);
        if (!Enum.IsDefined(filter.Operator) || !Enum.IsDefined(filter.SecondOperator) || !Enum.IsDefined(filter.LogicalOperator))
            throw new ArgumentException("Invalid filter operator.");
        var state = GetColumnState(column);
        state.FilterOperator = filter.Operator; state.FilterValue = filter.Value;
        state.SecondFilterOperator = filter.SecondOperator; state.SecondFilterValue = filter.SecondValue;
        state.LogicalFilterOperator = filter.LogicalOperator;
        _page = 1;
        await InvokeAsync(StateHasChanged);
    }

    private async Task FilterKeyDownAsync(KeyboardEventArgs e, TreeGridColumn column)
    {
        if (e.Key == "Enter") await ApplyFilterAsync(column);
        if (e.Key == "Escape") _filterPopupField = null;
    }
    private void ShowHeaderMenu(MouseEventArgs e, TreeGridColumn? column = null)
    {
        if (!ShowHeaderContextMenu) return;
        _menuColumn = column; _menuX = e.ClientX; _menuY = e.ClientY; _headerMenu = true;
    }
    public Task OpenSortDialogAsync()
    {
        if (!AllowSorting) return Task.CompletedTask;
        _headerMenu = false; _sortError = null;
        _sortDraft = _sorts.Select(s => new SortLevel { Field = s.Field, Direction = s.Direction }).ToList();
        if (_sortDraft.Count == 0) AddSortLevel();
        _sortDialog = true; return InvokeAsync(StateHasChanged);
    }
    private void AddSortLevel()
    {
        var field = SortColumns.FirstOrDefault(c => !_sortDraft.Any(s => s.Field == c.Value));
        if (field is not null) _sortDraft.Add(new() { Field = field.Value });
    }
    private void MoveSortLevel(SortLevel level, int delta)
    {
        var index = _sortDraft.IndexOf(level); var next = index + delta;
        if (next < 0 || next >= _sortDraft.Count) return;
        (_sortDraft[index], _sortDraft[next]) = (_sortDraft[next], _sortDraft[index]);
    }
    private async Task ApplySortDialogAsync()
    {
        try { await SetSortsAsync(_sortDraft.Select(s => new GridSortDescriptor(s.Field, s.Direction))); _sortDialog = false; }
        catch (ArgumentException ex) { _sortError = ex.Message; }
    }

    public TreeGridViewState CaptureState() => new()
    {
        Sorts = _sorts.ToList(),
        Filters = GetFilters().ToList(),
        ExpandedIds = _flatNodes.Where(n => n.IsExpanded && n.HasChildren).Select(n => StateId(n.Id)).ToList(),
        SelectedId = _flatNodes.FirstOrDefault(n => EqualityComparer<TValue>.Default.Equals(n.Data, _selectedItem)) is { } selected ? StateId(selected.Id) : null,
        HierarchyMode = FilterHierarchyMode,
        MatchCase = FilterMatchCase,
        Page = CurrentPage,
        PageSize = PageSize,
        ColumnVisibility = new(_visibilityOverrides),
        CheckedIds = _checkedKeys.Select(StateId).ToList(),
        FrozenColumns = FrozenColumns,
        FrozenPositions = new(_frozenOverrides),
        ColumnWidths = new(_columnWidthOverrides)
    };
    private static string StateId(object? id) => Convert.ToString(id, CultureInfo.InvariantCulture) ?? "";
    public async Task RestoreStateAsync(TreeGridViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PageSize < 1) throw new ArgumentException("Page size must be positive.");
        foreach (var filter in state.Filters) ValidateField(filter.Field);
        // Validate sorts before changing any other state.
        await SetSortsAsync(state.Sorts);
        await ClearFiltersAsync();
        foreach (var filter in state.Filters) await SetFilterAsync(filter);
        FilterHierarchyMode = state.HierarchyMode; FilterMatchCase = state.MatchCase;
        PageSize = state.PageSize;
        var expanded = state.ExpandedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var node in _flatNodes) node.IsExpanded = expanded.Contains(StateId(node.Id));
        _selectedItem = _flatNodes.FirstOrDefault(n => StateId(n.Id) == state.SelectedId) is { } selected ? selected.Data : default;
        _visibilityOverrides.Clear(); _columnWidthOverrides.Clear();
        foreach (var pair in state.ColumnVisibility.Where(p => _columns.Any(c => c.Field == p.Key))) _visibilityOverrides[pair.Key] = pair.Value;
        if (!VisibleColumns.Any() && _columns.FirstOrDefault() is { } first) _visibilityOverrides[GetColumnKey(first)] = true;
        foreach (var pair in state.ColumnWidths.Where(p => double.IsFinite(p.Value) && p.Value > 0)) _columnWidthOverrides[pair.Key] = pair.Value;
        _checkedKeys.Clear(); _checkedKeys.UnionWith(_flatNodes.Where(n => state.CheckedIds.Contains(StateId(n.Id))).Select(n => n.Id!));
        RefreshHierarchyChecks(); await PublishChecksAsync();
        FrozenColumns = Math.Max(0, state.FrozenColumns); _frozenOverrides.Clear();
        foreach (var pair in state.FrozenPositions.Where(p => _columns.Any(c => c.Field == p.Key) && (!p.Value.HasValue || Enum.IsDefined(p.Value.Value)))) _frozenOverrides[pair.Key] = pair.Value;
        await GoToPageAsync(state.Page);
    }

    /// <summary>Exports the current visible query across all pages, or all loaded records. Does not fetch remote children.</summary>
    public GridExportResult Export(GridExportFormat format, string fileName = "Tree", bool allLoaded = false)
    {
        var table = new GridExportTable { Title = fileName, SheetName = "Tree" };
        var columns = VisibleColumns;
        table.Columns.Add(new("Level"));
        foreach (var col in columns) table.Columns.Add(new(col.DisplayHeader, col.Format, col.ResolvedTextAlign) { Field = col.Field });
        foreach (var node in allLoaded ? _flatNodes : VisibleNodes)
        {
            var row = new GridExportRow { IsBold = node.HasChildren };
            row.Values.Add(node.Level); row.XlsxValues.Add(node.Level);
            foreach (var col in columns)
            {
                row.Values.Add(GetCellDisplayValue(node.Data, col));
                row.XlsxValues.Add(GetPropertyValue(node.Data, col.Field));
            }
            table.Rows.Add(row);
        }
        return GridExporter.Export(table, format, fileName);
    }
    public Task DownloadAsync(GridExportFormat format, string fileName = "Tree", bool allLoaded = false) =>
        LegacyScrollJs is null ? Task.CompletedTask : GridExporter.DownloadAsync(LegacyScrollJs, Export(format, fileName, allLoaded));
}
