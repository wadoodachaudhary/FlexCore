using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue> : IGridOwner, IAsyncDisposable
{
    void IGridOwner.RegisterColumnsContainer(GridColumnsBase container)
    {
        var changed = !ReferenceEquals(_columnsContainer, container);
        _columnsContainer = container;
        _autoWidthPending = true;
        if (changed)
            _ = InvokeAsync(StateHasChanged);
    }

    void IGridOwner.NotifyColumnsChanged()
    {
        _autoWidthPending = true;
        _ = InvokeAsync(StateHasChanged);
    }

    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public GridWidthMode WidthMode { get; set; } = GridWidthMode.FillAvailable;
    [Parameter] public bool EnableViewportSafeSizing { get; set; } = true;
    [Parameter] public string ViewportSafeMaxWidth { get; set; } = "";
    [Parameter] public string ViewportSafeMaxHeight { get; set; } = "";
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public int RowHeight { get; set; }
    [Parameter] public bool AllowRowResizing { get; set; }
    [Parameter] public bool AllowRowReorder { get; set; }
    [Parameter] public Func<TValue, bool>? RowReorderPredicate { get; set; }
    [Parameter] public bool ShowRowSelectorHandle { get; set; }
    [Parameter] public GridRowSelectorHandleShape RowSelectorHandleShape { get; set; } = GridRowSelectorHandleShape.HalfButton;
    [Parameter] public int RowSelectorHandleWidth { get; set; } = 18;
    [Parameter] public Func<TValue, bool>? RowSelectorHandlePredicate { get; set; }
    [Parameter] public Func<TValue, bool>? RowSelectorHandleEmphasisPredicate { get; set; }
    [Parameter] public double MinRowHeight { get; set; } = 16;
    [Parameter] public Func<TValue, int, double?>? RowHeightSelector { get; set; }
    [Parameter] public Func<TValue, int, string?>? RowCssClassSelector { get; set; }

    [Parameter] public bool AllowSorting { get; set; }
    [Parameter] public bool AllowMultiSorting { get; set; }
    [Parameter] public bool AllowFiltering { get; set; }
    [Parameter] public int PageSize { get; set; } = 50;
    [Parameter] public int[] PageSizes { get; set; } = [25, 50, 100, 200];
    [Parameter] public int PageButtonCount { get; set; } = 5;
    [Parameter] public int AutoPageRowThreshold { get; set; } = 10000;
    [Parameter] public bool ClearFiltersOnDataSourceChange { get; set; } = true;
    [Parameter] public bool ShowHeaderFilterIcon { get; set; } = true;
    [Parameter] public string HeaderFilterIcon { get; set; } = string.Empty;
    [Parameter] public bool AllowPaging { get; set; }
    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public bool HighlightSelectedRows { get; set; } = true;
    [Parameter] public bool AllowRowDragSelection { get; set; } = true;
    [Parameter] public string SelectedRowBackground { get; set; } = string.Empty;
    [Parameter] public string SelectedRowForeground { get; set; } = string.Empty;
    [Parameter] public string SelectedRowHoverBackground { get; set; } = string.Empty;
    [Parameter] public string RowHoverBackground { get; set; } = string.Empty;
    [Parameter] public bool CommitSelectedRowOnEnter { get; set; }
    [Parameter] public bool ShowSelectionInfoBar { get; set; }
    [Parameter] public string? TypeAheadTargetField { get; set; }
    [Parameter] public string? TypeAheadFallbackField { get; set; }
    [Parameter] public GridBatchEditBehavior BatchEditBehavior { get; set; } = GridBatchEditBehavior.MultiRow;
    [Parameter] public bool EditOnSingleClick { get; set; }
    [Parameter] public bool AllowSingleCellColumnMassEdit { get; set; }
    [Parameter] public bool AllowGrouping { get; set; }
    [Parameter] public bool AllowResizing { get; set; }
    [Parameter] public bool AllowCellFormulas { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;
    [Parameter] public bool EnableAltRow { get; set; } = true;
    [Parameter] public bool ShowSearchBar { get; set; }
    [Parameter] public bool EnableTypeSearch { get; set; }
    [Parameter] public int TypeSearchDelaySeconds { get; set; } = 3;
    [Parameter] public GridLines GridLines { get; set; } = GridLines.Both;
    [Parameter] public List<string>? Toolbar { get; set; }

    [Parameter] public bool? ShowGridToolbar { get; set; }

    [Parameter] public bool ShowExpandAllButton { get; set; } = true;

    [Parameter] public bool ShowCollapseAllButton { get; set; } = true;

    [Parameter] public bool ShowAdvancedViewToggleButton { get; set; }

    [Parameter] public bool DefaultAdvancedView { get; set; }

    internal bool ShouldRenderToolbar =>
        ShowGridToolbar ?? ((Toolbar is { Count: > 0 }) || ShowSearchBar);

    [Parameter] public List<string>? GroupColumns { get; set; }

    [Parameter] public List<AggregateRow>? AggregateRows { get; set; }

    [Parameter] public bool ShowGroupExpandCollapse { get; set; } = true;

    [Parameter] public bool ShowExpressionFilterButton { get; set; }

    [Parameter] public bool ShowGridOptionsRail { get; set; }

    [Parameter] public bool ShowColumnOptionsButton { get; set; }

    [Parameter] public bool ShowFilterPanelButton { get; set; }

    [Parameter] public bool AllowPivoting { get; set; }

    [Parameter] public bool ShowPivotPanelButton { get; set; }

    [Parameter] public bool ShowGridThemeToggle { get; set; }
    [Parameter] public GridTheme Theme { get; set; } = GridTheme.Default;

    [Parameter] public bool ShowGridBackButton { get; set; }

    [Parameter] public bool HideGroupedColumns { get; set; } = true;

    [Parameter] public bool AllowColumnReorder { get; set; } = true;

    [Parameter] public string ColumnReorderPipeColor { get; set; } = "#2b2b2b";

    [Parameter] public IEnumerable<ChooseColumnDescriptor>? AvailableColumns { get; set; }

    [Parameter] public IEnumerable<ChooseColumnDescriptor>? DefaultColumns { get; set; }

    [Parameter] public EventCallback<ChooseColumnsResult> OnColumnsChosen { get; set; }

    [Parameter] public EventCallback<GridSettings> OnLayoutChanged { get; set; }

    [Parameter] public string? PersistenceKey { get; set; }

    [Inject] private IServiceProvider Services { get; set; } = default!;
    private IGridSettingsStore? GridSettingsStore =>
        Services?.GetService(typeof(IGridSettingsStore)) as IGridSettingsStore;

    private bool _gridSettingsLoaded;
    private string? _gridSettingsLoadedKey;
    private GridSettings? _lastAppliedSettings;
    private string? _lastAppliedColumnSignature;

    [Parameter] public bool ShowGroupCount { get; set; }

    [Parameter] public bool ShowAsChart { get; set; }

    [Parameter] public IList<string>? ChartValueFields { get; set; }

    [Parameter] public string? ChartLabelField { get; set; }

    [Parameter] public IList<string>? ChartValueLabels { get; set; }

    [Parameter] public string ChartBarColor { get; set; } = "#2563eb";

    [Parameter] public bool ChartShowValues { get; set; } = false;

    [Parameter] public bool DefaultGroupsCollapsed { get; set; } = false;

    [Parameter] public string GroupedColumnColor { get; set; } = "";

    [Parameter] public string GroupItemTextColor { get; set; } = "";

    [Parameter] public string GroupTotalTextColor { get; set; } = "";

    [Parameter] public GroupExpandIconStyle GroupExpandIconStyle { get; set; } = GroupExpandIconStyle.PlusMinus;

    [Parameter] public string? CollapsedGlyph { get; set; }

    [Parameter] public string? ExpandedGlyph { get; set; }

    [Parameter] public string? ExpandIconStyle { get; set; }

    [Parameter] public RenderFragment<bool>? ExpandIconTemplate { get; set; }

    internal string ResolveCollapsedGlyph() =>
        CollapsedGlyph ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "+" : "▶");

    internal string ResolveExpandedGlyph() =>
        ExpandedGlyph ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "−" : "▼");

    internal string? ResolveExpandIconStyle() =>
        ExpandIconStyle ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? FxGridIconStyles.PlusMinus
            : null);   // Triangle → no inline style; CSS class drives it

    private string ResolvedGroupItemTextColor =>
        !string.IsNullOrWhiteSpace(GroupItemTextColor)
            ? GroupItemTextColor.Trim()
            : (string.IsNullOrWhiteSpace(GroupedColumnColor) ? "" : GroupedColumnColor.Trim());

    private string ResolvedGroupTotalTextColor =>
        string.IsNullOrWhiteSpace(GroupTotalTextColor) ? "" : GroupTotalTextColor.Trim();

    private string GroupedColumnColorSoft => $"color-mix(in srgb, {ResolvedGroupItemTextColor} 12%, white)";
    private string ResolvedColumnReorderPipeColor =>
        string.IsNullOrWhiteSpace(ColumnReorderPipeColor) ? "#2b2b2b" : ColumnReorderPipeColor.Trim();

    private string? GroupItemTextStyle =>
        string.IsNullOrEmpty(ResolvedGroupItemTextColor) ? null : $"color:{ResolvedGroupItemTextColor};";

    private string? GroupTotalTextStyle =>
        string.IsNullOrEmpty(ResolvedGroupTotalTextColor) ? null : $"color:{ResolvedGroupTotalTextColor};";

    private bool SingleCellColumnMassEditEnabled =>
        BatchEditBehavior == GridBatchEditBehavior.SingleCell && AllowSingleCellColumnMassEdit;

    private string GridContentStyle
    {
        get
        {
            if (string.IsNullOrEmpty(Height))
                return string.Empty;

            return "overflow-x:auto; overflow-y:hidden; flex:1;";
        }
    }

    private string GetRowCssClass(TValue item, int rowIndex)
    {
        if (RowCssClassSelector == null)
            return string.Empty;

        try
        {
            return RowCssClassSelector.Invoke(item, rowIndex) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string HostStyle
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("--fx-grid-reorder-pipe-color:").Append(ResolvedColumnReorderPipeColor).Append("; ");
            if (!string.IsNullOrWhiteSpace(SelectedRowBackground))
                sb.Append("--fx-grid-selected-row-bg:").Append(SelectedRowBackground).Append("; ");
            if (!string.IsNullOrWhiteSpace(SelectedRowForeground))
                sb.Append("--fx-grid-selected-row-color:").Append(SelectedRowForeground).Append("; ");
            if (!string.IsNullOrWhiteSpace(SelectedRowHoverBackground))
                sb.Append("--fx-grid-selected-row-hover-bg:").Append(SelectedRowHoverBackground).Append("; ");
            if (!string.IsNullOrWhiteSpace(RowHoverBackground))
                sb.Append("--fx-grid-row-hover-bg:").Append(RowHoverBackground).Append("; ");
            if (!string.IsNullOrEmpty(ResolvedGroupItemTextColor))
            {
                sb.Append("--fx-grid-group-color:").Append(ResolvedGroupItemTextColor).Append("; ");
                sb.Append("--fx-grid-group-color-soft:").Append(GroupedColumnColorSoft).Append("; ");
                sb.Append("--fx-grid-group-item-text-color:").Append(ResolvedGroupItemTextColor).Append("; ");
            }
            if (!string.IsNullOrEmpty(ResolvedGroupTotalTextColor))
            {
                sb.Append("--fx-grid-group-total-text-color:").Append(ResolvedGroupTotalTextColor).Append("; ");
            }
            sb.Append("box-sizing:border-box; min-width:0; min-height:0; max-width:100%; max-height:100%; ");
            var height = ResolveViewportSafeSize(Height, ViewportSafeMaxHeight);
            var width = ResolveViewportSafeSize(Width, ViewportSafeMaxWidth);
            if (!string.IsNullOrEmpty(height))
            {
                sb.Append("height:").Append(height).Append("; ");
                if (IsPercentageOnlySize(height))
                    sb.Append("flex:1 1 0; ");
            }
            if (!string.IsNullOrEmpty(width))  sb.Append("width:").Append(width).Append("; ");
            return sb.ToString();
        }
    }

    private string? ResolveViewportSafeSize(string? size, string maxSize)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var trimmed = size.Trim();
        if (!EnableViewportSafeSizing || string.IsNullOrWhiteSpace(maxSize))
            return trimmed;

        if (IsPercentageOnlySize(trimmed))
            return trimmed;

        if (TryParsePixelSize(trimmed, out var sizePx) &&
            TryParsePixelSize(maxSize, out var maxPx) &&
            sizePx <= maxPx)
        {
            return trimmed;
        }

        if (TryParsePixelSize(trimmed, out _) || IsViewportOrCalculatedSize(trimmed))
            return $"min({trimmed}, {maxSize})";

        return trimmed;
    }

    private static bool IsPercentageOnlySize(string value)
    {
        return value.EndsWith("%", StringComparison.Ordinal) &&
               !value.Contains("calc", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("vh", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("vw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsViewportOrCalculatedSize(string value)
    {
        return value.Contains("calc", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("vh", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("vw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePixelSize(string value, out double pixels)
    {
        pixels = 0;
        if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            return false;

        return double.TryParse(
            value[..^2].Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out pixels);
    }

    private static string CombineStyles(params string?[] styles)
    {
        var normalized = styles
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().TrimEnd(';'))
            .Where(s => s.Length > 0);

        return string.Join(";", normalized);
    }

    private string GetRowStyle(TValue item, int rowIndex, bool isSelected)
    {
        var sb = new StringBuilder();
        if (isSelected && HighlightSelectedRows)
            sb.Append("background:var(--fx-grid-selected-row-bg,#b6c8dd);");

        var height = GetEffectiveRowHeight(item, rowIndex);
        if (height.HasValue)
            sb.Append("height:")
              .Append(height.Value.ToString("0.##", CultureInfo.InvariantCulture))
              .Append("px;");

        return sb.ToString();
    }

    private double? GetEffectiveRowHeight(TValue item, int rowIndex)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        if (_runtimeRowHeights.TryGetValue(resolvedRowIndex, out var runtimeHeight))
            return Math.Max(MinRowHeight, runtimeHeight);

        var selectedHeight = RowHeightSelector?.Invoke(item, resolvedRowIndex);
        if (selectedHeight.HasValue && selectedHeight.Value > 0)
            return Math.Max(MinRowHeight, selectedHeight.Value);

        return RowHeight > 0 ? Math.Max(MinRowHeight, RowHeight) : null;
    }

    private bool _appliedDefaultCollapsed;

    [Parameter] public FilterSettings? FilterSettingsRef { get; set; }
    [Parameter] public PageSettings? PageSettingsRef { get; set; }
    [Parameter] public EditSettings? EditSettingsRef { get; set; }
    [Parameter] public SelectionSettings? SelectionSettingsRef { get; set; }
    [Parameter] public GridControlEvents<TValue>? EventsRef { get; set; }

    [Parameter] public EventCallback<string> OnToolbarItemClick { get; set; }
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResizing { get; set; }
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResized { get; set; }
    [Parameter] public EventCallback<RowReorderEventArgs<TValue>> RowReordering { get; set; }
    [Parameter] public EventCallback<RowReorderEventArgs<TValue>> RowReordered { get; set; }
    [Parameter] public Func<TValue>? NewItemFactory { get; set; }
    [Parameter] public bool EnsureTrailingNewRow { get; set; }
    [Parameter] public Func<TValue, bool>? IsTrailingNewRow { get; set; }
    [Parameter] public bool AddNewRowOnLastCellExit { get; set; }
    [Parameter] public Func<TValue, bool>? CanAddNewRowOnLastCellExit { get; set; }
    [Parameter] public Func<TValue, TValue>? CloneFactory { get; set; }

    internal GridColumnsBase? _columnsContainer;
    private readonly Dictionary<string, ColumnState> _columnStates = new();
    private readonly PageState _pageState = new();
    private readonly HashSet<TValue> _selectedItems = new();
    private readonly List<(int RowIndex, int CellIndex)> _selectedCells = new();
    private (int RowIndex, int CellIndex)? _activeCell;
    private bool _expandAllGroups;
    private bool _allGroupsCollapsed;
    private TValue? _trailingNewRowItem;
    private bool _hasTrailingNewRowItem;
    private bool _ensuringTrailingNewRow;
    private readonly HashSet<string> _collapsedGroupPaths = new(StringComparer.OrdinalIgnoreCase);
    private (int RowIndex, int CellIndex)? _lastSelectedCell;
    private int? _lastSelectedRowIndex;

    private int? _dragAnchorRowIndex;
    private TValue? _dragAnchorItem;
    private TValue? _lastSelectedItem;
    private bool _isDragSelecting;
    private (int RowIndex, int CellIndex)? _cellDragAnchor;
    private bool _isCellDragSelecting;

    private bool _isEditing;
    private int _editingRowIndex = -1;
    private TValue? _editItem;

    private TValue? _batchEditItem;
    private int _batchEditRowIndex = -1;
    private string? _batchEditField;
    private string? _batchEditValue;
    private bool _batchEditDirty;
    private bool _batchEditReplaceOnFirstInput;

    private TValue? _lastCommittedBatchEditItem;
    private int _lastCommittedBatchEditRowIndex = -1;
    private string? _lastCommittedBatchEditField;
    private TValue? _lastKeyboardNavigationItem;
    private int _lastKeyboardNavigationRowIndex = -1;
    private int _lastKeyboardNavigationCellIndex = -1;
    private bool _hasLastKeyboardNavigationSource;
    private TValue? _keyboardRangeAnchorItem;
    private int _keyboardRangeAnchorCellIndex = -1;
    private string _typeSearchBuffer = "";
    private DateTime _typeSearchLastInputUtc = DateTime.MinValue;
    private TValue? _typeSearchMatchItem;
    private string? _typeSearchMatchField;
    private bool _hasTypeSearchMatch;

    private bool _pendingBatchEditFocus;

    private bool _pendingBatchEditSelectAll;
    private double? _pendingBatchEditClientX;
    private bool _batchDropdownOpenOnRender;

    private bool _pendingBatchEditScrollIntoView;
    private bool _pendingActiveCellScrollIntoView;

    private ElementReference _batchEditInputRef;
    private int _pendingBatchEditFocusRetries;

    private IJSObjectReference? _gridJsModule;
    private ElementReference _gridHostElement;
    private DotNetObjectReference<GridControl<TValue>>? _gridDotNetRef;
    private bool _headerDragPreviewRegistered;
    private bool _rowDragSelectionAutoScrollRegistered;
    private bool _gridKeyboardTrapRegistered;
    private int? _lastHostResolvedPageSize;

    private enum GridScrollNavigationKey
    {
        PageUp,
        PageDown,
        Home,
        End
    }

    private string? _filterPopupField;
    private double _filterPopupX;
    private double _filterPopupY;
    private string _filterTextDraft = "";
    private TextFilterOperator _filterOperatorDraft = TextFilterOperator.Contains;
    private bool _filterPopupAutoApply = true;
    private HashSet<string> _filterCheckedDraft = new(StringComparer.Ordinal);
    private IEnumerable<TValue>? _lastFilterDataSource;
    private bool _filterDataSourceCaptured;
    private IEnumerable<TValue>? _lastSelectionDataSource;
    private DataSourceSelectionSignature _lastSelectionDataSourceSignature;
    private bool _selectionDataSourceCaptured;
    private string FilterPopupStyle =>
        string.Create(CultureInfo.InvariantCulture,
            $"left:clamp(8px,{_filterPopupX - 280}px,calc(100vw - 360px));top:clamp(8px,{_filterPopupY + 10}px,calc(100vh - 440px));");

    private string _typeAheadBuffer = "";
    private bool _rowSelectionTypeAheadTargetCaptured;
    private string? _rowSelectionTypeAheadTargetField;

    private readonly record struct DataSourceSelectionSignature(int Count, int Fingerprint);

    private string? SearchText;
    private CancellationTokenSource? _searchCts;

    private readonly List<GroupDescriptor> _groupDescriptors = new();
    private string? _draggingColumnField;
    private string? _draggingGroupChipField;
    private bool _dragOverGroupArea;
    private string? _dragOverHeaderField;
    private bool _dragInsertAfterTarget;
    private string? _lastHeaderDragSourceField;
    private DateTime _lastHeaderDragStartedUtc;
    private int _headerDragGeneration;

    private GridColumn? _resizingCol;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private TValue? _resizingRowItem;
    private int _resizingRowIndex = -1;
    private double _rowResizeStartY;
    private double _rowResizeStartHeight;
    private readonly Dictionary<int, double> _runtimeRowHeights = new();
    private bool _autoWidthPending = true;
    private TValue? _rowReorderDragItem;
    private int _rowReorderDragSourceIndex = -1;
    private int _rowReorderDragTargetIndex = -1;
    private bool _isRowReorderDragging;

    private bool ShowCheckboxColumn =>
        SelectionSettingsRef?.CheckboxOnly == true ||
        VisibleColumns.Any(c => c.Type == ColumnType.CheckBox && string.IsNullOrEmpty(c.Field));

    private bool ShowRowReorderColumn =>
        AllowRowReorder && !(AllowGrouping && _groupDescriptors.Count > 0);

    private bool ShowRowSelectorHandleColumn =>
        ShowRowSelectorHandle && !(AllowGrouping && _groupDescriptors.Count > 0);

    private const int RowReorderColumnWidth = 22;

    private string RowReorderColumnStyle =>
        $"width:{RowReorderColumnWidth}px;min-width:{RowReorderColumnWidth}px;max-width:{RowReorderColumnWidth}px;";

    private int ResolvedRowSelectorHandleWidth =>
        Math.Clamp(RowSelectorHandleWidth, 12, 40);

    private string RowSelectorHandleColumnStyle =>
        $"width:{ResolvedRowSelectorHandleWidth}px;min-width:{ResolvedRowSelectorHandleWidth}px;max-width:{ResolvedRowSelectorHandleWidth}px;";

    private FilterType ResolvedFilterType =>
        FilterSettingsRef?.Type ?? FilterType.FilterBar;

    private bool UseDefaultHeaderFilterIcon =>
        string.IsNullOrWhiteSpace(HeaderFilterIcon);

    private string ResolvedHeaderFilterIcon => HeaderFilterIcon;

    private int ResolvedPageSize =>
        PageSettingsRef != null
            ? Math.Max(0, PageSettingsRef.PageSize)
            : Math.Max(0, PageSize);

    private int[] ResolvedPageSizes =>
        PageSettingsRef?.PageSizes is { Length: > 0 }
            ? PageSettingsRef.PageSizes
            : (PageSizes is { Length: > 0 } ? PageSizes : [25, 50, 100, 200]);

    private int ResolvedPageButtonCount =>
        PageSettingsRef?.PageCount is > 0
            ? PageSettingsRef.PageCount
            : (PageButtonCount > 0 ? PageButtonCount : 5);

    private bool IsPagingActive =>
        _pageState.PageSize > 0
        && _pageState.TotalRecords > _pageState.PageSize
        && (AllowPaging || (AutoPageRowThreshold > 0 && _pageState.TotalRecords > AutoPageRowThreshold));

    private IEnumerable<GridColumn> VisibleColumns
    {
        get
        {
            var cols = _columnsContainer?.Columns.Where(IsColumnVisible) ?? Enumerable.Empty<GridColumn>();
            if (HideGroupedColumns && _groupDescriptors.Count > 0)
            {
                var groupedFields = _groupDescriptors.Select(g => g.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
                cols = cols.Where(c => !groupedFields.Contains(c.Field));
            }
            return cols;
        }
    }

    public IReadOnlyList<GridColumn> Columns =>
        _columnsContainer?.Columns ?? Array.Empty<GridColumn>();

    private bool AllRowsSelected =>
        PagedData.Any() && PagedData.All(item => _selectedItems.Contains(item));

    private EventCallback<bool> SelectAllCheckedChanged =>
        EventCallback.Factory.Create<bool>(this, (bool value) => ToggleSelectAll(value));

    private int GroupedPlaceholderCount =>
        (AllowGrouping && HideGroupedColumns) ? _groupDescriptors.Count : 0;

    private int TotalColumnCount =>
        VisibleColumns.Count()
        + (ShowCheckboxColumn ? 1 : 0)
        + (ShowRowReorderColumn ? 1 : 0)
        + (ShowRowSelectorHandleColumn ? 1 : 0)
        + GroupedPlaceholderCount;

    private IReadOnlyList<GridColumn> GroupedLayoutColumns
    {
        get
        {
            if (!(AllowGrouping && HideGroupedColumns) || _groupDescriptors.Count == 0)
                return Array.Empty<GridColumn>();

            var result = new List<GridColumn>(_groupDescriptors.Count);
            foreach (var gd in _groupDescriptors)
            {
                var col = Columns.FirstOrDefault(c => string.Equals(c.Field, gd.Field, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                    result.Add(col);
            }
            return result;
        }
    }

    private bool HasAnyData =>
        AllowGrouping && _groupDescriptors.Count > 0
            ? GroupedData.Any()
            : PagedData.Any();

    private IEnumerable<TValue> FilteredData
    {
        get
        {
            var data = DataSource ?? Enumerable.Empty<TValue>();

            foreach (var kvp in _columnStates)
            {
                var colField = kvp.Key;
                var state = kvp.Value;

                if (!string.IsNullOrEmpty(state.FilterValue))
                {
                    var filterVal = state.FilterValue;
                    data = data.Where(item =>
                    {
                        var val = GetFilterRawValue(item, colField)?.ToString() ?? "";
                        return PassesTextFilter(val, filterVal, state.FilterOperator);
                    });
                }

                if (state.UseCheckedFilter)
                {
                    var checkedVals = state.CheckedFilterValues;
                    data = data.Where(item =>
                    {
                        var val = GetFilterRawValue(item, colField)?.ToString() ?? "";
                        return checkedVals.Contains(val);
                    });
                }

                if (state.UseNumericRangeFilter || state.UseNumericBoundsFilter)
                {
                    var ranges = state.UseNumericRangeFilter
                        ? GetNumericFilterRanges(colField)
                        : Array.Empty<NumericFilterRange>();
                    data = data.Where(item => PassesNumericFilter(colField, GetFilterRawValue(item, colField), state, ranges));
                }
            }

            if (_expressionFilterRoot != null)
            {
                data = data.Where(PassesExpressionFilter);
            }

            if (!string.IsNullOrEmpty(SearchText))
            {
                var searchLower = SearchText.ToLower();
                data = data.Where(item =>
                    VisibleColumns.Any(col =>
                    {
                        var val = GetPropertyValue(item, col.Field)?.ToString() ?? "";
                        return val.Contains(searchLower, StringComparison.OrdinalIgnoreCase);
                    }));
            }

            return data;
        }
    }

    private IEnumerable<TValue> SortedData
    {
        get
        {
            var data = FilteredData;
            var sortedCol = _columnStates.FirstOrDefault(kvp => kvp.Value.SortDirection.HasValue);
            if (sortedCol.Key != null)
            {
                var sortField = sortedCol.Key;
                if (sortedCol.Value.SortDirection == SortDirection.Ascending)
                    data = data.OrderBy(item => GetPropertyValue(item, sortField));
                else
                    data = data.OrderByDescending(item => GetPropertyValue(item, sortField));
            }
            return data;
        }
    }

    private IEnumerable<TValue> PagedData
    {
        get
        {
            var data = SortedData;
            _pageState.TotalRecords = data.Count();
            EnsureCurrentPageInRange();

            if (!IsPagingActive)
                return data;

            return data
                .Skip((_pageState.CurrentPage - 1) * _pageState.PageSize)
                .Take(_pageState.PageSize);
        }
    }

    private IEnumerable<GroupResult<TValue>> GroupedData
    {
        get
        {
            if (_groupDescriptors.Count == 0)
                return Enumerable.Empty<GroupResult<TValue>>();

            var data = SortedData.ToList();
            _pageState.TotalRecords = data.Count;
            return BuildGroups(data, 0, "");
        }
    }

    private IEnumerable<GroupResult<TValue>> BuildGroups(IEnumerable<TValue> data, int level, string parentPath)
    {
        if (level >= _groupDescriptors.Count)
            return Enumerable.Empty<GroupResult<TValue>>();

        var gd = _groupDescriptors[level];
        var groups = data
            .GroupBy(item => GetPropertyValue(item, gd.Field)?.ToString() ?? "(empty)")
            .Select(g =>
            {
                var allItems = g.ToList();
                var groupPath = string.IsNullOrEmpty(parentPath)
                    ? $"{gd.Field}:{g.Key}"
                    : $"{parentPath}/{gd.Field}:{g.Key}";
                var group = new GroupResult<TValue>
                {
                    Field = gd.Field,
                    HeaderText = gd.HeaderText,
                    Key = g.Key,
                    GroupPath = groupPath,
                    Count = allItems.Count,
                    Items = level == _groupDescriptors.Count - 1 ? allItems : Enumerable.Empty<TValue>(),
                    SubGroups = level < _groupDescriptors.Count - 1
                        ? BuildGroups(allItems, level + 1, groupPath)
                        : Enumerable.Empty<GroupResult<TValue>>()
                };

                if (AggregateRows is { Count: > 0 })
                {
                    group.Aggregates = ComputeAggregates(allItems);
                }

                return group;
            })
            .ToList();

        if (_expandAllGroups)
        {
            ApplyGroupCollapseState(groups, collapsed: false);
        }

        if (_allGroupsCollapsed)
        {
            ApplyGroupCollapseState(groups, collapsed: true);
        }
        else if (!_expandAllGroups)
        {
            foreach (var group in groups)
                group.IsCollapsed = _collapsedGroupPaths.Contains(group.GroupPath);
        }

        return groups;
    }

    private Dictionary<string, object?> ComputeAggregates(IEnumerable<TValue> items)
    {
        var result = new Dictionary<string, object?>();
        if (AggregateRows == null) return result;

        var itemsList = items.ToList();
        if (itemsList.Count == 0) return result;

        foreach (var aggRow in AggregateRows)
        {
            foreach (var aggCol in aggRow.Columns)
            {
                var key = $"{aggCol.Field}_{aggCol.Type}";
                if (result.ContainsKey(key)) continue;

                var values = itemsList
                    .Select(item => GetPropertyValue(item, aggCol.Field))
                    .Where(v => v != null)
                    .Select(v =>
                    {
                        if (v is double d) return d;
                        if (v is int i) return (double)i;
                        if (v is decimal dec) return (double)dec;
                        if (v is float f) return (double)f;
                        if (v is long l) return (double)l;
                        if (double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                        return (double?)null;
                    })
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (values.Count == 0)
                {
                    result[key] = null;
                    continue;
                }

                double computed = aggCol.Type switch
                {
                    AggregateType.Sum => values.Sum(),
                    AggregateType.Average => values.Average(),
                    AggregateType.Count => values.Count,
                    AggregateType.Min => values.Min(),
                    AggregateType.Max => values.Max(),
                    _ => 0
                };

                result[key] = computed;
            }
        }

        return result;
    }

    internal string FormatAggregateValue(AggregateColumn aggCol, object? value, string? templateOverride = null)
    {
        if (value == null) return "";

        string formatted;
        if (!string.IsNullOrEmpty(aggCol.Format) && value is IFormattable formattable)
            formatted = formattable.ToString(aggCol.Format, CultureInfo.CurrentCulture);
        else
            formatted = value.ToString() ?? "";

        var template = templateOverride ?? aggCol.FooterTemplate ?? "{value}";
        var text = template.Replace("{value}", formatted);
        text = text.Replace("Grand Total:", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Sub Total:", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Total:", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
        return text;
    }

    public void ToggleExpandCollapseAll()
    {
        if (_allGroupsCollapsed)
        {
            _expandAllGroups = true;
            _allGroupsCollapsed = false;
            _collapsedGroupPaths.Clear();
        }
        else
        {
            _expandAllGroups = false;
            _allGroupsCollapsed = true;
        }
        StateHasChanged();
    }

    public Task CollapseAllGroupAsync()
    {
        _allGroupsCollapsed = true;
        _expandAllGroups = false;
        return InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        _pageState.PageSize = ResolvedPageSize;
        _lastHostResolvedPageSize = ResolvedPageSize;

        if (GroupColumns is { Count: > 0 })
        {
            foreach (var colField in GroupColumns)
            {
                if (!_groupDescriptors.Any(g => g.Field == colField))
                {
                    var col = VisibleColumns.FirstOrDefault(c => c.Field == colField);
                    _groupDescriptors.Add(new GroupDescriptor
                    {
                        Field = colField,
                        HeaderText = col?.DisplayHeader ?? colField
                    });
                }
            }
            _lastSyncedGroupColumns = GroupColumns;
        }
    }

    private List<string>? _lastSyncedGroupColumns;

    private void SyncGroupDescriptorsFromParameter()
    {
        if (ReferenceEquals(GroupColumns, _lastSyncedGroupColumns)) return;
        _lastSyncedGroupColumns = GroupColumns;

        var desired = GroupColumns ?? new List<string>();
        var currentFields = _groupDescriptors
            .Select(g => g.Field)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredFields = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (desiredFields.SetEquals(currentFields)) return;

        _groupDescriptors.Clear();
        foreach (var colField in desired)
        {
            var col = VisibleColumns.FirstOrDefault(c => c.Field == colField)
                ?? Columns.FirstOrDefault(c => c.Field == colField);
            _groupDescriptors.Add(new GroupDescriptor
            {
                Field = colField,
                HeaderText = col?.DisplayHeader ?? colField
            });
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            if (GroupColumns is { Count: > 0 } && _groupDescriptors.Count == 0)
            {
                foreach (var colField in GroupColumns)
                {
                    var col = VisibleColumns.FirstOrDefault(c => c.Field == colField);
                    if (col != null && !_groupDescriptors.Any(g => g.Field == colField))
                    {
                        _groupDescriptors.Add(new GroupDescriptor
                        {
                            Field = colField,
                            HeaderText = col.DisplayHeader
                        });
                    }
                }
            }
            StateHasChanged();
        }

        await EnsureGridKeyboardTrapRegisteredAsync();
        await EnsureHeaderDragPreviewRegisteredAsync();
        await EnsureRowDragSelectionAutoScrollRegisteredAsync();

        if (!string.IsNullOrEmpty(PersistenceKey)
            && PersistenceKey != _gridSettingsLoadedKey
            && _columnsContainer != null
            && Columns.Count > 0
            && GridSettingsStore != null)
        {
            _gridSettingsLoadedKey = PersistenceKey;
            await LoadGridSettingsAsync();
        }
        else if (_gridSettingsLoaded)
        {
            ReapplyAfterRebuildIfNeeded();
        }

        if (_autoWidthPending)
        {
            _autoWidthPending = false;
            if (EnsureAutoColumnWidths())
                StateHasChanged();
        }

        await EnsureTrailingNewRowIfNeededAsync();

        if (_pendingBatchEditFocus && !string.IsNullOrEmpty(_batchEditField))
        {
            if (!HasElementReference(_batchEditInputRef) && _pendingBatchEditFocusRetries < 3)
            {
                _pendingBatchEditFocusRetries++;
                await InvokeAsync(StateHasChanged);
                return;
            }

            var selectAll = _pendingBatchEditSelectAll;
            var clientX = _pendingBatchEditClientX;
            var scrollIntoView = _pendingBatchEditScrollIntoView;
            _pendingBatchEditFocus = false;
            _pendingBatchEditSelectAll = false;
            _pendingBatchEditClientX = null;
            _pendingBatchEditScrollIntoView = false;
            _pendingBatchEditFocusRetries = 0;

            if (scrollIntoView)
            {
                try { await _batchEditInputRef.FocusAsync(preventScroll: false); }
                catch { /* best-effort: cell may have re-rendered away */ }
                return;
            }

            try
            {
                _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                    "import", GridJsModulePath);

                if (selectAll)
                {
                    await _gridJsModule.InvokeVoidAsync("selectAllInputContents", _batchEditInputRef);
                }
                else
                {
                    await _gridJsModule.InvokeVoidAsync("focusInputAtClientX", _batchEditInputRef, clientX);
                }
            }
            catch (Exception)
            {
                try { await _batchEditInputRef.FocusAsync(preventScroll: true); }
                catch {  }
            }
        }

        if (_pendingActiveCellScrollIntoView)
        {
            _pendingActiveCellScrollIntoView = false;
            await EnsureActiveCellVisibleAsync();
        }
    }

    private async Task EnsureActiveCellVisibleAsync()
    {
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("ensureActiveGridCellVisible", _gridHostElement);
        }
        catch (Exception)
        {
        }
    }

    private int GetKeyboardPageRowCount()
    {
        if (TryParsePixelSize(Height ?? string.Empty, out var heightPx))
        {
            var rowHeight = RowHeight > 0 ? RowHeight : Math.Max(18, (int)Math.Ceiling(MinRowHeight));
            return Math.Max(1, (int)Math.Floor(Math.Max(0, heightPx - 24) / rowHeight));
        }

        return IsPagingActive && _pageState.PageSize > 0
            ? Math.Max(1, Math.Min(_pageState.PageSize, 20))
            : 20;
    }

    private static string GridJsModulePath =>
        $"./_content/{typeof(GridControl<TValue>).Assembly.GetName().Name}/grid-control.js";

    private async ValueTask<IJSObjectReference?> GetGridJsModuleAsync()
    {
        try
        {
            return _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadGridSettingsAsync()
    {
        if (string.IsNullOrEmpty(PersistenceKey) || GridSettingsStore == null) return;
        try
        {
            var settings = await GridSettingsStore.LoadAsync(PersistenceKey);
            if (settings == null)
            {
                _gridSettingsLoaded = true;
                _lastAppliedColumnSignature = ComputeColumnSignature();
                return;
            }
            ApplyLoadedSettings(settings);
            _gridSettingsLoaded = true;
            _lastAppliedSettings = settings;
            _lastAppliedColumnSignature = ComputeColumnSignature();
            StateHasChanged();
        }
        catch (Exception)
        {
            _gridSettingsLoaded = true;
        }
    }

    private void ApplyLoadedSettings(GridSettings settings)
    {
        if (settings.HeaderOverrides != null)
        {
            foreach (var kv in settings.HeaderOverrides)
                _headerOverrides[kv.Key] = kv.Value;
        }
        if (settings.Visibility != null)
        {
            foreach (var kv in settings.Visibility)
                _visibilityOverrides[kv.Key] = kv.Value;
        }
        if (settings.Widths != null)
        {
            foreach (var col in Columns)
                if (settings.Widths.TryGetValue(col.Field, out var w))
                    col.RuntimeWidth = w;
        }
        if (settings.ColumnOrder is { Count: > 0 } && _columnsContainer != null)
        {
            _columnsContainer.ReorderColumns(settings.ColumnOrder);
        }
        if (settings.GroupColumns != null)
        {
            _groupDescriptors.Clear();
            foreach (var f in settings.GroupColumns)
            {
                var col = Columns.FirstOrDefault(c => c.Field == f);
                if (col == null) continue;
                _groupDescriptors.Add(new GroupDescriptor
                {
                    Field = f,
                    HeaderText = col.DisplayHeader
                });
            }
        }

    }

    private string ComputeColumnSignature() => string.Join("|", Columns.Select(c => c.Field));

    private void ReapplyAfterRebuildIfNeeded()
    {
        if (!_gridSettingsLoaded || _lastAppliedSettings == null || Columns.Count == 0)
            return;
        var sig = ComputeColumnSignature();
        if (sig == _lastAppliedColumnSignature) return;
        ApplyLoadedSettings(_lastAppliedSettings);
        _lastAppliedColumnSignature = ComputeColumnSignature();
        StateHasChanged();
    }

    private async Task SaveSnapshotSettingsAsync(List<ChooseColumnDescriptor> snapshot)
    {
        if (!_gridSettingsLoaded || string.IsNullOrEmpty(PersistenceKey) || GridSettingsStore == null)
            return;

        var settings = new GridSettings
        {
            ColumnOrder     = snapshot.Select(c => c.Field).Where(f => !string.IsNullOrEmpty(f)).ToList(),
            Visibility      = snapshot.ToDictionary(c => c.Field, c => c.Visible),
            Widths          = Columns.Where(c => c.RuntimeWidth.HasValue && !string.IsNullOrEmpty(c.Field))
                                     .ToDictionary(c => c.Field, c => c.RuntimeWidth!.Value),
            HeaderOverrides = _headerOverrides.Count > 0 ? new Dictionary<string, string>(_headerOverrides) : null,
            GroupColumns    = _groupDescriptors.Select(g => g.Field).ToList()
        };
        _lastAppliedSettings = settings;
        try { await GridSettingsStore.SaveAsync(PersistenceKey, settings); }
        catch (Exception) {  }
    }

    private GridSettings BuildCurrentSnapshot()
    {
        return new GridSettings
        {
            ColumnOrder     = Columns.Select(c => c.Field).Where(f => !string.IsNullOrEmpty(f)).ToList(),
            Visibility      = _visibilityOverrides.Count > 0 ? new Dictionary<string, bool>(_visibilityOverrides) : null,
            HeaderOverrides = _headerOverrides.Count > 0 ? new Dictionary<string, string>(_headerOverrides) : null,
            Widths          = Columns.Where(c => c.RuntimeWidth.HasValue && !string.IsNullOrEmpty(c.Field))
                                     .ToDictionary(c => c.Field, c => c.RuntimeWidth!.Value),
            GroupColumns    = _groupDescriptors.Select(g => g.Field).ToList()
        };
    }

    private async Task FireLayoutChangedAsync()
    {
        if (!OnLayoutChanged.HasDelegate) return;
        await OnLayoutChanged.InvokeAsync(BuildCurrentSnapshot());
    }

    private async Task SaveGridSettingsAsync()
    {
        if (!_gridSettingsLoaded || string.IsNullOrEmpty(PersistenceKey) || GridSettingsStore == null)
            return;

        var settings = BuildCurrentSnapshot();
        _lastAppliedSettings = settings;
        _lastAppliedColumnSignature = ComputeColumnSignature();
        try
        {
            await GridSettingsStore.SaveAsync(PersistenceKey, settings);
        }
        catch (Exception)
        {
        }
    }

    protected override void OnParametersSet()
    {
        var resolvedPageSize = ResolvedPageSize;
        if (_lastHostResolvedPageSize != resolvedPageSize)
        {
            _pageState.PageSize = resolvedPageSize;
            _lastHostResolvedPageSize = resolvedPageSize;
            _pageState.CurrentPage = 1;
        }
        _autoWidthPending = true;
        EnsureThemeInitialized();
        EnsureAdvancedViewInitialized();
        ResetFiltersIfDataSourceChanged();
        ClearSelectionIfDataSourceChanged();

        if (DefaultGroupsCollapsed && !_appliedDefaultCollapsed)
        {
            _allGroupsCollapsed = true;
            _expandAllGroups = false;
            _appliedDefaultCollapsed = true;
        }

        SyncGroupDescriptorsFromParameter();
    }

    private void ResetFiltersIfDataSourceChanged()
    {
        if (!_filterDataSourceCaptured)
        {
            _lastFilterDataSource = DataSource;
            _filterDataSourceCaptured = true;
            return;
        }

        if (ReferenceEquals(_lastFilterDataSource, DataSource))
            return;

        _lastFilterDataSource = DataSource;
        if (ClearFiltersOnDataSourceChange)
            ClearAllFilterState();
    }

    private void ClearSelectionIfDataSourceChanged()
    {
        var signature = ComputeSelectionDataSourceSignature();
        if (!_selectionDataSourceCaptured)
        {
            _lastSelectionDataSource = DataSource;
            _lastSelectionDataSourceSignature = signature;
            _selectionDataSourceCaptured = true;
            return;
        }

        if (ReferenceEquals(_lastSelectionDataSource, DataSource)
            && signature.Equals(_lastSelectionDataSourceSignature))
            return;

        _lastSelectionDataSource = DataSource;
        _lastSelectionDataSourceSignature = signature;
        _runtimeRowHeights.Clear();
        ClearTransientSelectionState(clearRows: true);
    }

    private DataSourceSelectionSignature ComputeSelectionDataSourceSignature()
    {
        if (DataSource == null)
            return new DataSourceSelectionSignature(0, 0);

        var hash = new HashCode();
        var count = 0;

        if (DataSource is IList<TValue> list)
        {
            count = list.Count;
            AddSelectionSignatureSamples(ref hash, list);
            return new DataSourceSelectionSignature(count, hash.ToHashCode());
        }

        if (DataSource is IReadOnlyList<TValue> readOnlyList)
        {
            count = readOnlyList.Count;
            AddSelectionSignatureSamples(ref hash, readOnlyList);
            return new DataSourceSelectionSignature(count, hash.ToHashCode());
        }

        foreach (var item in DataSource)
        {
            if (count < 32 || count % 128 == 0)
                hash.Add(GetSelectionIdentityHash(item));
            count++;
        }

        return new DataSourceSelectionSignature(count, hash.ToHashCode());
    }

    private static void AddSelectionSignatureSamples(ref HashCode hash, IReadOnlyList<TValue> list)
    {
        var limit = Math.Min(list.Count, 32);
        for (var i = 0; i < limit; i++)
            hash.Add(GetSelectionIdentityHash(list[i]));

        if (list.Count > limit)
            hash.Add(GetSelectionIdentityHash(list[^1]));
    }

    private static void AddSelectionSignatureSamples(ref HashCode hash, IList<TValue> list)
    {
        var limit = Math.Min(list.Count, 32);
        for (var i = 0; i < limit; i++)
            hash.Add(GetSelectionIdentityHash(list[i]));

        if (list.Count > limit)
            hash.Add(GetSelectionIdentityHash(list[list.Count - 1]));
    }

    private static int GetSelectionIdentityHash(TValue? item)
    {
        if (item == null)
            return 0;

        var type = item.GetType();
        return type.IsValueType || item is string
            ? EqualityComparer<TValue>.Default.GetHashCode(item)
            : RuntimeHelpers.GetHashCode(item);
    }

    private void ClearTransientSelectionState(bool clearRows)
    {
        if (clearRows)
            _selectedItems.Clear();

        _selectedCells.Clear();
        _activeCell = null;
        _lastSelectedCell = null;
        _lastSelectedRowIndex = null;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        _lastSelectedItem = default;
        _isDragSelecting = false;
        ClearCellDragState();

        _typeAheadBuffer = "";
        ResetRowSelectionTypeAheadTarget();
        _batchEditItem = default;
        _batchEditRowIndex = -1;
        _batchEditField = null;
        _batchEditValue = null;
        _batchEditDirty = false;
        _batchEditReplaceOnFirstInput = false;
        _batchDropdownOpenOnRender = false;
        _pendingBatchEditFocus = false;
        _pendingBatchEditSelectAll = false;
        _pendingBatchEditClientX = null;
        _pendingBatchEditScrollIntoView = false;
        ClearKeyboardNavigationSource();
    }

    private async Task HandleSort(GridColumn col)
    {
        if (!AllowSorting || !col.AllowSorting || string.IsNullOrEmpty(col.Field))
            return;

        var state = GetColumnState(col.Field);

        if (EventsRef?.Sorting.HasDelegate == true)
        {
            var args = new SortEventArgs
            {
                Field = col.Field,
                Direction = state.SortDirection == SortDirection.Ascending
                    ? SortDirection.Descending : SortDirection.Ascending
            };
            await EventsRef.Sorting.InvokeAsync(args);
            if (args.Cancel) return;
        }

        if (!AllowMultiSorting)
        {
            foreach (var kvp in _columnStates)
                if (kvp.Key != col.Field)
                    kvp.Value.SortDirection = null;
        }

        if (!state.SortDirection.HasValue)
            state.SortDirection = SortDirection.Ascending;
        else if (state.SortDirection == SortDirection.Ascending)
            state.SortDirection = SortDirection.Descending;
        else
            state.SortDirection = null;

        _pageState.CurrentPage = 1;

        if (EventsRef?.Sorted.HasDelegate == true)
            await EventsRef.Sorted.InvokeAsync(new SortEventArgs { Field = col.Field, Direction = state.SortDirection ?? SortDirection.Ascending });
    }

    private async Task ApplyFilter(string field, string? value, TextFilterOperator? filterOperator = null)
    {
        var state = GetColumnState(field);

        if (EventsRef?.Filtering.HasDelegate == true)
        {
            var args = new FilterEventArgs { Field = field, Value = value };
            await EventsRef.Filtering.InvokeAsync(args);
            if (args.Cancel) return;
        }

        state.FilterValue = string.IsNullOrWhiteSpace(value) ? null : value;
        state.FilterOperator = string.IsNullOrWhiteSpace(value)
            ? TextFilterOperator.Contains
            : (filterOperator ?? TextFilterOperator.Contains);
        state.CheckedNumericRangeKeys.Clear();
        state.UseNumericRangeFilter = false;
        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
        _pageState.CurrentPage = 1;

        if (EventsRef?.Filtered.HasDelegate == true)
            await EventsRef.Filtered.InvokeAsync(new FilterEventArgs { Field = field, Value = value });
    }

    private void ToggleFilterPopup(string field, MouseEventArgs e)
    {
        if (_filterPopupField == field)
        {
            CloseFilterPopup();
            return;
        }

        _filterPopupField = field;
        SeedFilterPopupDraft(field);
        _filterPopupX = e.ClientX;
        _filterPopupY = e.ClientY;
    }

    private void CloseFilterPopup()
    {
        _filterPopupField = null;
    }

    private void ToggleCheckboxFilter(string field, string value)
    {
        var selected = !IsFilterValueChecked(field, value);
        SetFilterValueChecked(field, value, selected);
    }

    private void ClearFilter(string field)
    {
        var state = GetColumnState(field);
        ResetColumnFilterState(state);
        ResetFilterPopupDraft(field);
        _pageState.CurrentPage = 1;
    }

    private void ClearFilterFromPopup(string field)
    {
        ResetFilterPopupDraft(field);
        if (_filterPopupAutoApply)
            ClearFilter(field);
    }

    private void ResetFilterPopupDraft(string field)
    {
        SetColumnFilterValueSearch(field, null);
        _numericFilterMinText.Remove(field);
        _numericFilterMaxText.Remove(field);
        if (string.Equals(_filterPopupField, field, StringComparison.Ordinal))
        {
            _filterTextDraft = "";
            _filterOperatorDraft = TextFilterOperator.Contains;
            _filterCheckedDraft = new HashSet<string>(GetDistinctValues(field), StringComparer.Ordinal);
        }
    }

    private static void ResetColumnFilterState(ColumnState state)
    {
        state.FilterValue = null;
        state.FilterOperator = TextFilterOperator.Contains;
        state.CheckedFilterValues.Clear();
        state.UseCheckedFilter = false;
        state.CheckedNumericRangeKeys.Clear();
        state.UseNumericRangeFilter = false;
        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
    }

    private void ClearAllFilterState()
    {
        foreach (var state in _columnStates.Values)
            ResetColumnFilterState(state);

        _columnFilterValueSearch.Clear();
        _numericFilterMinText.Clear();
        _numericFilterMaxText.Clear();
        ClearExpressionFilterState();
        _filterPopupField = null;
        _filterTextDraft = "";
        _filterOperatorDraft = TextFilterOperator.Contains;
        _filterCheckedDraft.Clear();
        _pageState.CurrentPage = 1;
    }

    private List<string> GetDistinctValues(string field)
    {
        return (DataSource ?? Enumerable.Empty<TValue>())
            .Select(item => GetFilterRawValue(item, field)?.ToString() ?? "")
            .Distinct()
            .OrderBy(v => v)
            .ToList();
    }

    private async Task ApplySearchDebounced()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(300, token);
            _pageState.CurrentPage = 1;
            StateHasChanged();
        }
        catch (TaskCanceledException) { }
    }

    private void EnsureCurrentPageInRange()
    {
        if (_pageState.CurrentPage < 1)
        {
            _pageState.CurrentPage = 1;
            return;
        }

        var totalPages = _pageState.TotalPages;
        if (_pageState.CurrentPage > totalPages)
            _pageState.CurrentPage = totalPages;
    }

    private async Task GoToPage(int page)
    {
        EnsureCurrentPageInRange();
        if (page < 1 || page > _pageState.TotalPages || page == _pageState.CurrentPage)
            return;

        if (EventsRef?.PageChanging.HasDelegate == true)
        {
            var args = new PageChangeEventArgs { PreviousPage = _pageState.CurrentPage, CurrentPage = page };
            await EventsRef.PageChanging.InvokeAsync(args);
            if (args.Cancel) return;
        }

        var prev = _pageState.CurrentPage;
        _pageState.CurrentPage = page;

        if (EventsRef?.PageChanged.HasDelegate == true)
            await EventsRef.PageChanged.InvokeAsync(new PageChangeEventArgs { PreviousPage = prev, CurrentPage = page });
    }

    private void HandlePageSizeChange(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var size))
        {
            _pageState.PageSize = size;
            _pageState.CurrentPage = 1;
        }
    }

    private IEnumerable<PageSizeChoice> PageSizeChoices =>
        ResolvedPageSizes.Select(size => new PageSizeChoice(size, size <= 0 ? "All" : $"{size} / page"));

    private Task HandlePageSizeValueChanged(int size)
    {
        _pageState.PageSize = size;
        _pageState.CurrentPage = 1;

        return Task.CompletedTask;
    }

    private sealed record PageSizeChoice(int Value, string Text);

    private IEnumerable<int> GetPageNumbers()
    {
        var total = _pageState.TotalPages;
        var current = _pageState.CurrentPage;
        var count = ResolvedPageButtonCount;

        var start = Math.Max(1, current - count / 2);
        var end = Math.Min(total, start + count - 1);
        start = Math.Max(1, end - count + 1);

        return Enumerable.Range(start, end - start + 1);
    }

    private async Task HandleRowClick(TValue item, int rowIndex, MouseEventArgs? mouseArgs = null)
    {
        await CommitBatchEdit();
        ClearTypeSearchBuffer();
        ClearKeyboardNavigationSource();
        if (mouseArgs?.ShiftKey != true)
            ClearKeyboardRangeSelectionAnchor();

        if (_isDragSelecting)
        {
            _isDragSelecting = false;
            _dragAnchorRowIndex = null;
            _dragAnchorItem = default;
            return;
        }
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;

        if (EventsRef?.OnRecordClick.HasDelegate == true)
            await EventsRef.OnRecordClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex });

        if (!AllowSelection) return;
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell && SelectionSettingsRef?.CheckboxOnly != true)
            return;

        await SelectRow(item, rowIndex, mouseArgs);
        await FocusGridHostAsync();
    }

    private void HandleRowMouseDown(TValue item, int rowIndex, MouseEventArgs args)
    {
        if (!AllowRowDragSelection) return;

        if (args.Button != 0) return;

        if (args.CtrlKey || args.MetaKey || args.ShiftKey) return;

        if (SelectionSettingsRef?.Mode == SelectionMode.Cell && SelectionSettingsRef?.CheckboxOnly != true)
            return;

        var selType = SelectionSettingsRef?.Type ?? SelectionType.Single;
        if (selType != SelectionType.Multiple) return;
        if (!AllowSelection) return;

        _dragAnchorItem = item;
        _dragAnchorRowIndex = ResolveRowIndex(item, rowIndex);
        _isDragSelecting = false;   // promoted to first mouseenter

        _ = ClearGridTextSelectionAsync();
    }

    private async Task HandleCellMouseDown(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);

        if (args.Button == 0)
        {
            var previousActiveCell = _activeCell;
            var activeCellChanged = previousActiveCell?.RowIndex != resolvedRowIndex
                || previousActiveCell?.CellIndex != cellIndex;

            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
                && activeCellChanged
                && _typeAheadBuffer.Length > 0)
            {
                await CommitPendingSingleCellTypeAheadAsync();
            }

            SetActiveCell(resolvedRowIndex, cellIndex);
            ClearKeyboardNavigationSource();
            if (!args.ShiftKey)
                ClearKeyboardRangeSelectionAnchor();
            CaptureRowSelectionTypeAheadTarget(cellIndex);
            if (CanStartSingleCellColumnDrag(cellIndex))
            {
                _cellDragAnchor = (resolvedRowIndex, cellIndex);
                _isCellDragSelecting = false;
            }
        }

        HandleRowMouseDown(item, rowIndex, args);
    }

    private void HandleGridMouseUp(MouseEventArgs args)
    {
        if (_isDragSelecting || _isCellDragSelecting) return;

        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        _cellDragAnchor = null;
    }

    private async Task HandleCellMouseEnter(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);

        if (!_cellDragAnchor.HasValue)
        {
            return;
        }

        if ((args.Buttons & 1) == 0)
        {
            ClearCellDragState();
            return;
        }

        var anchor = _cellDragAnchor.Value;
        if (cellIndex != anchor.CellIndex)
            return;

        if (resolvedRowIndex == anchor.RowIndex && !_isCellDragSelecting)
            return;

        _isCellDragSelecting = true;
        SelectSingleCellColumnDragRange(anchor.RowIndex, resolvedRowIndex, anchor.CellIndex);
        _lastSelectedCell = anchor;
        await InvokeAsync(StateHasChanged);
    }

    private bool CanStartSingleCellColumnDrag(int cellIndex)
    {
        if (!SingleCellColumnMassEditEnabled)
            return false;
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
            return false;

        var col = VisibleColumns.ElementAtOrDefault(cellIndex);
        return col != null
            && (col.AllowEditing || col.AllowCellDragSelection)
            && !col.IsPrimaryKey
            && col.Type != ColumnType.CheckBox
            && !string.IsNullOrWhiteSpace(col.Field);
    }

    private void SelectSingleCellColumnDragRange(int startRowIndex, int endRowIndex, int cellIndex)
    {
        _selectedCells.Clear();

        var start = Math.Min(startRowIndex, endRowIndex);
        var end = Math.Max(startRowIndex, endRowIndex);
        for (var i = start; i <= end; i++)
            _selectedCells.Add((i, cellIndex));
    }

    private void ClearCellDragState()
    {
        _cellDragAnchor = null;
        _isCellDragSelecting = false;
    }

    private void RememberKeyboardNavigationSource(TValue item, int rowIndex, int cellIndex)
    {
        _lastKeyboardNavigationItem = item;
        _lastKeyboardNavigationRowIndex = ResolveRowIndex(item, rowIndex);
        _lastKeyboardNavigationCellIndex = cellIndex;
        _hasLastKeyboardNavigationSource = true;
    }

    private void ClearKeyboardNavigationSource()
    {
        _lastKeyboardNavigationItem = default;
        _lastKeyboardNavigationRowIndex = -1;
        _lastKeyboardNavigationCellIndex = -1;
        _hasLastKeyboardNavigationSource = false;
    }

    private void ClearKeyboardRangeSelectionAnchor()
    {
        _keyboardRangeAnchorItem = default;
        _keyboardRangeAnchorCellIndex = -1;
    }

    private void CaptureRowSelectionTypeAheadTarget(int cellIndex)
    {
        var col = VisibleColumns.ElementAtOrDefault(cellIndex);
        var field = CanReceiveTypeAhead(col) ? col!.Field : null;

        _rowSelectionTypeAheadTargetCaptured = true;
        if (!string.Equals(_rowSelectionTypeAheadTargetField, field, StringComparison.OrdinalIgnoreCase))
        {
            _rowSelectionTypeAheadTargetField = field;
            ClearTypeAheadBuffer();
        }
    }

    private void ResetRowSelectionTypeAheadTarget()
    {
        _rowSelectionTypeAheadTargetCaptured = false;
        _rowSelectionTypeAheadTargetField = null;
    }

    private bool CanReceiveTypeAhead(GridColumn? col)
    {
        return col != null
            && !string.IsNullOrWhiteSpace(col.Field)
            && col.AllowEditing
            && !col.IsPrimaryKey
            && col.Type != ColumnType.CheckBox
            && EditSettingsRef?.AllowEditing != false;
    }

    private void ClearTypeAheadBuffer()
    {
        if (_typeAheadBuffer.Length == 0)
            return;

        _typeAheadBuffer = "";
        _ = NotifyTypeAheadChangedAsync();
    }

    private async Task ClearGridTextSelectionAsync()
    {
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("clearTextSelection");
        }
        catch (Exception)
        {
        }
    }

    private async Task EnsureGridKeyboardTrapRegisteredAsync()
    {
        if (_gridKeyboardTrapRegistered) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerGridKeyboardTrap", _gridHostElement);
            _gridKeyboardTrapRegistered = true;
        }
        catch (Exception)
        {
        }
    }

    private async Task EnsureHeaderDragPreviewRegisteredAsync()
    {
        if (_headerDragPreviewRegistered) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerHeaderDragPreview", _gridHostElement);
            _headerDragPreviewRegistered = true;
        }
        catch (Exception)
        {
        }
    }

    private async Task EnsureRowDragSelectionAutoScrollRegisteredAsync()
    {
        if (_rowDragSelectionAutoScrollRegistered) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            _gridDotNetRef ??= DotNetObjectReference.Create(this);
            await _gridJsModule.InvokeVoidAsync("registerRowDragSelectionAutoScroll", _gridHostElement, _gridDotNetRef);
            _rowDragSelectionAutoScrollRegistered = true;
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleRowMouseEnter(TValue item, int rowIndex, MouseEventArgs args)
    {
        if (_dragAnchorItem == null) return;

        if ((args.Buttons & 1) == 0)
        {
            _dragAnchorItem = default;
            _dragAnchorRowIndex = null;
            _isDragSelecting = false;
            return;
        }

        await ContinueRowDragSelectionAsync(item, rowIndex);
    }

    [JSInvokable]
    public async Task ContinueRowDragSelectionFromBrowserAsync(int visibleRowIndex)
    {
        if (_dragAnchorItem == null) return;

        var visible = GetVisibleRowItems();
        if (visibleRowIndex < 0 || visibleRowIndex >= visible.Count) return;

        var item = visible[visibleRowIndex];
        await ContinueRowDragSelectionAsync(item, ResolveRowIndex(item, visibleRowIndex));
    }

    private async Task ContinueRowDragSelectionAsync(TValue item, int rowIndex)
    {
        var anchorItem = _dragAnchorItem;
        if (anchorItem == null) return;

        var visible = GetVisibleRowItems();
        var anchorIdx = visible.IndexOf(anchorItem);
        var currentIdx = visible.IndexOf(item);
        if (anchorIdx < 0 || currentIdx < 0) return;

        if (anchorIdx == currentIdx && !_isDragSelecting)
        {
            return;
        }

        _isDragSelecting = true;

        var start = Math.Min(anchorIdx, currentIdx);
        var end = Math.Max(anchorIdx, currentIdx);

        _selectedItems.Clear();
        for (var i = start; i <= end && i < visible.Count; i++)
        {
            _selectedItems.Add(visible[i]);
        }
        _lastSelectedItem = item;
        _lastSelectedRowIndex = ResolveRowIndex(item, rowIndex);

        await InvokeAsync(StateHasChanged);
        await NotifySelectionChangedAsync(GridSelectionChangeSource.MouseDrag);
    }

    private async Task HandleRowDblClick(TValue item, int rowIndex)
    {
        await InvokeRecordDoubleClickAsync(item, rowIndex);

        if (EditSettingsRef?.AllowEditing == true && EditSettingsRef.AllowEditOnDblClick)
            await StartEdit(item, rowIndex);
    }

    private async Task InvokeRecordDoubleClickAsync(TValue item, int rowIndex)
    {
        if (EventsRef?.OnRecordDoubleClick.HasDelegate == true)
            await EventsRef.OnRecordDoubleClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex });
    }

    private bool ShouldHandleCellDoubleClick(GridColumn col)
    {
        return EventsRef?.OnRecordDoubleClick.HasDelegate == true
            || (col.ShowEditButton
                && EventsRef?.OnEditButtonClick.HasDelegate == true
                && !string.IsNullOrEmpty(col.Field))
            || IsBatchDoubleClickEditCell(col);
    }

    private bool IsBatchDoubleClickEditCell(GridColumn col)
    {
        return EditSettingsRef?.Mode == EditMode.Batch
            && EditSettingsRef.AllowEditOnDblClick
            && col.AllowEditing
            && !col.IsPrimaryKey
            && !string.IsNullOrEmpty(col.Field);
    }

    private async Task HandleCellDblClick(TValue item, int rowIndex, GridColumn col, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);

        if (EventsRef?.OnRecordDoubleClick.HasDelegate == true)
        {
            if (AllowSelection
                && SelectionSettingsRef?.Mode != SelectionMode.Cell
                && SelectionSettingsRef?.CheckboxOnly != true)
            {
                await SelectRow(item, resolvedRowIndex);
            }

            await EventsRef.OnRecordDoubleClick.InvokeAsync(new CellClickEventArgs<TValue>
            {
                Data = item,
                RowIndex = resolvedRowIndex,
                Column = col.Field ?? string.Empty
            });
            return;
        }

        if (col.ShowEditButton
            && EventsRef?.OnEditButtonClick.HasDelegate == true
            && !string.IsNullOrEmpty(col.Field))
        {
            await HandleEditButtonClick(item, col);
            return;
        }

        if (IsBatchDoubleClickEditCell(col))
            await StartBatchEdit(item, resolvedRowIndex, col, args.ClientX, openDropdownOnRender: true);
    }

    private async Task SelectRow(TValue item, int rowIndex, MouseEventArgs? mouseArgs = null)
    {
        var selType = SelectionSettingsRef?.Type ?? SelectionType.Single;
        var isCtrl = mouseArgs?.CtrlKey == true || mouseArgs?.MetaKey == true;
        var isShift = mouseArgs?.ShiftKey == true;

        if (EventsRef?.RowSelecting.HasDelegate == true)
        {
            var args = new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex };
            await EventsRef.RowSelecting.InvokeAsync(args);
            if (args.Cancel) return;
        }

        if (selType == SelectionType.Single && !isCtrl && !isShift)
        {
            var wasSelected = _selectedItems.Contains(item);
            _selectedItems.Clear();
            if (!wasSelected || SelectionSettingsRef?.EnableToggle != true)
                _selectedItems.Add(item);
        }
        else if (selType == SelectionType.Multiple || isCtrl || isShift)
        {
            if (!isCtrl && !isShift && selType == SelectionType.Multiple)
            {
                var wasSelected = _selectedItems.Contains(item);
                var hadMulti = _selectedItems.Count > 1;
                _selectedItems.Clear();
                if (hadMulti || !wasSelected || SelectionSettingsRef?.EnableToggle != true)
                    _selectedItems.Add(item);
            }
            else if (isShift && _lastSelectedItem != null)
            {
                var visible = GetVisibleRowItems();
                var anchorIdx = visible.IndexOf(_lastSelectedItem);
                var currentIdx = visible.IndexOf(item);
                if (anchorIdx >= 0 && currentIdx >= 0)
                {
                    var start = Math.Min(anchorIdx, currentIdx);
                    var end = Math.Max(anchorIdx, currentIdx);

                    if (!isCtrl) _selectedItems.Clear();

                    for (var i = start; i <= end && i < visible.Count; i++)
                    {
                        _selectedItems.Add(visible[i]);
                    }
                }
            }
            else if (isCtrl)
            {
                if (!_selectedItems.Remove(item))
                    _selectedItems.Add(item);
            }
            else
            {
                if (!_selectedItems.Remove(item))
                    _selectedItems.Add(item);
            }
        }

        _lastSelectedItem = item;
        _lastSelectedRowIndex = ResolveRowIndex(item, rowIndex);

        if (EventsRef?.RowSelected.HasDelegate == true)
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex });
        var source = mouseArgs != null
            ? GridSelectionChangeSource.Pointer
            : GridSelectionChangeSource.Programmatic;
        await NotifySelectionChangedAsync(source);
    }

    private bool CanStartRowReorder(TValue item) =>
        ShowRowReorderColumn && (RowReorderPredicate?.Invoke(item) ?? true);

    private bool CanShowRowSelectorHandle(TValue item)
    {
        if (!ShowRowSelectorHandleColumn)
            return false;

        if (RowSelectorHandlePredicate == null)
            return true;

        try
        {
            return RowSelectorHandlePredicate.Invoke(item);
        }
        catch
        {
            return false;
        }
    }

    private bool IsRowSelectorHandleEmphasized(TValue item)
    {
        if (RowSelectorHandleEmphasisPredicate == null)
            return false;

        try
        {
            return RowSelectorHandleEmphasisPredicate.Invoke(item);
        }
        catch
        {
            return false;
        }
    }

    private string RowSelectorHandleShapeClass =>
        RowSelectorHandleShape switch
        {
            GridRowSelectorHandleShape.Button => "button",
            GridRowSelectorHandleShape.CheckBox => "checkbox",
            _ => "half-button"
        };

    private string GetRowSelectorHandleStyle(bool isSelected, bool isEmphasized)
    {
        var background = isSelected
            ? "linear-gradient(#c9d9ec,#8fb0d4)"
            : isEmphasized
                ? "linear-gradient(#eeeeee,#c9c9c9)"
                : "linear-gradient(#f5f5f5,#d8d8d8)";
        var borderColor = isSelected ? "#4f79a7" : isEmphasized ? "#707070" : "#8f8f8f";
        var width = RowSelectorHandleShape switch
        {
            GridRowSelectorHandleShape.Button => "14px",
            GridRowSelectorHandleShape.CheckBox => "13px",
            _ => "10px"
        };
        var height = RowSelectorHandleShape == GridRowSelectorHandleShape.CheckBox ? "13px" : "12px";
        var borderLeft = RowSelectorHandleShape == GridRowSelectorHandleShape.HalfButton ? "border-left:none;" : "";
        var radius = RowSelectorHandleShape switch
        {
            GridRowSelectorHandleShape.HalfButton => "0 2px 2px 0",
            GridRowSelectorHandleShape.CheckBox => "0",
            _ => "1px"
        };
        var boxShadow = RowSelectorHandleShape == GridRowSelectorHandleShape.CheckBox ? "none" : "inset 1px 1px 0 #fff";
        var resolvedBackground = RowSelectorHandleShape == GridRowSelectorHandleShape.CheckBox && !isSelected && !isEmphasized
            ? "#fff"
            : background;

        return "display:inline-flex;align-items:center;justify-content:center;"
            + $"width:{width};height:{height};margin:0;padding:0;border:1px solid {borderColor};"
            + borderLeft
            + $"border-radius:{radius};background:{resolvedBackground};box-shadow:{boxShadow};"
            + "cursor:pointer;vertical-align:middle;appearance:none;-webkit-appearance:none;";
    }

    private string GetRowSelectorHandleMarkStyle(bool isSelected)
    {
        if (!isSelected || RowSelectorHandleShape != GridRowSelectorHandleShape.CheckBox)
            return string.Empty;

        return "width:4px;height:8px;border:solid #000;border-width:0 2px 2px 0;transform:rotate(45deg);";
    }

    private async Task HandleRowSelectorHandleClick(TValue item, int rowIndex, MouseEventArgs args)
    {
        await CommitBatchEdit();
        ClearKeyboardNavigationSource();
        if (!args.ShiftKey)
            ClearKeyboardRangeSelectionAnchor();

        _selectedCells.Clear();
        _activeCell = null;
        _cellDragAnchor = null;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        _isDragSelecting = false;
        _isCellDragSelecting = false;

        if (!AllowSelection)
            return;

        await SelectRow(item, rowIndex, args);
        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task StartRowReorderDrag(TValue item, int rowIndex, DragEventArgs _)
    {
        if (!CanStartRowReorder(item))
            return;

        await CommitBatchEdit();
        ClearTypeSearchBuffer();
        ClearCellDragState();
        _selectedCells.Clear();
        _activeCell = null;
        _lastSelectedCell = null;

        _isRowReorderDragging = true;
        _rowReorderDragItem = item;
        _rowReorderDragSourceIndex = ResolveRowIndex(item, rowIndex);
        _rowReorderDragTargetIndex = _rowReorderDragSourceIndex;
        _isDragSelecting = false;
        _isCellDragSelecting = false;
    }

    private void HandleRowReorderDragOver(TValue item, int rowIndex, DragEventArgs _)
    {
        if (!_isRowReorderDragging || _rowReorderDragItem == null)
            return;

        _rowReorderDragTargetIndex = ResolveRowIndex(item, rowIndex);
    }

    private async Task HandleRowReorderDrop(TValue targetItem, int targetRowIndex, DragEventArgs _)
    {
        if (!_isRowReorderDragging || _rowReorderDragItem == null)
        {
            EndRowReorderDrag();
            return;
        }

        var sourceItem = _rowReorderDragItem;
        var oldIndex = _rowReorderDragSourceIndex;
        var targetIndex = ResolveRowIndex(targetItem, targetRowIndex);
        var newIndex = ComputeRowReorderInsertIndex(sourceItem, targetItem, oldIndex, targetIndex);

        if (oldIndex < 0
            || newIndex < 0
            || EqualityComparer<TValue>.Default.Equals(sourceItem, targetItem))
        {
            EndRowReorderDrag();
            return;
        }

        var args = new RowReorderEventArgs<TValue>
        {
            Data = sourceItem,
            TargetData = targetItem,
            OldIndex = oldIndex,
            NewIndex = newIndex
        };

        if (EventsRef?.RowReordering.HasDelegate == true)
            await EventsRef.RowReordering.InvokeAsync(args);
        if (RowReordering.HasDelegate)
            await RowReordering.InvokeAsync(args);
        if (args.Cancel)
        {
            EndRowReorderDrag();
            return;
        }

        TryReorderMutableDataSource(sourceItem, targetItem);

        var finalIndex = ResolveRowIndex(sourceItem, args.NewIndex);
        if (finalIndex >= 0)
            args.NewIndex = finalIndex;

        _selectedItems.Clear();
        _selectedItems.Add(sourceItem);
        _lastSelectedItem = sourceItem;
        _lastSelectedRowIndex = args.NewIndex;
        if (_activeCell?.RowIndex == oldIndex)
            _activeCell = (args.NewIndex, _activeCell.Value.CellIndex);
        if (_lastSelectedCell?.RowIndex == oldIndex)
            _lastSelectedCell = (args.NewIndex, _lastSelectedCell.Value.CellIndex);

        if (EventsRef?.RowReordered.HasDelegate == true)
            await EventsRef.RowReordered.InvokeAsync(args);
        if (RowReordered.HasDelegate)
            await RowReordered.InvokeAsync(args);

        EndRowReorderDrag();
        await NotifySelectionChangedAsync(GridSelectionChangeSource.Pointer);
        await InvokeAsync(StateHasChanged);
    }

    private int ComputeRowReorderInsertIndex(TValue sourceItem, TValue targetItem, int fallbackOldIndex, int fallbackTargetIndex)
    {
        if (DataSource is IList<TValue> list)
        {
            var from = list.IndexOf(sourceItem);
            var to = list.IndexOf(targetItem);
            if (from >= 0 && to >= 0)
                return from < to ? to - 1 : to;
        }

        return fallbackOldIndex < fallbackTargetIndex ? fallbackTargetIndex - 1 : fallbackTargetIndex;
    }

    private void TryReorderMutableDataSource(TValue sourceItem, TValue targetItem)
    {
        if (DataSource is not IList<TValue> list)
            return;

        var from = list.IndexOf(sourceItem);
        var to = list.IndexOf(targetItem);
        if (from < 0 || to < 0 || from == to)
            return;

        list.RemoveAt(from);
        if (from < to)
            to--;
        to = Math.Clamp(to, 0, list.Count);
        list.Insert(to, sourceItem);
    }

    private void EndRowReorderDrag()
    {
        _rowReorderDragItem = default;
        _rowReorderDragSourceIndex = -1;
        _rowReorderDragTargetIndex = -1;
        _isRowReorderDragging = false;
    }

    private async Task HandleCellClick(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        ClearTypeSearchBuffer();

        if (_isCellDragSelecting)
        {
            ClearCellDragState();
            await FocusGridHostAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (_isDragSelecting)
        {
            _isDragSelecting = false;
            _dragAnchorRowIndex = null;
            _dragAnchorItem = default;
            await FocusGridHostAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var previousActiveCell = _activeCell;
        var activeCellChanged = previousActiveCell?.RowIndex != resolvedRowIndex
            || previousActiveCell?.CellIndex != cellIndex;

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
            && activeCellChanged
            && _typeAheadBuffer.Length > 0)
        {
            await CommitPendingSingleCellTypeAheadAsync();
        }

        SetActiveCell(resolvedRowIndex, cellIndex);
        _cellDragAnchor = null;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;

        if (!AllowSelection)
            return;

        await CommitBatchEdit();

        var clickedCol = VisibleColumns.ElementAtOrDefault(cellIndex);
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
        {
            _selectedCells.Clear();
        }

        var isEditableCell = EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && clickedCol != null
            && clickedCol.AllowEditing
            && !clickedCol.IsPrimaryKey
            && !string.IsNullOrEmpty(clickedCol.Field);
        var isOptionListCell = isEditableCell && clickedCol?.EditOptions?.Any() == true;
        var useSingleCellBatchBehavior = isEditableCell
            && BatchEditBehavior == GridBatchEditBehavior.SingleCell;

        if (SelectionSettingsRef?.Mode != SelectionMode.Cell &&
            SelectionSettingsRef?.CheckboxOnly != true)
        {
            var isPlainClick = !args.CtrlKey && !args.MetaKey && !args.ShiftKey;
            var isActionCell = clickedCol?.EffectiveTemplate != null
                || clickedCol?.Commands is { Count: > 0 };
            var preserveSelection = BatchEditBehavior == GridBatchEditBehavior.MultiRow
                && SelectionSettingsRef?.MultiSelectBehavior == GridMultiSelectBehavior.FullMultiSelect
                && isPlainClick
                && _selectedItems.Count > 1
                && _selectedItems.Contains(item)
                && (isEditableCell || isActionCell);

            if (!preserveSelection)
            {
                if (EventsRef?.OnRecordClick.HasDelegate == true)
                    await EventsRef.OnRecordClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex, Column = clickedCol?.Field ?? "" });

                await SelectRow(item, rowIndex, args);
            }
        }

        if (isEditableCell && !useSingleCellBatchBehavior && clickedCol?.Type == ColumnType.CheckBox)
        {
            await StartBatchEdit(item, resolvedRowIndex, clickedCol!);
            return;
        }

        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
        {
            if (isEditableCell && (EditOnSingleClick || isOptionListCell) && clickedCol?.Type != ColumnType.CheckBox)
            {
                await StartBatchEdit(item, resolvedRowIndex, clickedCol!, args.ClientX);
                return;
            }

            await FocusGridHostAsync();
            return;
        }

        if (EventsRef?.CellSelecting.HasDelegate == true)
        {
            var selectingArgs = new CellSelectingEventArgs<TValue>
            {
                Data = item,
                RowIndex = resolvedRowIndex,
                CellIndex = cellIndex
            };
            await EventsRef.CellSelecting.InvokeAsync(selectingArgs);
            if (selectingArgs.Cancel)
                return;
        }

        var isCtrl = args.CtrlKey || args.MetaKey;
        var isShift = args.ShiftKey;

        if (SingleCellColumnMassEditEnabled)
        {
            SelectSingleCellColumnMassEditCells(resolvedRowIndex, cellIndex, isCtrl, isShift);
        }
        else
        {
            if (!isCtrl && !isShift)
            {
                _selectedCells.Clear();
            }

            if (isShift && _lastSelectedCell.HasValue && _lastSelectedCell.Value.RowIndex == resolvedRowIndex)
            {
                var start = Math.Min(_lastSelectedCell.Value.CellIndex, cellIndex);
                var end = Math.Max(_lastSelectedCell.Value.CellIndex, cellIndex);
                for (var i = start; i <= end; i++)
                {
                    var cell = (resolvedRowIndex, i);
                    if (!_selectedCells.Contains(cell))
                        _selectedCells.Add(cell);
                }
            }
            else
            {
                var cell = (resolvedRowIndex, cellIndex);
                if (isCtrl && _selectedCells.Contains(cell))
                    _selectedCells.Remove(cell);
                else if (!_selectedCells.Contains(cell))
                    _selectedCells.Add(cell);
            }
        }

        _lastSelectedCell = (resolvedRowIndex, cellIndex);

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            var selectedField = VisibleColumns.ElementAtOrDefault(cellIndex)?.Field ?? "";
            var value = GetPropertyValue(item, selectedField);
            await EventsRef.CellSelected.InvokeAsync(new CellSelectEventArgs<TValue>
            {
                Data = item,
                RowIndex = resolvedRowIndex,
                CellIndex = cellIndex,
                CurrentValue = value,
                IsCtrlPressed = isCtrl,
                IsShiftPressed = isShift
            });
        }

        if (isEditableCell && (EditOnSingleClick || isOptionListCell) && clickedCol?.Type != ColumnType.CheckBox)
        {
            await StartBatchEdit(item, resolvedRowIndex, clickedCol!, args.ClientX);
            return;
        }

        await FocusGridHostAsync();
    }

    private void SelectSingleCellColumnMassEditCells(int rowIndex, int cellIndex, bool isCtrl, bool isShift)
    {
        var cell = (rowIndex, cellIndex);

        if (!isCtrl && !isShift)
        {
            if (_selectedCells.Count > 1
                && _selectedCells.Contains(cell)
                && _selectedCells.All(c => c.CellIndex == cellIndex))
                return;

            _selectedCells.Clear();
            _selectedCells.Add(cell);
            return;
        }

        if (_selectedCells.Any(c => c.CellIndex != cellIndex))
            _selectedCells.Clear();

        if (isShift && _lastSelectedCell.HasValue && _lastSelectedCell.Value.CellIndex == cellIndex)
        {
            if (!isCtrl)
                _selectedCells.Clear();

            var start = Math.Min(_lastSelectedCell.Value.RowIndex, rowIndex);
            var end = Math.Max(_lastSelectedCell.Value.RowIndex, rowIndex);
            for (var i = start; i <= end; i++)
            {
                var rangeCell = (i, cellIndex);
                if (!_selectedCells.Contains(rangeCell))
                    _selectedCells.Add(rangeCell);
            }
            return;
        }

        if (isShift && !isCtrl)
            _selectedCells.Clear();

        if (isCtrl && _selectedCells.Contains(cell))
            _selectedCells.Remove(cell);
        else if (!_selectedCells.Contains(cell))
            _selectedCells.Add(cell);
    }

    private async Task FocusGridHostAsync()
    {
        try
        {
            await _gridHostElement.FocusAsync(preventScroll: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSException)
        {
        }
    }

    public Task FocusGridAsync() => FocusGridHostAsync();

    public Task ResumeKeyboardNavigationAsync() => FocusGridHostAsync();

    private void SetActiveCell(int rowIndex, int cellIndex)
    {
        var col = VisibleColumns.ElementAtOrDefault(cellIndex);
        _activeCell = col != null ? (rowIndex, cellIndex) : null;
    }

    private static bool HasElementReference(ElementReference elementReference) =>
        !string.IsNullOrEmpty(elementReference.Id);

    private async Task ActivateCheckboxCellAsync(TValue item, int rowIndex, int cellIndex, bool focusGridHost)
    {
        await CommitBatchEdit();

        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        SetActiveCell(resolvedRowIndex, cellIndex);
        RememberKeyboardNavigationSource(item, resolvedRowIndex, cellIndex);
        _lastSelectedCell = (resolvedRowIndex, cellIndex);

        _selectedCells.Clear();
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
            _selectedCells.Add((resolvedRowIndex, cellIndex));

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            var field = VisibleColumns.ElementAtOrDefault(cellIndex)?.Field ?? string.Empty;
            var value = string.IsNullOrWhiteSpace(field) ? null : GetPropertyValue(item, field);
            await EventsRef.CellSelected.InvokeAsync(new CellSelectEventArgs<TValue>
            {
                Data = item,
                RowIndex = resolvedRowIndex,
                CellIndex = cellIndex,
                CurrentValue = value
            });
        }

        await TryAppendTrailingNewRowFromLastCellAsync(item, resolvedRowIndex, cellIndex, beginEdit: false);

        if (focusGridHost)
            await FocusGridHostAsync();

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleCheckboxKeyDown(TValue item, int rowIndex, int cellIndex, GridColumn col, KeyboardEventArgs e)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);

        if (TryGetHorizontalKeyboardNavigation(e, out var backwards))
        {
            SetActiveCell(resolvedRowIndex, cellIndex);
            RememberKeyboardNavigationSource(item, resolvedRowIndex, cellIndex);
            _lastSelectedCell = (resolvedRowIndex, cellIndex);
            _selectedCells.Clear();
            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
                _selectedCells.Add((resolvedRowIndex, cellIndex));

            await NavigateToAdjacentEditTargetAsync(item, resolvedRowIndex, cellIndex, backwards);
            return;
        }

        if (TryGetVerticalKeyboardNavigation(e, out var rowDelta))
        {
            SetActiveCell(resolvedRowIndex, cellIndex);
            RememberKeyboardNavigationSource(item, resolvedRowIndex, cellIndex);
            _lastSelectedCell = (resolvedRowIndex, cellIndex);
            _selectedCells.Clear();
            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
                _selectedCells.Add((resolvedRowIndex, cellIndex));

            await NavigateToVerticalEditTargetAsync(item, resolvedRowIndex, cellIndex, rowDelta);
            return;
        }

        if (e.Key is " " or "Spacebar" or "Enter" or "NumpadEnter")
        {
            SetActiveCell(resolvedRowIndex, cellIndex);
            RememberKeyboardNavigationSource(item, resolvedRowIndex, cellIndex);
            _lastSelectedCell = (resolvedRowIndex, cellIndex);
            _selectedCells.Clear();
            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
                _selectedCells.Add((resolvedRowIndex, cellIndex));

            await HandleCheckboxToggle(item, col);
        }
    }

    private int ResolveRowIndex(TValue item, int fallbackIndex)
    {
        if (DataSource is IList<TValue> list)
        {
            var idx = list.IndexOf(item);
            if (idx >= 0)
                return idx;
        }

        return fallbackIndex;
    }

    private TValue? GetItemAtResolvedRowIndex(int rowIndex)
    {
        if (rowIndex < 0)
            return default;

        if (DataSource is IList<TValue> list && rowIndex < list.Count)
            return list[rowIndex];

        var visible = GetVisibleRowItems();
        return rowIndex < visible.Count ? visible[rowIndex] : default;
    }

    private List<TValue> GetVisibleRowItems()
    {
        if (_groupDescriptors.Count == 0)
            return PagedData.ToList();

        var result = new List<TValue>();
        void Walk(IEnumerable<GroupResult<TValue>> groups)
        {
            foreach (var g in groups)
            {
                if (g.IsCollapsed) continue;
                var subList = g.SubGroups as ICollection<GroupResult<TValue>> ?? g.SubGroups?.ToList();
                if (subList != null && subList.Count > 0)
                    Walk(subList);
                else if (g.Items != null)
                    result.AddRange(g.Items);
            }
        }
        Walk(GroupedData);
        return result;
    }

    private List<TValue> GetKeyboardNavigationRowItems()
    {
        return _groupDescriptors.Count == 0
            ? SortedData.ToList()
            : GetVisibleRowItems();
    }

    private async Task ToggleRowSelection(TValue item, int rowIndex)
    {
        await SelectRow(item, rowIndex);
    }

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        ToggleSelectAll();
    }

    private void ToggleSelectAll(bool _)
    {
        ToggleSelectAll();
    }

    private void ToggleSelectAll()
    {
        if (AllRowsSelected)
            _selectedItems.Clear();
        else
            foreach (var item in PagedData)
                _selectedItems.Add(item);
        _ = NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
    }

    private async Task StartEdit(TValue item, int rowIndex)
    {
        if (EventsRef?.OnBeginEdit.HasDelegate == true)
        {
            var args = new RowEditEventArgs<TValue> { Data = item, RowIndex = rowIndex };
            await EventsRef.OnBeginEdit.InvokeAsync(args);
            if (args.Cancel) return;
        }

        _editItem = CloneItem(item);
        _editingRowIndex = rowIndex;
        _isEditing = true;
    }

    private void StartAdd()
    {
        _editItem = CreateNewItem();
        _editingRowIndex = -1;
        _isEditing = true;
    }

    private async Task SaveEdit()
    {
        if (_editItem == null) return;

        if (_editingRowIndex >= 0)
        {
            if (EventsRef?.RowUpdating.HasDelegate == true)
            {
                var args = new RowEditEventArgs<TValue> { Data = _editItem, RowIndex = _editingRowIndex };
                await EventsRef.RowUpdating.InvokeAsync(args);
                if (args.Cancel) return;
            }

            var list = DataSource as IList<TValue>;
            if (list != null)
            {
                var pagedList = SortedData.ToList();
                var actualIdx = IsPagingActive
                    ? (_pageState.CurrentPage - 1) * _pageState.PageSize + _editingRowIndex
                    : _editingRowIndex;

                if (actualIdx >= 0 && actualIdx < pagedList.Count)
                {
                    var original = pagedList[actualIdx];
                    var origIdx = list.IndexOf(original);
                    if (origIdx >= 0)
                        CopyProperties(_editItem, list[origIdx]!);
                }
            }

            if (EventsRef?.RowUpdated.HasDelegate == true)
                await EventsRef.RowUpdated.InvokeAsync(new RowEditEventArgs<TValue> { Data = _editItem, RowIndex = _editingRowIndex });
        }
        else
        {
            if (EventsRef?.RowCreating.HasDelegate == true)
            {
                var args = new RowEditEventArgs<TValue> { Data = _editItem };
                await EventsRef.RowCreating.InvokeAsync(args);
                if (args.Cancel) return;
            }

            var list = DataSource as IList<TValue>;
            if (list != null)
            {
                if (EditSettingsRef?.NewRowPosition == NewRowPosition.Top)
                    list.Insert(0, _editItem);
                else
                    list.Add(_editItem);
            }

            if (EventsRef?.RowCreated.HasDelegate == true)
                await EventsRef.RowCreated.InvokeAsync(new RowEditEventArgs<TValue> { Data = _editItem });
        }

        _isEditing = false;
        _editItem = default;
        _editingRowIndex = -1;
    }

    private void CancelEdit()
    {
        _isEditing = false;
        _editItem = default;
        _editingRowIndex = -1;
    }

    private async Task DeleteRow(TValue item, int rowIndex)
    {
        if (EditSettingsRef?.ShowConfirmDialog == true)
        {
        }

        if (EventsRef?.RowDeleting.HasDelegate == true)
        {
            var args = new RowEditEventArgs<TValue> { Data = item, RowIndex = rowIndex };
            await EventsRef.RowDeleting.InvokeAsync(args);
            if (args.Cancel) return;
        }

        var list = DataSource as IList<TValue>;
        list?.Remove(item);
        _selectedItems.Remove(item);

        if (EventsRef?.RowDeleted.HasDelegate == true)
            await EventsRef.RowDeleted.InvokeAsync(new RowEditEventArgs<TValue> { Data = item, RowIndex = rowIndex });
    }

    private async Task HandleCommand(string type, TValue item, int rowIndex)
    {
        switch (type.ToLower())
        {
            case "edit":
                await StartEdit(item, rowIndex);
                break;
            case "delete":
                await DeleteRow(item, rowIndex);
                break;
            case "save":
                await SaveEdit();
                break;
            case "cancel":
                CancelEdit();
                break;
        }
    }

    private async Task StartBatchEdit(TValue item, int rowIndex, GridColumn col, double? clientX = null, bool replaceOnFirstInput = false, bool openDropdownOnRender = false)
    {
        await TryStartBatchEdit(item, rowIndex, col, clientX, replaceOnFirstInput, openDropdownOnRender);
    }

    private async Task<bool> TryStartBatchEdit(TValue item, int rowIndex, GridColumn col, double? clientX = null, bool replaceOnFirstInput = false, bool openDropdownOnRender = false)
    {
        if (!col.AllowEditing || string.IsNullOrEmpty(col.Field) || col.IsPrimaryKey) return false;
        if (EditSettingsRef?.AllowEditing != true || EditSettingsRef.Mode != EditMode.Batch) return false;

        await CommitBatchEdit();

        if (col.Type == ColumnType.CheckBox)
        {
            await HandleCheckboxToggle(item, col);
            return false;
        }

        if (EventsRef?.OnCellEdit.HasDelegate == true)
        {
            var args = new CellEditArgs<TValue> { Data = item, ColumnName = col.Field };
            await EventsRef.OnCellEdit.InvokeAsync(args);
            if (args.Cancel) return false;
        }

        _batchEditItem = item;
        _batchEditRowIndex = ResolveRowIndex(item, rowIndex);
        _batchEditField = col.Field;
        _batchEditValue = GetPropertyValue(item, col.Field)?.ToString() ?? "";
        _batchEditDirty = false;  // Reset on every new edit start.
        _batchEditReplaceOnFirstInput = replaceOnFirstInput;
        _batchDropdownOpenOnRender = openDropdownOnRender && col.EditOptions?.Any() == true;
        _batchEditInputRef = default;
        _pendingBatchEditFocus = true;
        _pendingBatchEditSelectAll = col.SelectAllOnEdit && clientX == null;
        _pendingBatchEditClientX = clientX;
        _pendingBatchEditFocusRetries = 0;
        return true;
    }

    private bool CanToggleCheckboxColumn(GridColumn col)
    {
        return col.Type == ColumnType.CheckBox
            && col.AllowEditing
            && !col.IsPrimaryKey
            && !string.IsNullOrWhiteSpace(col.Field)
            && EditSettingsRef?.AllowEditing == true;
    }

    private Task HandleCheckboxToggle(TValue item, GridColumn col, bool newValue)
    {
        return HandleCheckboxToggle(item, col, new ChangeEventArgs { Value = newValue });
    }

    private async Task HandleCheckboxToggle(TValue item, GridColumn col, ChangeEventArgs? e = null)
    {
        if (!CanToggleCheckboxColumn(col))
            return;

        await CommitBatchEdit();

        if (EventsRef?.OnCellEdit.HasDelegate == true)
        {
            var args = new CellEditArgs<TValue> { Data = item, ColumnName = col.Field };
            await EventsRef.OnCellEdit.InvokeAsync(args);
            if (args.Cancel)
            {
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        var newValue = e?.Value is bool changedValue
            ? changedValue
            : !GetBoolValue(item, col.Field);

        if (!SetPropertyObjectValue(item, col.Field, newValue))
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (EventsRef?.OnCellSave.HasDelegate == true)
        {
            await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
            {
                Data = item,
                ColumnName = col.Field,
                Value = newValue
            });
        }

        await EnsureTrailingNewRowIfNeededAsync();

        var rowIndex = ResolveRowIndex(item, -1);
        var cellIndex = ResolveVisibleColumnIndex(col.Field);
        if (rowIndex >= 0 && cellIndex >= 0)
            await TryAppendTrailingNewRowFromLastCellAsync(item, rowIndex, cellIndex, beginEdit: false);

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleEditButtonClick(TValue item, GridColumn col)
    {
        if (string.IsNullOrWhiteSpace(col.Field))
            return;

        if (EventsRef?.OnEditButtonClick.HasDelegate == true)
        {
            await EventsRef.OnEditButtonClick.InvokeAsync(new CellEditButtonArgs<TValue>
            {
                Data = item,
                ColumnName = col.Field,
                Column = col
            });
        }
    }

    private bool ShouldShowAlwaysEditButton(GridColumn col, TValue item)
    {
        if (!col.ShowEditButton || !col.AlwaysShowEditButton || string.IsNullOrWhiteSpace(col.Field))
            return false;

        if (col.ShowEditButtonPredicate == null)
            return true;

        try
        {
            return col.ShowEditButtonPredicate.Invoke(item!);
        }
        catch
        {
            return false;
        }
    }

    private void RenderDisplayCellContent(RenderTreeBuilder builder, int sequence, TValue item, GridColumn col)
    {
        var text = GetCellDisplayValue(item, col);

        if (TryGetTypeSearchHighlight(text, item, col, out var matchedText, out var remainingText))
        {
            builder.OpenElement(sequence, "span");
            builder.AddAttribute(sequence + 1, "class", "fx-type-search-match");
            builder.AddContent(sequence + 2, matchedText);
            builder.CloseElement();
            builder.AddContent(sequence + 3, remainingText);
            return;
        }

        if (!ShouldShowAlwaysEditButton(col, item))
        {
            builder.AddContent(sequence, text);
            return;
        }

        var buttonItem = item;
        var buttonCol = col;
        builder.OpenElement(sequence, "button");
        builder.AddAttribute(sequence + 1, "type", "button");
        builder.AddAttribute(sequence + 2, "class", "fx-cell-action-btn");
        builder.AddAttribute(sequence + 3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => HandleEditButtonClick(buttonItem, buttonCol)));
        builder.AddEventStopPropagationAttribute(sequence + 4, "onclick", true);
        builder.AddAttribute(sequence + 5, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, _ => { }));
        builder.AddEventStopPropagationAttribute(sequence + 6, "onmousedown", true);
        builder.AddEventPreventDefaultAttribute(sequence + 7, "onmousedown", true);
        builder.OpenElement(sequence + 8, "span");
        builder.AddAttribute(sequence + 9, "class", "fx-cell-action-text");
        builder.AddContent(sequence + 10, text);
        builder.CloseElement();
        builder.OpenElement(sequence + 11, "span");
        builder.AddAttribute(sequence + 12, "class", "fx-cell-action-ellipsis");
        builder.AddContent(sequence + 13, "...");
        builder.CloseElement();
        builder.CloseElement();
    }

    private bool TryGetTypeSearchHighlight(
        string text,
        TValue item,
        GridColumn col,
        out string matchedText,
        out string remainingText)
    {
        matchedText = "";
        remainingText = text;

        if (!EnableTypeSearch
            || !_hasTypeSearchMatch
            || _typeSearchBuffer.Length == 0
            || string.IsNullOrWhiteSpace(col.Field)
            || string.IsNullOrWhiteSpace(_typeSearchMatchField)
            || !string.Equals(col.Field, _typeSearchMatchField, StringComparison.OrdinalIgnoreCase)
            || _typeSearchMatchItem == null
            || !EqualityComparer<TValue>.Default.Equals(item, _typeSearchMatchItem))
        {
            return false;
        }

        if (!text.StartsWith(_typeSearchBuffer, StringComparison.CurrentCultureIgnoreCase))
            return false;

        var matchLength = Math.Min(_typeSearchBuffer.Length, text.Length);
        matchedText = text[..matchLength];
        remainingText = text[matchLength..];
        return true;
    }

    private bool TryGetCheckboxDisplayValue(TValue item, string field, out bool value)
    {
        var raw = GetPropertyValue(item, field);
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case byte b:
                value = b != 0;
                return true;
            case short s:
                value = s != 0;
                return true;
            case int i:
                value = i != 0;
                return true;
            case long l:
                value = l != 0;
                return true;
            case decimal d:
                value = d != 0;
                return true;
            case double d:
                value = Math.Abs(d) > double.Epsilon;
                return true;
            case float f:
                value = Math.Abs(f) > float.Epsilon;
                return true;
            case string s when bool.TryParse(s, out var parsedBool):
                value = parsedBool;
                return true;
            case string s when int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInt):
                value = parsedInt != 0;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private IEnumerable<string> GetEditOptions(GridColumn col)
    {
        if (col.EditOptions == null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in col.EditOptions)
        {
            var value = option ?? string.Empty;
            if (seen.Add(value))
                yield return value;
        }
    }

    private void RenderBatchEditor(RenderTreeBuilder builder, int sequence, TValue item, int rowIndex, GridColumn col)
    {
        var editItem = item;
        var editField = col.Field;
        var options = col.EditOptions?.ToList();
        if (options is { Count: > 0 })
        {
            _pendingBatchEditFocus = false;

            builder.OpenComponent<DropDownListControl<string, string>>(sequence);
            builder.SetKey((editItem, editField, rowIndex));
            builder.AddAttribute(sequence + 1, "DataSource", GetEditOptions(col).ToList());
            builder.AddAttribute(sequence + 2, "Value", _batchEditValue ?? string.Empty);
            builder.AddAttribute(sequence + 3, "ValueChanged", EventCallback.Factory.Create<string>(this, async value =>
            {
                UpdateBatchEditValue(editItem, editField, value ?? string.Empty);
                await CommitBatchEdit(editItem, editField);
            }));
            builder.AddAttribute(sequence + 4, "Width", "100%");
            builder.AddAttribute(sequence + 5, "CssClass", "fx-batch-dropdown");
            builder.AddAttribute(sequence + 6, "OpenOnRender", _batchDropdownOpenOnRender);
            builder.AddAttribute(sequence + 7, "OpenOnArrowClickOnly", true);
            builder.AddAttribute(sequence + 8, "Closed", EventCallback.Factory.Create(this, () => CommitBatchEdit(editItem, editField)));
            builder.CloseComponent();
        }
        else
        {
            var inputType = GetEditorInputType(col);
            builder.OpenComponent<TextBoxControl>(sequence);
            builder.AddAttribute(sequence + 1, "InputType", inputType);
            builder.AddAttribute(sequence + 2, "CssClass", "fx-batch-input");
            builder.AddAttribute(sequence + 3, "Value", _batchEditValue);
            builder.AddAttribute(sequence + 4, "style", GetEditorInputStyle(col));
            if (col.Type == ColumnType.Number && !col.ShowNumericSpinner)
                builder.AddAttribute(sequence + 5, "inputmode", "decimal");
            builder.AddAttribute(sequence + 13, "UpdateOnInput", true);
            builder.AddAttribute(sequence + 14, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => UpdateBatchEditValue(editItem, editField, value ?? string.Empty)));
            builder.AddAttribute(sequence + 7, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, e => HandleBatchEditKeyDown(editItem, editField, e)));
            builder.AddAttribute(sequence + 8, "onblur", EventCallback.Factory.Create(this, () => CommitBatchEdit(editItem, editField)));
            builder.AddAttribute(sequence + 12, "ElementReferenceCaptured", (Action<ElementReference>)(er => _batchEditInputRef = er));
            builder.CloseComponent();
        }

        if (col.ShowEditButton && !col.AlwaysShowEditButton)
        {
            var ebItem = item;
            var ebCol = col;
            builder.OpenElement(sequence + 40, "button");
            builder.AddAttribute(sequence + 41, "type", "button");
            builder.AddAttribute(sequence + 42, "class", "fx-cell-edit-btn");
            builder.AddAttribute(sequence + 43, "tabindex", "-1");
            builder.AddAttribute(sequence + 44, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, _ => { }));
            builder.AddEventPreventDefaultAttribute(sequence + 45, "onmousedown", true);
            builder.AddEventStopPropagationAttribute(sequence + 46, "onmousedown", true);
            builder.AddAttribute(sequence + 47, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => HandleEditButtonClick(ebItem, ebCol)));
            builder.AddEventStopPropagationAttribute(sequence + 48, "onclick", true);
            builder.AddContent(sequence + 49, "...");
            builder.CloseElement();
        }
    }

    private async Task CommitBatchEdit()
    {
        if (_batchEditItem == null || string.IsNullOrEmpty(_batchEditField)) return;

        var primary = _batchEditItem;
        var field = _batchEditField;
        var newValue = _batchEditValue ?? "";

        var live = new HashSet<TValue>();
        if (_batchEditDirty && DataSource != null)
        {
            foreach (var d in DataSource)
                if (_selectedItems.Contains(d))
                    live.Add(d);
        }

        List<TValue>? cellMassEditTargets = null;
        if (_batchEditDirty && SingleCellColumnMassEditEnabled)
        {
            var batchCol = VisibleColumns.FirstOrDefault(c =>
                string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
            if (batchCol != null)
                cellMassEditTargets = ResolveSingleCellColumnMassEditTargets(primary, batchCol);
        }

        List<TValue> targets;
        if (cellMassEditTargets is { Count: > 1 })
        {
            targets = cellMassEditTargets;
        }
        else if (_batchEditDirty && live.Count > 1 && live.Contains(primary))
        {
            targets = new List<TValue> { primary };
            foreach (var sel in live)
                if (!EqualityComparer<TValue>.Default.Equals(sel, primary))
                    targets.Add(sel);
        }
        else
        {
            targets = new List<TValue> { primary };
        }

        if (targets.Count > 1)
        {
            _selectedItems.Clear();
            ResetRowSelectionTypeAheadTarget();
        }

        if (cellMassEditTargets is { Count: > 1 } && EventsRef?.OnTypeAheadCommit.HasDelegate == true)
        {
            await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
            {
                SelectedItems = targets,
                ColumnName = field,
                Value = newValue
            });
        }
        else
        {
            var committedAnyCell = false;
            foreach (var item in targets)
            {
                var oldValueObj = GetPropertyValue(item, field);
                var oldValueStr = oldValueObj?.ToString() ?? "";
                var changed = !string.Equals(oldValueStr, newValue, StringComparison.Ordinal);
                var shouldRaiseCellSave = changed || _batchEditDirty;

                if (changed)
                    SetPropertyValue(item, field, newValue);
                if (shouldRaiseCellSave)
                    committedAnyCell = true;

                if (shouldRaiseCellSave && EventsRef?.OnCellSave.HasDelegate == true)
                {
                    await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
                    {
                        Data = item,
                        ColumnName = field,
                        Value = newValue
                    });
                }
            }

            if (committedAnyCell)
                await EnsureTrailingNewRowIfNeededAsync();
        }

        _lastCommittedBatchEditItem = primary;
        _lastCommittedBatchEditRowIndex = _batchEditRowIndex;
        _lastCommittedBatchEditField = field;

        _batchEditItem = default;
        _batchEditRowIndex = -1;
        _batchEditField = null;
        _batchEditValue = null;
        _batchEditDirty = false;
        _batchEditReplaceOnFirstInput = false;
        _batchDropdownOpenOnRender = false;
        _pendingBatchEditFocus = false;
        _pendingBatchEditSelectAll = false;
        _pendingBatchEditScrollIntoView = false;
    }

    private bool IsActiveBatchEditSource(TValue item, string? field)
    {
        return _batchEditItem != null
            && !string.IsNullOrWhiteSpace(_batchEditField)
            && !string.IsNullOrWhiteSpace(field)
            && EqualityComparer<TValue>.Default.Equals(_batchEditItem, item)
            && string.Equals(_batchEditField, field, StringComparison.OrdinalIgnoreCase);
    }

    private Task CommitBatchEdit(TValue item, string? field)
    {
        return IsActiveBatchEditSource(item, field)
            ? CommitBatchEdit()
            : Task.CompletedTask;
    }

    private void UpdateBatchEditValue(TValue item, string? field, ChangeEventArgs e)
    {
        UpdateBatchEditValue(item, field, e.Value?.ToString() ?? string.Empty);
    }

    private void UpdateBatchEditValue(TValue item, string? field, string incomingValue)
    {
        if (!IsActiveBatchEditSource(item, field))
            return;

        if (_batchEditReplaceOnFirstInput)
        {
            incomingValue = ResolveFirstInputReplacement(_batchEditValue ?? "", incomingValue);
            _batchEditReplaceOnFirstInput = false;
        }

        _batchEditValue = incomingValue;
        _batchEditDirty = true;
    }

    private static string ResolveFirstInputReplacement(string previousValue, string incomingValue)
    {
        if (string.IsNullOrEmpty(previousValue) || string.Equals(previousValue, incomingValue, StringComparison.Ordinal))
            return incomingValue;

        var prefixLength = 0;
        while (prefixLength < previousValue.Length
            && prefixLength < incomingValue.Length
            && previousValue[prefixLength] == incomingValue[prefixLength])
        {
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < previousValue.Length - prefixLength
            && suffixLength < incomingValue.Length - prefixLength
            && previousValue[previousValue.Length - 1 - suffixLength] == incomingValue[incomingValue.Length - 1 - suffixLength])
        {
            suffixLength++;
        }

        var insertedLength = incomingValue.Length - prefixLength - suffixLength;
        if (insertedLength > 0)
            return incomingValue.Substring(prefixLength, insertedLength);

        return incomingValue.Length < previousValue.Length ? string.Empty : incomingValue;
    }

    private async Task HandleBatchEditKeyDown(TValue sourceItem, string? sourceField, KeyboardEventArgs e)
    {
        if (!IsActiveBatchEditSource(sourceItem, sourceField))
            return;

        if (await TryCommitBatchEditAndExtendSelectionWithShiftArrowAsync(e))
            return;

        if (_batchEditItem != null
            && !string.IsNullOrWhiteSpace(_batchEditField)
            && IsNativeEditorCaretNavigationKey(e))
        {
            var shouldLeaveEditor = await ShouldNavigateOutOfBatchEditorOnHorizontalArrowAsync(e);
            if (!shouldLeaveEditor)
                return;
        }

        var isHorizontalNavigation = TryGetHorizontalKeyboardNavigation(e, out var backwards);
        var isVerticalNavigation = TryGetVerticalKeyboardNavigation(e, out var rowDelta);
        var isScrollNavigation = TryGetScrollKeyboardNavigation(e, out var scrollKey);

        if (isHorizontalNavigation || isVerticalNavigation || isScrollNavigation)
        {
            var hasLiveEdit = _batchEditItem != null && !string.IsNullOrWhiteSpace(_batchEditField);
            var item = hasLiveEdit ? _batchEditItem : default;
            var rowIndex = hasLiveEdit ? _batchEditRowIndex : -1;
            var colIndex = hasLiveEdit ? ResolveVisibleColumnIndex(_batchEditField) : -1;

            if (hasLiveEdit)
            {
                await CommitBatchEdit();
            }
            else if (!TryResolveActiveCellNavigationSource(ref item, ref rowIndex, ref colIndex)
                && !TryResolveLastCommittedNavigationSource(ref item, ref rowIndex, ref colIndex))
            {
                if (!isScrollNavigation
                    || !TryResolveKeyboardNavigationSource(scrollKey is GridScrollNavigationKey.PageUp or GridScrollNavigationKey.Home, out var selectedItem, out rowIndex, out colIndex))
                {
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                item = selectedItem;
            }

            if (item != null && colIndex >= 0)
            {
                RememberKeyboardNavigationSource(item, rowIndex, colIndex);
                if (isScrollNavigation)
                    await NavigateByScrollKeyFromActiveCellAsync(scrollKey);
                else if (isVerticalNavigation)
                    await NavigateToVerticalEditTargetAsync(item, rowIndex, colIndex, rowDelta);
                else
                    await NavigateToAdjacentEditTargetAsync(item, rowIndex, colIndex, backwards);

                await FocusGridHostAsync();
            }
            else
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        else if (e.Key == "Enter" || e.Key == "NumpadEnter")
        {
            var item = _batchEditItem;
            var rowIndex = _batchEditRowIndex;
            var colIndex = ResolveVisibleColumnIndex(_batchEditField);
            await CommitBatchEdit();
            if (item != null && colIndex >= 0)
            {
                SetActiveCell(rowIndex, colIndex);
                RememberKeyboardNavigationSource(item, rowIndex, colIndex);
                _lastSelectedCell = (rowIndex, colIndex);
                _lastSelectedItem = item;
                _lastSelectedRowIndex = rowIndex;
                _pendingActiveCellScrollIntoView = true;
                await FocusGridHostAsync();
            }
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Key == "Escape")
        {
            _batchEditItem = default;
            _batchEditRowIndex = -1;
            _batchEditField = null;
            _batchEditValue = null;
            _batchEditReplaceOnFirstInput = false;
            _batchDropdownOpenOnRender = false;
            _pendingBatchEditFocus = false;
            _pendingBatchEditSelectAll = false;
            _pendingBatchEditClientX = null;
            _pendingBatchEditScrollIntoView = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<bool> TryCommitBatchEditAndExtendSelectionWithShiftArrowAsync(KeyboardEventArgs e)
    {
        if (!TryGetShiftArrowSelection(e, out _, out _))
            return false;

        var item = _batchEditItem;
        var rowIndex = _batchEditRowIndex;
        var colIndex = ResolveVisibleColumnIndex(_batchEditField);
        if (item == null || rowIndex < 0 || colIndex < 0)
            return false;

        await CommitBatchEdit();

        SetActiveCell(rowIndex, colIndex);
        RememberKeyboardNavigationSource(item, rowIndex, colIndex);
        _lastSelectedCell = (rowIndex, colIndex);
        _lastSelectedItem = item;
        _lastSelectedRowIndex = rowIndex;
        _keyboardRangeAnchorItem = item;
        _keyboardRangeAnchorCellIndex = colIndex;

        return await TryExtendSelectionWithShiftArrowAsync(e);
    }

    private bool TryResolveActiveCellNavigationSource(ref TValue? item, ref int rowIndex, ref int cellIndex)
    {
        if (!_activeCell.HasValue)
            return false;

        var activeRowIndex = _activeCell.Value.RowIndex;
        var activeCellIndex = _activeCell.Value.CellIndex;
        var activeItem = GetItemAtResolvedRowIndex(activeRowIndex);
        if (activeItem == null || activeCellIndex < 0)
            return false;

        item = activeItem;
        rowIndex = activeRowIndex;
        cellIndex = activeCellIndex;
        return true;
    }

    private bool TryResolveLastCommittedNavigationSource(ref TValue? item, ref int rowIndex, ref int cellIndex)
    {
        if (_lastCommittedBatchEditItem == null)
            return false;

        var committedCellIndex = ResolveVisibleColumnIndex(_lastCommittedBatchEditField);
        if (committedCellIndex < 0)
            return false;

        item = _lastCommittedBatchEditItem;
        rowIndex = _lastCommittedBatchEditRowIndex;
        cellIndex = committedCellIndex;
        return true;
    }

    private async Task<bool> NavigateFromActiveCellAsync(bool backwards)
    {
        if (_hasLastKeyboardNavigationSource
            && _lastKeyboardNavigationItem != null
            && _lastKeyboardNavigationCellIndex >= 0)
        {
            await NavigateToAdjacentEditTargetAsync(
                _lastKeyboardNavigationItem,
                _lastKeyboardNavigationRowIndex,
                _lastKeyboardNavigationCellIndex,
                backwards);
            return true;
        }

        if (!_activeCell.HasValue)
        {
            if (!TryResolveSelectedRowNavigationSource(backwards, out var selectedItem, out var selectedRowIndex, out var selectedCellIndex))
                return false;

            await NavigateToAdjacentEditTargetAsync(selectedItem, selectedRowIndex, selectedCellIndex, backwards);
            return true;
        }

        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (item == null)
            return false;

        await NavigateToAdjacentEditTargetAsync(
            item,
            _activeCell.Value.RowIndex,
            _activeCell.Value.CellIndex,
            backwards);
        return true;
    }

    private async Task<bool> NavigateVerticallyFromActiveCellAsync(int rowDelta)
    {
        if (_hasLastKeyboardNavigationSource
            && _lastKeyboardNavigationItem != null
            && _lastKeyboardNavigationCellIndex >= 0)
        {
            await NavigateToVerticalEditTargetAsync(
                _lastKeyboardNavigationItem,
                _lastKeyboardNavigationRowIndex,
                _lastKeyboardNavigationCellIndex,
                rowDelta);
            return true;
        }

        if (!_activeCell.HasValue)
        {
            if (!TryResolveSelectedRowNavigationSource(rowDelta < 0, out var selectedItem, out var selectedRowIndex, out var selectedCellIndex))
                return false;

            await NavigateToVerticalEditTargetAsync(selectedItem, selectedRowIndex, selectedCellIndex, rowDelta);
            return true;
        }

        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (item == null)
            return false;

        await NavigateToVerticalEditTargetAsync(
            item,
            _activeCell.Value.RowIndex,
            _activeCell.Value.CellIndex,
            rowDelta);
        return true;
    }

    private async Task<bool> NavigateByScrollKeyFromActiveCellAsync(GridScrollNavigationKey key)
    {
        if (key is GridScrollNavigationKey.Home or GridScrollNavigationKey.End)
            return await NavigateToGridEdgeAsync(key == GridScrollNavigationKey.End);

        var backwards = key == GridScrollNavigationKey.PageUp;
        if (!TryResolveKeyboardNavigationSource(backwards, out var item, out var rowIndex, out var cellIndex))
            return false;

        var visibleRows = GetKeyboardPageRowCount();
        var rowDelta = Math.Max(1, visibleRows - 1);
        if (backwards)
            rowDelta = -rowDelta;

        return await NavigateToRelativeRowEditTargetAsync(item, rowIndex, cellIndex, rowDelta);
    }

    private async Task<bool> NavigateToGridEdgeAsync(bool end)
    {
        var rows = GetKeyboardNavigationRowItems();
        var columns = VisibleColumns.ToList();
        if (rows.Count == 0 || columns.Count == 0)
            return false;

        var targetCellIndex = end
            ? FindLastKeyboardNavigationTargetColumnIndex(columns)
            : columns.FindIndex(IsKeyboardNavigationTargetColumn);
        if (targetCellIndex < 0)
            return false;

        var targetVisibleRowIndex = end ? rows.Count - 1 : 0;
        var targetItem = rows[targetVisibleRowIndex];
        var targetResolvedRowIndex = ResolveRowIndex(targetItem, targetVisibleRowIndex);
        var targetColumn = columns[targetCellIndex];

        return await TryActivateKeyboardEditTargetAsync(
            targetItem,
            targetResolvedRowIndex,
            targetCellIndex,
            targetColumn,
            scrollIntoView: true,
            allowSelectionOnly: true);
    }

    private async Task<bool> NavigateToRelativeRowEditTargetAsync(TValue currentItem, int currentRowIndex, int currentCellIndex, int rowDelta)
    {
        if (rowDelta == 0)
            return false;

        var rows = GetKeyboardNavigationRowItems();
        var columns = VisibleColumns.ToList();
        if (rows.Count == 0 || columns.Count == 0)
            return false;

        var displayRowIndex = ResolveKeyboardDisplayRowIndex(rows, currentItem, currentRowIndex);
        if (displayRowIndex < 0)
            return false;

        var targetVisibleRowIndex = Math.Clamp(displayRowIndex + rowDelta, 0, rows.Count - 1);
        var targetCellIndex = ResolvePreferredKeyboardNavigationCellIndex(columns, currentCellIndex);
        if (targetCellIndex < 0)
            return false;

        var targetItem = rows[targetVisibleRowIndex];
        var targetResolvedRowIndex = ResolveRowIndex(targetItem, targetVisibleRowIndex);
        var targetColumn = columns[targetCellIndex];

        return await TryActivateKeyboardEditTargetAsync(
            targetItem,
            targetResolvedRowIndex,
            targetCellIndex,
            targetColumn,
            scrollIntoView: true,
            allowSelectionOnly: true);
    }

    private bool TryResolveKeyboardNavigationSource(bool backwards, out TValue item, out int rowIndex, out int cellIndex)
    {
        item = default!;
        rowIndex = -1;
        cellIndex = backwards ? VisibleColumns.Count() : -1;

        if (_hasLastKeyboardNavigationSource
            && _lastKeyboardNavigationItem != null
            && _lastKeyboardNavigationCellIndex >= 0)
        {
            item = _lastKeyboardNavigationItem;
            rowIndex = _lastKeyboardNavigationRowIndex;
            cellIndex = _lastKeyboardNavigationCellIndex;
            return true;
        }

        TValue? candidateItem = default;
        var candidateRowIndex = -1;
        var candidateCellIndex = -1;
        if (TryResolveActiveCellNavigationSource(ref candidateItem, ref candidateRowIndex, ref candidateCellIndex)
            || TryResolveLastCommittedNavigationSource(ref candidateItem, ref candidateRowIndex, ref candidateCellIndex))
        {
            if (candidateItem == null)
                return false;

            item = candidateItem;
            rowIndex = candidateRowIndex;
            cellIndex = candidateCellIndex;
            return true;
        }

        return TryResolveSelectedRowNavigationSource(backwards, out item, out rowIndex, out cellIndex);
    }

    private bool TryResolveSelectedRowNavigationSource(bool backwards, out TValue item, out int rowIndex, out int cellIndex)
    {
        item = default!;
        rowIndex = -1;
        cellIndex = backwards ? VisibleColumns.Count() : -1;

        var selected = _lastSelectedItem ?? _selectedItems.LastOrDefault();
        if (selected == null)
            return false;

        rowIndex = ResolveRowIndex(selected, _lastSelectedRowIndex ?? -1);
        if (rowIndex < 0)
            return false;

        item = selected;
        return true;
    }

    private async Task<bool> TryToggleActiveCheckboxAsync()
    {
        if (!_activeCell.HasValue)
            return false;

        var column = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        if (column == null || !CanToggleCheckboxColumn(column))
            return false;

        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (item == null)
            return false;

        await HandleCheckboxToggle(item, column);
        return true;
    }

    private async Task NavigateToAdjacentEditTargetAsync(TValue currentItem, int currentRowIndex, int currentCellIndex, bool backwards)
    {
        var cursorItem = currentItem;
        var cursorRowIndex = currentRowIndex;
        var cursorCellIndex = currentCellIndex;

        while (TryFindAdjacentEditTarget(cursorItem, cursorRowIndex, cursorCellIndex, backwards,
                   out var targetItem, out var targetRowIndex, out var targetCellIndex, out var targetColumn))
        {
            if (await TryActivateKeyboardEditTargetAsync(
                    targetItem,
                    targetRowIndex,
                    targetCellIndex,
                    targetColumn,
                    allowSelectionOnly: true))
                return;

            cursorItem = targetItem;
            cursorRowIndex = targetRowIndex;
            cursorCellIndex = targetCellIndex;
        }

        if (!backwards && await TryAddNewRowOnLastCellExitAsync(currentItem, currentRowIndex, currentCellIndex))
            return;

        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task NavigateToVerticalEditTargetAsync(TValue currentItem, int currentRowIndex, int currentCellIndex, int rowDelta)
    {
        if (rowDelta == 0)
            return;

        var cursorItem = currentItem;
        var cursorRowIndex = currentRowIndex;
        var cursorCellIndex = currentCellIndex;

        while (TryFindVerticalEditTarget(cursorItem, cursorRowIndex, cursorCellIndex, rowDelta,
                   out var targetItem, out var targetRowIndex, out var targetCellIndex, out var targetColumn))
        {
            if (await TryActivateKeyboardEditTargetAsync(
                    targetItem,
                    targetRowIndex,
                    targetCellIndex,
                    targetColumn,
                    allowSelectionOnly: true))
                return;

            cursorItem = targetItem;
            cursorRowIndex = targetRowIndex;
            cursorCellIndex = targetCellIndex;
        }

        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
    }

    private bool TryFindVerticalEditTarget(
        TValue currentItem,
        int currentRowIndex,
        int currentCellIndex,
        int rowDelta,
        out TValue targetItem,
        out int targetRowIndex,
        out int targetCellIndex,
        out GridColumn targetColumn)
    {
        targetItem = default!;
        targetRowIndex = -1;
        targetCellIndex = -1;
        targetColumn = default!;

        var columns = VisibleColumns.ToList();
        var rows = GetKeyboardNavigationRowItems();
        if (columns.Count == 0 || rows.Count == 0)
            return false;

        var displayRowIndex = ResolveKeyboardDisplayRowIndex(rows, currentItem, currentRowIndex);
        if (displayRowIndex < 0)
            return false;

        var preferredCellIndex = ResolvePreferredKeyboardNavigationCellIndex(columns, currentCellIndex);
        if (preferredCellIndex < 0)
            return false;

        var row = displayRowIndex + rowDelta;
        if (row < 0 || row >= rows.Count)
            return false;

        var candidate = columns[preferredCellIndex];
        if (!IsKeyboardNavigationTargetColumn(candidate))
            return false;

        targetItem = rows[row];
        targetRowIndex = ResolveRowIndex(targetItem, row);
        targetCellIndex = preferredCellIndex;
        targetColumn = candidate;
        return true;
    }

    private int ResolveKeyboardDisplayRowIndex(IReadOnlyList<TValue> rows, TValue currentItem, int currentRowIndex)
    {
        var displayRowIndex = rows.ToList().FindIndex(item =>
            EqualityComparer<TValue>.Default.Equals(item, currentItem));
        if (displayRowIndex >= 0)
            return displayRowIndex;

        var resolvedItem = GetItemAtResolvedRowIndex(currentRowIndex);
        return resolvedItem == null
            ? -1
            : rows.ToList().FindIndex(item => EqualityComparer<TValue>.Default.Equals(item, resolvedItem));
    }

    private int ResolvePreferredKeyboardNavigationCellIndex(IReadOnlyList<GridColumn> columns, int currentCellIndex)
    {
        return currentCellIndex >= 0
            && currentCellIndex < columns.Count
            && IsKeyboardNavigationTargetColumn(columns[currentCellIndex])
            ? currentCellIndex
            : columns.ToList().FindIndex(IsKeyboardNavigationTargetColumn);
    }

    private int FindLastKeyboardNavigationTargetColumnIndex(IReadOnlyList<GridColumn> columns)
    {
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            if (IsKeyboardNavigationTargetColumn(columns[i]))
                return i;
        }

        return -1;
    }

    private bool TryFindAdjacentEditTarget(
        TValue currentItem,
        int currentRowIndex,
        int currentCellIndex,
        bool backwards,
        out TValue targetItem,
        out int targetRowIndex,
        out int targetCellIndex,
        out GridColumn targetColumn)
    {
        targetItem = default!;
        targetRowIndex = -1;
        targetCellIndex = -1;
        targetColumn = default!;

        var columns = VisibleColumns.ToList();
        var rows = GetKeyboardNavigationRowItems();
        if (columns.Count == 0 || rows.Count == 0)
            return false;

        var displayRowIndex = rows.FindIndex(item =>
            EqualityComparer<TValue>.Default.Equals(item, currentItem));
        if (displayRowIndex < 0)
        {
            var resolvedItem = GetItemAtResolvedRowIndex(currentRowIndex);
            displayRowIndex = rows.FindIndex(item =>
                EqualityComparer<TValue>.Default.Equals(item, resolvedItem));
        }
        if (displayRowIndex < 0)
            return false;

        var row = displayRowIndex;
        var col = Math.Clamp(currentCellIndex, -1, columns.Count);
        var guard = rows.Count * columns.Count;

        for (var i = 0; i < guard; i++)
        {
            col += backwards ? -1 : 1;

            if (col < 0)
            {
                row--;
                col = columns.Count - 1;
            }
            else if (col >= columns.Count)
            {
                row++;
                col = 0;
            }

            if (row < 0 || row >= rows.Count)
                return false;

            var candidate = columns[col];
            if (!IsKeyboardNavigationTargetColumn(candidate))
                continue;

            targetItem = rows[row];
            targetRowIndex = ResolveRowIndex(targetItem, row);
            targetCellIndex = col;
            targetColumn = candidate;
            return true;
        }

        return false;
    }

    private async Task<bool> TryAddNewRowOnLastCellExitAsync(TValue currentItem, int currentRowIndex, int currentCellIndex)
    {
        return await TryAppendTrailingNewRowFromLastCellAsync(currentItem, currentRowIndex, currentCellIndex, beginEdit: true);
    }

    private async Task<bool> TryAppendTrailingNewRowFromLastCellAsync(TValue currentItem, int currentRowIndex, int currentCellIndex, bool beginEdit)
    {
        if (!AddNewRowOnLastCellExit
            || EditSettingsRef?.AllowAdding != true
            || DataSource is not IList<TValue>)
            return false;

        if (!IsLastKeyboardNavigationCell(currentItem, currentRowIndex, currentCellIndex))
            return false;

        if (CanAddNewRowOnLastCellExit?.Invoke(currentItem) == false)
            return false;

        var row = CreateNewItem();
        if (row is null)
            return false;

        ClearTrailingNewRowMarker();
        await AppendRowAsync(row, beginEdit: beginEdit);
        TrackTrailingNewRow(row);
        SyncDataSourceChangeTrackers();
        return true;
    }

    private bool IsLastKeyboardNavigationCell(TValue currentItem, int currentRowIndex, int currentCellIndex)
    {
        var columns = VisibleColumns.ToList();
        var rows = GetKeyboardNavigationRowItems();
        if (columns.Count == 0 || rows.Count == 0)
            return false;

        var displayRowIndex = rows.FindIndex(item =>
            EqualityComparer<TValue>.Default.Equals(item, currentItem));
        if (displayRowIndex < 0)
        {
            var resolvedItem = GetItemAtResolvedRowIndex(currentRowIndex);
            displayRowIndex = rows.FindIndex(item =>
                EqualityComparer<TValue>.Default.Equals(item, resolvedItem));
        }

        if (displayRowIndex != rows.Count - 1)
            return false;

        var cellIndex = Math.Clamp(currentCellIndex, 0, columns.Count - 1);
        if (!IsKeyboardNavigationTargetColumn(columns[cellIndex]))
            return false;

        for (var i = cellIndex + 1; i < columns.Count; i++)
        {
            if (IsKeyboardNavigationTargetColumn(columns[i]))
                return false;
        }

        return true;
    }

    private async Task<bool> TryActivateKeyboardEditTargetAsync(
        TValue item,
        int rowIndex,
        int cellIndex,
        GridColumn column,
        bool scrollIntoView = false,
        bool allowSelectionOnly = false)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
            return false;

        if (_batchEditItem != null)
            await CommitBatchEdit();

        if (IsPagingActive && _pageState.PageSize > 0)
        {
            var displayRowIndex = SortedData.ToList().FindIndex(candidate =>
                EqualityComparer<TValue>.Default.Equals(candidate, item));
            if (displayRowIndex >= 0)
            {
                var targetPage = (displayRowIndex / _pageState.PageSize) + 1;
                if (targetPage != _pageState.CurrentPage)
                    await GoToPage(targetPage);
            }
        }

        await SelectProgrammaticCellAsync(item, column.Field);
        RememberKeyboardNavigationSource(item, rowIndex, cellIndex);
        await TryAppendTrailingNewRowFromLastCellAsync(item, rowIndex, cellIndex, beginEdit: false);
        _pendingActiveCellScrollIntoView = scrollIntoView || allowSelectionOnly;

        if (allowSelectionOnly)
        {
            await FocusGridHostAsync();
            await InvokeAsync(StateHasChanged);
            return true;
        }

        if (!IsKeyboardEditTargetColumn(column))
        {
            return false;
        }

        if (CanStartKeyboardBatchEdit(column))
        {
            var started = await TryStartBatchEdit(item, rowIndex, column);
            if (!started)
            {
                if (!allowSelectionOnly)
                    return false;

                await FocusGridHostAsync();
                await InvokeAsync(StateHasChanged);
                return true;
            }

            _pendingBatchEditScrollIntoView = scrollIntoView;
            SyncDataSourceChangeTrackers();
            await InvokeAsync(StateHasChanged);
            return true;
        }

        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private bool IsKeyboardNavigationTargetColumn(GridColumn column)
    {
        return column.Visible
            && !column.IsPrimaryKey
            && !string.IsNullOrWhiteSpace(column.Field);
    }

    private bool IsKeyboardEditTargetColumn(GridColumn column)
    {
        return column.Visible
            && !column.IsPrimaryKey
            && !string.IsNullOrWhiteSpace(column.Field)
            && (CanStartKeyboardBatchEdit(column)
                || CanToggleCheckboxColumn(column)
                || column.ShowEditButton
                || column.AllowCellDragSelection
                || column.EffectiveTemplate != null
                || column.Commands is { Count: > 0 });
    }

    private bool CanStartKeyboardBatchEdit(GridColumn column)
    {
        return EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && column.AllowEditing
            && !column.IsPrimaryKey
            && column.Type != ColumnType.CheckBox
            && column.EffectiveTemplate == null
            && column.Commands == null
            && !string.IsNullOrWhiteSpace(column.Field);
    }

    private bool CanShowEditableCellCue(GridColumn column)
    {
        return EditSettingsRef?.AllowEditing == true
            && column.AllowEditing
            && !column.IsPrimaryKey
            && !string.IsNullOrWhiteSpace(column.Field);
    }

    private static bool TryGetHorizontalKeyboardNavigation(KeyboardEventArgs e, out bool backwards)
    {
        backwards = false;
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;

        if (e.Key == "Tab")
        {
            backwards = e.ShiftKey;
            return true;
        }

        if (e.ShiftKey)
            return false;

        if (e.Key == "ArrowLeft")
        {
            backwards = true;
            return true;
        }

        if (e.Key == "ArrowRight")
        {
            backwards = false;
            return true;
        }

        return false;
    }

    private static bool IsNativeEditorCaretNavigationKey(KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey || e.ShiftKey)
            return false;

        return e.Key is "ArrowLeft" or "ArrowRight";
    }

    private async Task<bool> ShouldNavigateOutOfBatchEditorOnHorizontalArrowAsync(KeyboardEventArgs e)
    {
        if (e.Key is not ("ArrowLeft" or "ArrowRight"))
            return false;

        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            return await _gridJsModule.InvokeAsync<bool>(
                "isInputCaretAtHorizontalBoundary",
                _batchEditInputRef,
                e.Key);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetVerticalKeyboardNavigation(KeyboardEventArgs e, out int rowDelta)
    {
        rowDelta = 0;
        if (e.AltKey || e.CtrlKey || e.MetaKey || e.ShiftKey)
            return false;

        if (e.Key == "ArrowUp")
        {
            rowDelta = -1;
            return true;
        }

        if (e.Key == "ArrowDown")
        {
            rowDelta = 1;
            return true;
        }

        return false;
    }

    private static bool TryGetScrollKeyboardNavigation(KeyboardEventArgs e, out GridScrollNavigationKey key)
    {
        key = GridScrollNavigationKey.PageDown;
        if (e.AltKey || e.ShiftKey)
            return false;

        if (!e.CtrlKey && !e.MetaKey)
        {
            if (e.Key == "PageUp")
            {
                key = GridScrollNavigationKey.PageUp;
                return true;
            }

            if (e.Key == "PageDown")
            {
                key = GridScrollNavigationKey.PageDown;
                return true;
            }

            return false;
        }

        if (e.Key == "Home")
        {
            key = GridScrollNavigationKey.Home;
            return true;
        }

        if (e.Key == "End")
        {
            key = GridScrollNavigationKey.End;
            return true;
        }

        return false;
    }

    private static bool TryGetShiftArrowSelection(KeyboardEventArgs e, out int rowDelta, out int cellDelta)
    {
        rowDelta = 0;
        cellDelta = 0;
        if (!e.ShiftKey || e.AltKey || e.CtrlKey || e.MetaKey)
            return false;

        switch (e.Key)
        {
            case "ArrowUp":
                rowDelta = -1;
                return true;
            case "ArrowDown":
                rowDelta = 1;
                return true;
            case "ArrowLeft":
                cellDelta = -1;
                return true;
            case "ArrowRight":
                cellDelta = 1;
                return true;
            default:
                return false;
        }
    }

    private int ResolveVisibleColumnIndex(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return -1;

        var columns = VisibleColumns.ToList();
        return columns.FindIndex(column =>
            string.Equals(column.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsBatchEditing(TValue item, string field)
    {
        if (_batchEditItem == null || _batchEditField != field) return false;
        return EqualityComparer<TValue>.Default.Equals(_batchEditItem, item);
    }

    private string? ResolveTypeAheadTargetField()
    {
        return ResolveTypeAheadTargetColumn()?.Field;
    }

    private GridColumn? ResolveTypeAheadTargetColumn()
    {
        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell && _activeCell.HasValue)
        {
            var activeCol = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
            if (CanReceiveTypeAhead(activeCol))
                return activeCol;
        }

        if (_rowSelectionTypeAheadTargetCaptured)
        {
            if (string.IsNullOrWhiteSpace(_rowSelectionTypeAheadTargetField))
                return null;

            return FindTypeAheadColumnByField(_rowSelectionTypeAheadTargetField);
        }

        var fallbackField = !string.IsNullOrWhiteSpace(TypeAheadTargetField)
            ? TypeAheadTargetField
            : TypeAheadFallbackField;
        if (!string.IsNullOrWhiteSpace(fallbackField))
        {
            var configured = VisibleColumns.FirstOrDefault(c =>
                string.Equals(c.Field, fallbackField, StringComparison.OrdinalIgnoreCase));
            if (CanReceiveTypeAhead(configured))
                return configured;
        }

        if (_activeCell.HasValue)
        {
            var col = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
            if (CanReceiveTypeAhead(col))
                return col;
        }

        return null;
    }

    private GridColumn? FindTypeAheadColumnByField(string field)
    {
        var col = VisibleColumns.FirstOrDefault(c =>
            string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        return CanReceiveTypeAhead(col) ? col : null;
    }

    private bool IsTypeAheadPreviewCell(TValue item, GridColumn col)
    {
        if (_typeAheadBuffer.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(col.Field)) return false;

        var target = ResolveTypeAheadTargetField();
        if (string.IsNullOrWhiteSpace(target)
            || !string.Equals(col.Field, target, StringComparison.OrdinalIgnoreCase))
            return false;

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell)
            return IsSingleCellColumnMassEditPreviewCell(item, col)
                || IsActiveCell(item, col.Field);

        return _selectedItems.Contains(item);
    }

    private bool IsActiveCell(TValue item, string field)
    {
        if (!_activeCell.HasValue) return false;
        var col = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        if (col == null || !string.Equals(col.Field, field, StringComparison.OrdinalIgnoreCase))
            return false;

        var activeItem = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        return activeItem != null && EqualityComparer<TValue>.Default.Equals(activeItem, item);
    }

    private bool IsSingleCellColumnMassEditPreviewCell(TValue item, GridColumn col)
    {
        if (!SingleCellColumnMassEditEnabled || !_activeCell.HasValue)
            return false;

        var activeColumnIndex = _activeCell.Value.CellIndex;
        var activeCol = VisibleColumns.ElementAtOrDefault(activeColumnIndex);
        if (activeCol == null
            || string.IsNullOrWhiteSpace(activeCol.Field)
            || !string.Equals(activeCol.Field, col.Field, StringComparison.OrdinalIgnoreCase))
            return false;

        var rowIndex = ResolveRowIndex(item, -1);
        return rowIndex >= 0 && _selectedCells.Contains((rowIndex, activeColumnIndex));
    }

    private async Task OnToolbarClick(string item)
    {
        switch (item.ToLower())
        {
            case "add":
                StartAdd();
                break;
            case "edit":
                if (_selectedItems.Count == 1)
                {
                    var sel = _selectedItems.First();
                    var idx = PagedData.ToList().IndexOf(sel);
                    await StartEdit(sel, idx);
                }
                break;
            case "delete":
                if (_selectedItems.Count > 0)
                {
                    foreach (var sel in _selectedItems.ToList())
                        await DeleteRow(sel, 0);
                }
                break;
            case "csv":
                await ExportToCsvAsync();
                break;
            case "excel":
                await ExportToXlsxAsync();
                break;
            case "pdf":
                await ExportToPdfAsync();
                break;
        }

        if (OnToolbarItemClick.HasDelegate)
            await OnToolbarItemClick.InvokeAsync(item);
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && _isEditing)
        {
            CancelEdit();
            return;
        }

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
            && _batchEditItem == null
            && _typeAheadBuffer.Length > 0
            && e.Key == "Escape")
        {
            _typeAheadBuffer = "";
            await NotifyTypeAheadChangedAsync();
            return;
        }

        if ((e.Key is " " or "Spacebar" or "Enter" or "NumpadEnter")
            && await TryToggleActiveCheckboxAsync())
        {
            return;
        }

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
            && _batchEditItem == null
            && _typeAheadBuffer.Length > 0
            && (e.Key == "Enter" || e.Key == "NumpadEnter"))
        {
            await CommitPendingSingleCellTypeAheadAsync();
            return;
        }

        if ((e.Key == "Enter" || e.Key == "NumpadEnter") && await TryCommitSelectedRowOnEnterAsync(e))
            return;

        if (_batchEditItem == null && !_isEditing && await HandleTypeSearchKeyAsync(e))
            return;

        if (EnableTypeSearch && _typeSearchBuffer.Length > 0)
            ClearTypeSearchBuffer();

        if (_batchEditItem == null
            && _typeAheadBuffer.Length > 0
            && _selectedItems.Count > 1
            && IsRowSelectionTypeAheadCommitKey(e, out var commitAndStop))
        {
            var committed = await CommitPendingRowSelectionTypeAheadAsync();
            if (committed && commitAndStop)
                return;
        }

        if (_batchEditItem == null && await TryExtendSelectionWithShiftArrowAsync(e))
            return;

        if ((!e.ShiftKey || e.Key == "Tab") && !ShouldPreserveKeyboardRangeAnchorForRowTypeAhead(e))
            ClearKeyboardRangeSelectionAnchor();

        if (TryGetHorizontalKeyboardNavigation(e, out var backwards))
        {
            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
                && _batchEditItem == null
                && _typeAheadBuffer.Length > 0)
            {
                await CommitPendingSingleCellTypeAheadAsync();
            }

            if (await NavigateFromActiveCellAsync(backwards))
                return;
        }

        if (TryGetVerticalKeyboardNavigation(e, out var rowDelta))
        {
            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
                && _batchEditItem == null
                && _typeAheadBuffer.Length > 0)
            {
                await CommitPendingSingleCellTypeAheadAsync();
            }

            if (await NavigateVerticallyFromActiveCellAsync(rowDelta))
                return;
        }

        if (TryGetScrollKeyboardNavigation(e, out var scrollKey))
        {
            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
                && _batchEditItem == null
                && _typeAheadBuffer.Length > 0)
            {
                await CommitPendingSingleCellTypeAheadAsync();
            }

            if (await NavigateByScrollKeyFromActiveCellAsync(scrollKey))
                return;
        }

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
            && _batchEditItem == null
            && await HandleSingleCellTypeAheadKeyAsync(e))
            return;

        if (_selectedItems.Count > 1 && _batchEditItem == null)
        {
            var targetCol = ResolveTypeAheadTargetColumn();
            if (e.Key.Length == 1)
            {
                if (targetCol == null || !IsEditableTypeAheadKey(e, targetCol))
                    return;

                if (targetCol.Type == ColumnType.Number
                    && e.Key == "."
                    && _typeAheadBuffer.Contains('.'))
                    return;

                _typeAheadBuffer += e.Key;
                await NotifyTypeAheadChangedAsync();
                return;
            }

            if (e.Key == "Backspace" && _typeAheadBuffer.Length > 0)
            {
                _typeAheadBuffer = _typeAheadBuffer[..^1];
                await NotifyTypeAheadChangedAsync();
                return;
            }

            if (e.Key == "Escape" && _typeAheadBuffer.Length > 0)
            {
                _typeAheadBuffer = "";
                await NotifyTypeAheadChangedAsync();
                return;
            }

            if (e.Key == "Enter" && _typeAheadBuffer.Length > 0)
            {
                if (targetCol == null)
                {
                    _typeAheadBuffer = "";
                    await NotifyTypeAheadChangedAsync();
                    return;
                }

                if (EventsRef?.OnTypeAheadCommit.HasDelegate == true)
                {
                    await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
                    {
                        SelectedItems = _selectedItems.ToList(),
                        ColumnName = targetCol.Field,
                        Value = _typeAheadBuffer
                    });
                }
                _typeAheadBuffer = "";
                await NotifyTypeAheadChangedAsync();
                return;
            }
        }

        if (_batchEditItem == null && await TryStartActiveBatchEditFromTypedKeyAsync(e))
            return;
    }

    private async Task<bool> HandleTypeSearchKeyAsync(KeyboardEventArgs e)
    {
        if (!EnableTypeSearch)
            return false;
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;

        if (e.Key == "Escape" && _typeSearchBuffer.Length > 0)
        {
            ClearTypeSearchBuffer();
            await InvokeAsync(StateHasChanged);
            return true;
        }

        if (e.Key == "Backspace" && _typeSearchBuffer.Length > 0)
        {
            _typeSearchBuffer = _typeSearchBuffer[..^1];
            await MoveTypeSearchSelectionAsync();
            return true;
        }

        if (!IsTypeSearchCharacterKey(e))
            return false;

        var now = DateTime.UtcNow;
        var delay = Math.Max(1, TypeSearchDelaySeconds);
        if (_typeSearchBuffer.Length > 0
            && (now - _typeSearchLastInputUtc).TotalSeconds > delay)
        {
            ClearTypeSearchBuffer();
        }

        _typeSearchLastInputUtc = now;
        _typeSearchBuffer += e.Key;
        await MoveTypeSearchSelectionAsync();
        return true;
    }

    private static bool IsTypeSearchCharacterKey(KeyboardEventArgs e)
    {
        return e.Key.Length == 1 && !char.IsControl(e.Key[0]);
    }

    private async Task MoveTypeSearchSelectionAsync()
    {
        _typeSearchLastInputUtc = DateTime.UtcNow;

        if (_typeSearchBuffer.Length == 0)
        {
            ClearTypeSearchBuffer();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var columns = VisibleColumns.ToList();
        if (columns.Count == 0)
            return;

        var targetColumnIndex = ResolveTypeSearchColumnIndex(columns);
        if (targetColumnIndex < 0 || targetColumnIndex >= columns.Count)
            return;

        var targetColumn = columns[targetColumnIndex];
        if (string.IsNullOrWhiteSpace(targetColumn.Field))
            return;

        var rows = SortedData.ToList();
        _pageState.TotalRecords = rows.Count;
        EnsureCurrentPageInRange();

        var matchDisplayIndex = rows.FindIndex(item =>
            GetCellDisplayValue(item, targetColumn)
                .StartsWith(_typeSearchBuffer, StringComparison.CurrentCultureIgnoreCase));

        if (matchDisplayIndex < 0)
        {
            _hasTypeSearchMatch = false;
            _typeSearchMatchItem = default;
            _typeSearchMatchField = null;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var matchItem = rows[matchDisplayIndex];
        if (IsPagingActive && _pageState.PageSize > 0)
        {
            var targetPage = (matchDisplayIndex / _pageState.PageSize) + 1;
            if (targetPage != _pageState.CurrentPage)
                await GoToPage(targetPage);
        }

        var resolvedRowIndex = ResolveRowIndex(matchItem, matchDisplayIndex);
        _selectedItems.Clear();
        if (AllowSelection && SelectionSettingsRef?.Mode != SelectionMode.Cell)
            _selectedItems.Add(matchItem);
        _selectedCells.Clear();
        if (AllowSelection && SelectionSettingsRef?.Mode == SelectionMode.Cell)
            _selectedCells.Add((resolvedRowIndex, targetColumnIndex));

        SetActiveCell(resolvedRowIndex, targetColumnIndex);
        RememberKeyboardNavigationSource(matchItem, resolvedRowIndex, targetColumnIndex);
        _lastSelectedCell = (resolvedRowIndex, targetColumnIndex);
        _lastSelectedItem = matchItem;
        _lastSelectedRowIndex = resolvedRowIndex;
        _typeSearchMatchItem = matchItem;
        _typeSearchMatchField = targetColumn.Field;
        _hasTypeSearchMatch = true;
        _pendingActiveCellScrollIntoView = true;

        if (EventsRef?.RowSelected.HasDelegate == true)
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue> { Data = matchItem, RowIndex = resolvedRowIndex });

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            await EventsRef.CellSelected.InvokeAsync(new CellSelectEventArgs<TValue>
            {
                Data = matchItem,
                RowIndex = resolvedRowIndex,
                CellIndex = targetColumnIndex,
                CurrentValue = GetPropertyValue(matchItem, targetColumn.Field)
            });
        }

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
    }

    private int ResolveTypeSearchColumnIndex(IReadOnlyList<GridColumn> columns)
    {
        if (_activeCell.HasValue
            && _activeCell.Value.CellIndex >= 0
            && _activeCell.Value.CellIndex < columns.Count
            && !string.IsNullOrWhiteSpace(columns[_activeCell.Value.CellIndex].Field))
        {
            return _activeCell.Value.CellIndex;
        }

        if (_lastSelectedCell.HasValue
            && _lastSelectedCell.Value.CellIndex >= 0
            && _lastSelectedCell.Value.CellIndex < columns.Count
            && !string.IsNullOrWhiteSpace(columns[_lastSelectedCell.Value.CellIndex].Field))
        {
            return _lastSelectedCell.Value.CellIndex;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(columns[i].Field))
                return i;
        }

        return -1;
    }

    private void ClearTypeSearchBuffer()
    {
        _typeSearchBuffer = "";
        _typeSearchLastInputUtc = DateTime.MinValue;
        _typeSearchMatchItem = default;
        _typeSearchMatchField = null;
        _hasTypeSearchMatch = false;
    }

    private bool ShouldPreserveKeyboardRangeAnchorForRowTypeAhead(KeyboardEventArgs e)
    {
        if (_batchEditItem != null || _selectedItems.Count <= 1)
            return false;

        return e.Key.Length == 1
            || e.Key == "Backspace"
            || (e.Key is "Enter" or "NumpadEnter" && _typeAheadBuffer.Length > 0)
            || (_typeAheadBuffer.Length > 0 && IsRowSelectionTypeAheadCommitKey(e, out _));
    }

    private static bool IsRowSelectionTypeAheadCommitKey(KeyboardEventArgs e, out bool stopAfterCommit)
    {
        stopAfterCommit = false;

        if (e.Key is "Enter" or "NumpadEnter")
        {
            stopAfterCommit = true;
            return true;
        }

        if (TryGetHorizontalKeyboardNavigation(e, out _)
            || TryGetVerticalKeyboardNavigation(e, out _)
            || TryGetShiftArrowSelection(e, out _, out _))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> CommitPendingRowSelectionTypeAheadAsync()
    {
        if (_typeAheadBuffer.Length == 0 || _selectedItems.Count <= 1)
            return false;

        var targetCol = ResolveTypeAheadTargetColumn();
        if (targetCol == null)
        {
            ClearTypeAheadBuffer();
            return false;
        }

        var anchor = CaptureTypeAheadRestoreAnchor(targetCol);
        var value = _typeAheadBuffer;
        var selectedItems = _selectedItems.ToList();

        if (EventsRef?.OnTypeAheadCommit.HasDelegate == true)
        {
            await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
            {
                SelectedItems = selectedItems,
                ColumnName = targetCol.Field,
                Value = value
            });
        }

        var selectionChanged = RestoreTypeAheadAnchor(anchor, collapseSelection: true);
        _typeAheadBuffer = "";
        await NotifyTypeAheadChangedAsync();

        await FocusGridHostAsync();
        if (selectionChanged)
            await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private TypeAheadRestoreAnchor CaptureTypeAheadRestoreAnchor(GridColumn targetCol)
    {
        var targetCellIndex = ResolveVisibleColumnIndex(targetCol.Field);
        if (targetCellIndex < 0 && _activeCell.HasValue)
            targetCellIndex = _activeCell.Value.CellIndex;
        if (targetCellIndex < 0)
            targetCellIndex = 0;

        if (_keyboardRangeAnchorItem != null)
        {
            var rowIndex = ResolveRowIndex(_keyboardRangeAnchorItem, -1);
            if (rowIndex >= 0)
                return new TypeAheadRestoreAnchor(_keyboardRangeAnchorItem, rowIndex, targetCellIndex);
        }

        if (_activeCell.HasValue)
        {
            var activeItem = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
            if (activeItem != null)
                return new TypeAheadRestoreAnchor(activeItem, ResolveRowIndex(activeItem, _activeCell.Value.RowIndex), targetCellIndex);
        }

        var selected = _lastSelectedItem != null && _selectedItems.Contains(_lastSelectedItem)
            ? _lastSelectedItem
            : _selectedItems.FirstOrDefault();
        if (selected != null)
            return new TypeAheadRestoreAnchor(selected, ResolveRowIndex(selected, _lastSelectedRowIndex ?? 0), targetCellIndex);

        return default;
    }

    private bool RestoreTypeAheadAnchor(TypeAheadRestoreAnchor anchor, bool collapseSelection = false)
    {
        if (anchor.Item == null || anchor.RowIndex < 0 || anchor.CellIndex < 0)
            return false;

        var selectionChanged = false;

        if (collapseSelection)
        {
            _isDragSelecting = false;
            _dragAnchorRowIndex = null;
            _dragAnchorItem = default;
            ClearCellDragState();

            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
            {
                var target = (anchor.RowIndex, anchor.CellIndex);
                selectionChanged = _selectedCells.Count != 1 || !_selectedCells.Contains(target);
                _selectedCells.Clear();
                _selectedCells.Add(target);
                _selectedItems.Clear();
            }
            else
            {
                selectionChanged = _selectedItems.Count != 1 || !_selectedItems.Contains(anchor.Item);
                _selectedItems.Clear();
                _selectedItems.Add(anchor.Item);
                _selectedCells.Clear();
            }

            ResetRowSelectionTypeAheadTarget();
        }

        SetActiveCell(anchor.RowIndex, anchor.CellIndex);
        RememberKeyboardNavigationSource(anchor.Item, anchor.RowIndex, anchor.CellIndex);
        _lastSelectedCell = (anchor.RowIndex, anchor.CellIndex);
        _lastSelectedItem = anchor.Item;
        _lastSelectedRowIndex = anchor.RowIndex;
        _pendingActiveCellScrollIntoView = true;

        return selectionChanged;
    }

    private readonly record struct TypeAheadRestoreAnchor(TValue? Item, int RowIndex, int CellIndex);

    private async Task<bool> TryStartActiveBatchEditFromTypedKeyAsync(KeyboardEventArgs e)
    {
        if (_isEditing || _batchEditItem != null)
            return false;
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;
        if (!TryGetActiveKeyboardBatchEditCell(out var item, out var rowIndex, out var col))
            return false;
        if (col.EditOptions?.Any() == true)
            return false;
        if (!IsEditableTypeAheadKey(e, col))
            return false;

        ClearKeyboardRangeSelectionAnchor();
        var started = await TryStartBatchEdit(item, rowIndex, col);
        if (!started || !IsActiveBatchEditSource(item, col.Field))
            return false;

        _batchEditValue = e.Key;
        _batchEditDirty = true;
        _batchEditReplaceOnFirstInput = false;
        _pendingBatchEditSelectAll = false;
        _pendingBatchEditClientX = null;
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private bool TryGetActiveKeyboardBatchEditCell(out TValue item, out int rowIndex, out GridColumn col)
    {
        item = default!;
        rowIndex = -1;
        col = default!;

        if (!_activeCell.HasValue)
            return false;

        var candidateCol = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        if (candidateCol == null || !CanStartKeyboardBatchEdit(candidateCol))
            return false;

        var candidateItem = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (candidateItem == null)
            return false;

        item = candidateItem;
        rowIndex = ResolveRowIndex(candidateItem, _activeCell.Value.RowIndex);
        col = candidateCol;
        return true;
    }

    private async Task<bool> TryExtendSelectionWithShiftArrowAsync(KeyboardEventArgs e)
    {
        if (!TryGetShiftArrowSelection(e, out var rowDelta, out var cellDelta))
            return false;
        if (!AllowSelection || _isEditing || _batchEditItem != null)
            return false;

        var rows = GetKeyboardNavigationRowItems();
        var columns = VisibleColumns.ToList();
        if (rows.Count == 0 || columns.Count == 0)
            return false;

        if (!TryResolveActiveSelectionCell(rows, columns, out var currentItem, out var currentVisibleRowIndex, out var currentCellIndex))
            return false;

        if (_keyboardRangeAnchorItem == null)
        {
            _keyboardRangeAnchorItem = currentItem;
            _keyboardRangeAnchorCellIndex = currentCellIndex;
        }

        var targetVisibleRowIndex = Math.Clamp(currentVisibleRowIndex + rowDelta, 0, rows.Count - 1);
        var targetCellIndex = Math.Clamp(currentCellIndex + cellDelta, 0, columns.Count - 1);
        var targetItem = rows[targetVisibleRowIndex];
        var targetResolvedRowIndex = ResolveRowIndex(targetItem, targetVisibleRowIndex);
        var anchorVisibleRowIndex = rows.IndexOf(_keyboardRangeAnchorItem);
        if (anchorVisibleRowIndex < 0)
        {
            _keyboardRangeAnchorItem = currentItem;
            _keyboardRangeAnchorCellIndex = currentCellIndex;
            anchorVisibleRowIndex = currentVisibleRowIndex;
        }

        var anchorCellIndex = Math.Clamp(_keyboardRangeAnchorCellIndex, 0, columns.Count - 1);

        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
        {
            var startRow = Math.Min(anchorVisibleRowIndex, targetVisibleRowIndex);
            var endRow = Math.Max(anchorVisibleRowIndex, targetVisibleRowIndex);
            var startCell = SingleCellColumnMassEditEnabled
                ? anchorCellIndex
                : Math.Min(anchorCellIndex, targetCellIndex);
            var endCell = SingleCellColumnMassEditEnabled
                ? anchorCellIndex
                : Math.Max(anchorCellIndex, targetCellIndex);

            _selectedCells.Clear();
            for (var r = startRow; r <= endRow; r++)
            {
                var resolvedRow = ResolveRowIndex(rows[r], r);
                for (var c = startCell; c <= endCell; c++)
                    _selectedCells.Add((resolvedRow, c));
            }
        }
        else
        {
            var start = Math.Min(anchorVisibleRowIndex, targetVisibleRowIndex);
            var end = Math.Max(anchorVisibleRowIndex, targetVisibleRowIndex);
            _selectedItems.Clear();
            for (var i = start; i <= end; i++)
                _selectedItems.Add(rows[i]);
            ResetRowSelectionTypeAheadTarget();
            await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        }

        SetActiveCell(targetResolvedRowIndex, targetCellIndex);
        RememberKeyboardNavigationSource(targetItem, targetResolvedRowIndex, targetCellIndex);
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
            CaptureRowSelectionTypeAheadTarget(targetCellIndex);
        _lastSelectedCell = (targetResolvedRowIndex, targetCellIndex);
        _lastSelectedItem = targetItem;
        _lastSelectedRowIndex = targetResolvedRowIndex;
        _pendingActiveCellScrollIntoView = true;

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            var targetField = columns[targetCellIndex].Field;
            var value = string.IsNullOrWhiteSpace(targetField) ? null : GetPropertyValue(targetItem, targetField);
            await EventsRef.CellSelected.InvokeAsync(new CellSelectEventArgs<TValue>
            {
                Data = targetItem,
                RowIndex = targetResolvedRowIndex,
                CellIndex = targetCellIndex,
                CurrentValue = value,
                IsShiftPressed = true
            });
        }

        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private bool TryResolveActiveSelectionCell(
        List<TValue> rows,
        List<GridColumn> columns,
        out TValue item,
        out int visibleRowIndex,
        out int cellIndex)
    {
        item = default!;
        visibleRowIndex = -1;
        cellIndex = -1;

        if (_activeCell.HasValue)
        {
            var activeItem = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
            if (activeItem != null)
            {
                var activeVisibleIndex = rows.IndexOf(activeItem);
                if (activeVisibleIndex >= 0)
                {
                    item = activeItem;
                    visibleRowIndex = activeVisibleIndex;
                    cellIndex = Math.Clamp(_activeCell.Value.CellIndex, 0, columns.Count - 1);
                    return true;
                }
            }
        }

        var selectedItem = _lastSelectedItem != null && _selectedItems.Contains(_lastSelectedItem)
            ? _lastSelectedItem
            : _selectedItems.LastOrDefault();
        if (selectedItem != null)
        {
            var selectedVisibleIndex = rows.IndexOf(selectedItem);
            if (selectedVisibleIndex >= 0)
            {
                item = selectedItem;
                visibleRowIndex = selectedVisibleIndex;
                cellIndex = _lastSelectedCell?.CellIndex >= 0
                    ? Math.Clamp(_lastSelectedCell.Value.CellIndex, 0, columns.Count - 1)
                    : 0;
                return true;
            }
        }

        if (_lastSelectedCell.HasValue)
        {
            var selectedCellItem = GetItemAtResolvedRowIndex(_lastSelectedCell.Value.RowIndex);
            if (selectedCellItem != null)
            {
                var selectedCellVisibleIndex = rows.IndexOf(selectedCellItem);
                if (selectedCellVisibleIndex >= 0)
                {
                    item = selectedCellItem;
                    visibleRowIndex = selectedCellVisibleIndex;
                    cellIndex = Math.Clamp(_lastSelectedCell.Value.CellIndex, 0, columns.Count - 1);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> HandleSingleCellTypeAheadKeyAsync(KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;

        if (!TryGetActiveEditableCell(out var item, out var col))
            return false;

        if (!HasSingleCellBulkEditSelection())
            return await TryStartActiveBatchEditFromTypedKeyAsync(e);

        if ((e.Key == "Enter" || e.Key == "NumpadEnter") && _typeAheadBuffer.Length > 0)
        {
            await CommitSingleCellTypeAheadAsync(item, col);
            return true;
        }

        if (e.Key == "Escape" && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = "";
            await NotifyTypeAheadChangedAsync();
            return true;
        }

        if (e.Key == "Backspace" && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = _typeAheadBuffer[..^1];
            await NotifyTypeAheadChangedAsync();
            return true;
        }

        if (!IsEditableTypeAheadKey(e, col))
            return false;

        _typeAheadBuffer += e.Key;
        await NotifyTypeAheadChangedAsync();
        return true;
    }

    private bool HasSingleCellBulkEditSelection()
    {
        return _selectedItems.Count > 1
            || (SingleCellColumnMassEditEnabled && _selectedCells.Count > 1);
    }

    private async Task<bool> CommitPendingSingleCellTypeAheadAsync()
    {
        if (_typeAheadBuffer.Length == 0)
            return false;

        if (!TryGetActiveEditableCell(out var item, out var col))
        {
            _typeAheadBuffer = "";
            await NotifyTypeAheadChangedAsync();
            await InvokeAsync(StateHasChanged);
            return false;
        }

        await CommitSingleCellTypeAheadAsync(item, col);
        return true;
    }

    private bool TryGetActiveEditableCell(out TValue item, out GridColumn col)
    {
        item = default!;
        col = default!;

        if (!_activeCell.HasValue)
            return false;

        var candidateCol = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        if (candidateCol == null
            || string.IsNullOrWhiteSpace(candidateCol.Field)
            || !candidateCol.AllowEditing
            || candidateCol.IsPrimaryKey
            || candidateCol.Type == ColumnType.CheckBox)
            return false;

        var candidateItem = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (candidateItem == null)
            return false;

        item = candidateItem;
        col = candidateCol;
        return true;
    }

    private static bool IsEditableTypeAheadKey(KeyboardEventArgs e, GridColumn col)
    {
        if (e.Key.Length != 1)
            return false;

        if (col.Type != ColumnType.Number)
            return true;

        return char.IsDigit(e.Key[0])
            || e.Key == "."
            || e.Key == "-"
            || e.Key == "+";
    }

    private async Task CommitSingleCellTypeAheadAsync(TValue item, GridColumn col)
    {
        var field = col.Field;
        var newValue = _typeAheadBuffer;
        var targets = ResolveSingleCellColumnMassEditTargets(item, col);

        if (targets.Count > 1 && EventsRef?.OnTypeAheadCommit.HasDelegate == true)
        {
            await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
            {
                SelectedItems = targets,
                ColumnName = field,
                Value = newValue
            });
        }
        else
        {
            var committedAnyCell = false;
            foreach (var target in targets)
            {
                SetPropertyValue(target, field, newValue);
                committedAnyCell = true;

                if (EventsRef?.OnCellSave.HasDelegate == true)
                {
                    await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
                    {
                        Data = target,
                        ColumnName = field,
                        Value = newValue
                    });
                }
            }

            if (committedAnyCell)
                await EnsureTrailingNewRowIfNeededAsync();
        }

        _typeAheadBuffer = "";
        await NotifyTypeAheadChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    private List<TValue> ResolveSingleCellColumnMassEditTargets(TValue primary, GridColumn col)
    {
        if (!SingleCellColumnMassEditEnabled || !_activeCell.HasValue)
            return new List<TValue> { primary };

        var activeColumnIndex = _activeCell.Value.CellIndex;
        var activeCol = VisibleColumns.ElementAtOrDefault(activeColumnIndex);
        if (activeCol == null
            || string.IsNullOrWhiteSpace(activeCol.Field)
            || !string.Equals(activeCol.Field, col.Field, StringComparison.OrdinalIgnoreCase))
            return new List<TValue> { primary };

        var targets = new List<TValue>();
        var seen = new HashSet<TValue>();

        foreach (var selectedCell in _selectedCells.Where(c => c.CellIndex == activeColumnIndex))
        {
            var selectedItem = GetItemAtResolvedRowIndex(selectedCell.RowIndex);
            if (selectedItem != null && seen.Add(selectedItem))
                targets.Add(selectedItem);
        }

        if (seen.Add(primary))
            targets.Insert(0, primary);

        return targets.Count == 0 ? new List<TValue> { primary } : targets;
    }

    private async Task<bool> TryCommitSelectedRowOnEnterAsync(KeyboardEventArgs e)
    {
        if (!CommitSelectedRowOnEnter)
            return false;
        if (e.AltKey || e.CtrlKey || e.MetaKey || e.ShiftKey)
            return false;
        if (_isEditing || _batchEditItem != null)
            return false;
        if (_filterPopupField != null || _expressionFilterOpen || _showHeaderContextMenu || _showChooseColumnsDialog)
            return false;
        if (EventsRef?.OnRecordDoubleClick.HasDelegate != true)
            return false;
        if (_selectedItems.Count != 1)
            return false;

        var item = _lastSelectedItem != null && _selectedItems.Contains(_lastSelectedItem)
            ? _lastSelectedItem
            : _selectedItems.FirstOrDefault();

        if (item == null)
            return false;

        var rowIndex = _lastSelectedItem != null && EqualityComparer<TValue>.Default.Equals(item, _lastSelectedItem)
            ? _lastSelectedRowIndex ?? ResolveRowIndex(item, 0)
            : ResolveRowIndex(item, 0);

        await InvokeRecordDoubleClickAsync(item, rowIndex);
        return true;
    }

    private async Task NotifyTypeAheadChangedAsync()
    {
        if (EventsRef?.TypeAheadChanged.HasDelegate == true)
            await EventsRef.TypeAheadChanged.InvokeAsync(_typeAheadBuffer);
    }

    private void StartColumnDrag(string colField, DragEventArgs _)
    {
        _draggingColumnField = colField;
        _lastHeaderDragSourceField = colField;
        _lastHeaderDragStartedUtc = DateTime.UtcNow;
        _headerDragGeneration++;
        _draggingGroupChipField = null;
        _dragOverGroupArea = false;
        ClearHeaderDropIndicator();
    }

    private void EndColumnDrag()
    {
        var generation = _headerDragGeneration;
        _ = CleanupHeaderDragStateAsync(generation);
    }

    private async Task CleanupHeaderDragStateAsync(int generation)
    {
        await Task.Delay(120);
        if (generation != _headerDragGeneration) return;

        _draggingColumnField = null;
        _dragOverGroupArea = false;
        ClearHeaderDropIndicator();
        await InvokeAsync(StateHasChanged);
    }

    private void HandleHeaderDropZoneDragOver(string targetField, bool insertAfter, DragEventArgs _)
    {
        if (!AllowColumnReorder || string.IsNullOrEmpty(_draggingColumnField)) return;
        if (string.Equals(_draggingColumnField, targetField, StringComparison.OrdinalIgnoreCase))
        {
            ClearHeaderDropIndicator();
            return;
        }

        _dragOverHeaderField = targetField;
        _dragInsertAfterTarget = insertAfter;
    }

    private void ClearHeaderDropIndicator()
    {
        _dragOverHeaderField = null;
        _dragInsertAfterTarget = false;
    }

    private async Task HandleHeaderDropZoneDrop(string targetField, bool insertAfter, DragEventArgs _)
    {
        try
        {
            if (!AllowColumnReorder) return;

            var fromField = _draggingColumnField;
            if (string.IsNullOrEmpty(fromField))
            {
                if (!string.IsNullOrEmpty(_lastHeaderDragSourceField)
                    && DateTime.UtcNow - _lastHeaderDragStartedUtc <= TimeSpan.FromSeconds(2))
                {
                    fromField = _lastHeaderDragSourceField;
                }
            }

            if (string.IsNullOrEmpty(fromField)) return;
            if (string.Equals(fromField, targetField, StringComparison.OrdinalIgnoreCase)) return;
            if (_columnsContainer == null) return;

            var resolvedInsertAfter =
                string.Equals(_dragOverHeaderField, targetField, StringComparison.OrdinalIgnoreCase)
                    ? _dragInsertAfterTarget
                    : insertAfter;

            if (_columnsContainer.ReorderColumn(fromField, targetField, resolvedInsertAfter))
            {
                StateHasChanged();
                await SaveGridSettingsAsync();
                await FireLayoutChangedAsync();
            }
        }
        finally
        {
            _headerDragGeneration++;
            _draggingColumnField = null;
            _dragOverGroupArea = false;
            ClearHeaderDropIndicator();
        }
    }

    private bool _showHeaderContextMenu;
    private string _headerContextMenuField = "";
    private double _headerContextMenuX;
    private double _headerContextMenuY;
#pragma warning disable CS0414
    private bool _showInsertColumnSubmenu;
#pragma warning restore CS0414
    private bool _showRenameColumn;
    private string _renameColumnDraft = "";

    private readonly Dictionary<string, string> _headerOverrides =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, bool> _visibilityOverrides =
        new(StringComparer.Ordinal);

    internal bool IsColumnVisible(GridColumn col)
        => _visibilityOverrides.TryGetValue(col.Field, out var ov) ? ov : col.Visible;

    private void OpenHeaderContextMenu(MouseEventArgs e, string field)
    {
        _headerContextMenuField = field;
        _headerContextMenuX = e.ClientX;
        _headerContextMenuY = e.ClientY;
        _showHeaderContextMenu = true;
        _showInsertColumnSubmenu = false;
        _showRenameColumn = false;
    }

    private void CloseHeaderContextMenu()
    {
        _showHeaderContextMenu = false;
        _showInsertColumnSubmenu = false;
        _showRenameColumn = false;
        _headerContextMenuField = "";
    }

    private GridColumn? CurrentHeaderColumn =>
        string.IsNullOrEmpty(_headerContextMenuField)
            ? null
            : Columns.FirstOrDefault(c => string.Equals(c.Field, _headerContextMenuField, StringComparison.Ordinal));

    private bool IsHeaderColumnGrouped =>
        !string.IsNullOrEmpty(_headerContextMenuField)
        && _groupDescriptors.Any(g => string.Equals(g.Field, _headerContextMenuField, StringComparison.Ordinal));

    private bool CanHideHeaderColumn
    {
        get
        {
            var visibleCount = Columns.Count(IsColumnVisible);
            return visibleCount > 1;
        }
    }

    private IReadOnlyList<GridColumn> HiddenColumns =>
        Columns.Where(c => !IsColumnVisible(c) && !string.IsNullOrEmpty(c.Field)).ToList();

    private string HeaderColumnDisplay(GridColumn? col)
    {
        if (col == null) return "";
        if (_headerOverrides.TryGetValue(col.Field, out var custom) && !string.IsNullOrEmpty(custom))
            return custom;
        return col.DisplayHeader;
    }

    private async Task HeaderMenuToggleGroup()
    {
        var field = _headerContextMenuField;
        CloseHeaderContextMenu();
        if (string.IsNullOrEmpty(field)) return;
        if (_groupDescriptors.Any(g => g.Field == field))
            await RemoveGroup(field);
        else
            await AddGroup(field);
    }

    private async Task HeaderMenuExpandAll()
    {
        CloseHeaderContextMenu();
        await ExpandAllGroupAsync();
    }

    private async Task HeaderMenuCollapseAll()
    {
        CloseHeaderContextMenu();
        await CollapseAllGroupAsync();
    }

    private async Task HeaderMenuHideColumn()
    {
        var col = CurrentHeaderColumn;
        CloseHeaderContextMenu();
        if (col == null || !CanHideHeaderColumn) return;
        _visibilityOverrides[col.Field] = false;
        StateHasChanged();
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private void HeaderMenuToggleInsertSubmenu()
    {
        _showInsertColumnSubmenu = false;
        _showRenameColumn = false;
        HeaderMenuOpenChooseColumns();
    }

    private async Task HeaderMenuInsertColumn(string field)
    {
        var col = Columns.FirstOrDefault(c => c.Field == field);
        CloseHeaderContextMenu();
        if (col == null) return;
        _visibilityOverrides[col.Field] = true;
        StateHasChanged();
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private sealed class ChooseColumnRow
    {
        public string Field { get; init; } = "";
        public string Header { get; init; } = "";
        public bool Visible { get; set; }
    }

    private bool _showChooseColumnsDialog;
    private List<ChooseColumnRow> _chooseColumnsRows = new();
    private string _chooseColumnsSelectedField = "";

    private List<string>? _originalColumnOrder;
    private Dictionary<string, bool>? _originalVisibility;

    private void CaptureOriginalLayoutOnce()
    {
        if (_originalColumnOrder != null) return;
        var snapCols = Columns.Where(c => !string.IsNullOrEmpty(c.Field)).ToList();
        _originalColumnOrder = snapCols.Select(c => c.Field).ToList();
        _originalVisibility = snapCols.ToDictionary(c => c.Field, c => c.Visible, StringComparer.Ordinal);
    }

    private void HeaderMenuOpenChooseColumns()
    {
        CloseHeaderContextMenu();
        CaptureOriginalLayoutOnce();

        if (AvailableColumns != null)
        {
            _chooseColumnsRows = AvailableColumns
                .Where(d => !string.IsNullOrEmpty(d.Field))
                .Select(d => new ChooseColumnRow
                {
                    Field = d.Field,
                    Header = string.IsNullOrEmpty(d.Header) ? d.Field : d.Header,
                    Visible = d.Visible
                })
                .ToList();
        }
        else
        {
            _chooseColumnsRows = Columns
                .Where(c => !string.IsNullOrEmpty(c.Field))
                .Select(c => new ChooseColumnRow
                {
                    Field = c.Field,
                    Header = HeaderColumnDisplay(c),
                    Visible = IsColumnVisible(c)
                })
                .ToList();
        }
        _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
        _showChooseColumnsDialog = true;
        StateHasChanged();
    }

    private void ChooseColumnsSelect(string field) => _chooseColumnsSelectedField = field;

    private void ChooseColumnsToggle(ChooseColumnRow row) => row.Visible = !row.Visible;

    private ChooseColumnRow? CurrentChooseRow =>
        _chooseColumnsRows.FirstOrDefault(r => r.Field == _chooseColumnsSelectedField);

    private void ChooseColumnsMoveUp()
    {
        var idx = _chooseColumnsRows.FindIndex(r => r.Field == _chooseColumnsSelectedField);
        if (idx <= 0) return;
        var item = _chooseColumnsRows[idx];
        _chooseColumnsRows.RemoveAt(idx);
        _chooseColumnsRows.Insert(idx - 1, item);
    }

    private void ChooseColumnsMoveDown()
    {
        var idx = _chooseColumnsRows.FindIndex(r => r.Field == _chooseColumnsSelectedField);
        if (idx < 0 || idx >= _chooseColumnsRows.Count - 1) return;
        var item = _chooseColumnsRows[idx];
        _chooseColumnsRows.RemoveAt(idx);
        _chooseColumnsRows.Insert(idx + 1, item);
    }

    private void ChooseColumnsShow()
    {
        var row = CurrentChooseRow;
        if (row != null) row.Visible = true;
    }

    private void ChooseColumnsHide()
    {
        var row = CurrentChooseRow;
        if (row != null) row.Visible = false;
    }

    private void ChooseColumnsShowAll()
    {
        foreach (var r in _chooseColumnsRows) r.Visible = true;
    }

    private void ChooseColumnsHideAll()
    {
        foreach (var r in _chooseColumnsRows) r.Visible = false;
    }

    private void ChooseColumnsRestoreDefault()
    {
        if (DefaultColumns != null)
        {
            var defaults = DefaultColumns.Where(d => !string.IsNullOrEmpty(d.Field)).ToList();
            var defVisByField = new Dictionary<string, bool>(StringComparer.Ordinal);
            var defOrderByField = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < defaults.Count; i++)
            {
                defVisByField[defaults[i].Field] = defaults[i].Visible;
                if (!defOrderByField.ContainsKey(defaults[i].Field))
                    defOrderByField[defaults[i].Field] = i;
            }

            foreach (var row in _chooseColumnsRows)
                if (defVisByField.TryGetValue(row.Field, out var vis))
                    row.Visible = vis;

            _chooseColumnsRows = _chooseColumnsRows
                .Select((r, idx) => (r, idx))
                .OrderBy(t => defOrderByField.TryGetValue(t.r.Field, out var o) ? o : int.MaxValue)
                .ThenBy(t => t.idx)
                .Select(t => t.r)
                .ToList();

            _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
            return;
        }

        if (AvailableColumns != null)
        {
            _chooseColumnsRows = AvailableColumns
                .Where(d => !string.IsNullOrEmpty(d.Field))
                .Select(d => new ChooseColumnRow
                {
                    Field = d.Field,
                    Header = string.IsNullOrEmpty(d.Header) ? d.Field : d.Header,
                    Visible = d.Visible
                })
                .ToList();
            _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
            return;
        }

        if (_originalColumnOrder == null || _originalVisibility == null) return;
        var byField = Columns
            .Where(c => !string.IsNullOrEmpty(c.Field))
            .GroupBy(c => c.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _chooseColumnsRows = _originalColumnOrder
            .Where(byField.ContainsKey)
            .Select(f => new ChooseColumnRow
            {
                Field = f,
                Header = HeaderColumnDisplay(byField[f]),
                Visible = _originalVisibility.TryGetValue(f, out var v) ? v : byField[f].Visible
            })
            .ToList();
        _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
    }

    private void ChooseColumnsCancel()
    {
        _showChooseColumnsDialog = false;
        _chooseColumnsRows.Clear();
        _chooseColumnsSelectedField = "";
        StateHasChanged();
    }

    private async Task ChooseColumnsOk()
    {
        var snapshot = _chooseColumnsRows
            .Select(r => new ChooseColumnDescriptor
            {
                Field = r.Field,
                Header = r.Header,
                Visible = r.Visible
            })
            .ToList();

        if (OnColumnsChosen.HasDelegate)
        {
            await OnColumnsChosen.InvokeAsync(new ChooseColumnsResult { Columns = snapshot });
            await SaveSnapshotSettingsAsync(snapshot);
        }
        else
        {
            foreach (var row in _chooseColumnsRows)
                _visibilityOverrides[row.Field] = row.Visible;
            _columnsContainer?.ReorderColumns(_chooseColumnsRows.Select(r => r.Field));
            await SaveGridSettingsAsync();
        }

        _showChooseColumnsDialog = false;
        _chooseColumnsRows.Clear();
        _chooseColumnsSelectedField = "";
        StateHasChanged();
    }

    private void HeaderMenuStartRename()
    {
        var col = CurrentHeaderColumn;
        if (col == null) return;
        _renameColumnDraft = HeaderColumnDisplay(col);
        _showRenameColumn = true;
        _showInsertColumnSubmenu = false;
    }

    private async Task HeaderMenuCommitRename()
    {
        var field = _headerContextMenuField;
        var draft = _renameColumnDraft?.Trim() ?? "";
        CloseHeaderContextMenu();
        if (string.IsNullOrEmpty(field)) return;
        if (string.IsNullOrEmpty(draft))
            _headerOverrides.Remove(field);
        else
            _headerOverrides[field] = draft;
        StateHasChanged();
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private async Task HeaderMenuPrint()
    {
        CloseHeaderContextMenu();
        try { await JsRuntime.InvokeVoidAsync("window.print"); } catch {  }
    }

    private async Task HeaderMenuSaveAs()
    {
        CloseHeaderContextMenu();
        try { await JsRuntime.InvokeVoidAsync("window.print"); } catch {  }
    }

    private void StartGroupChipDrag(string groupField)
    {
        _draggingGroupChipField = groupField;
        _draggingColumnField = null;
    }

    private void HandleGroupAreaDragOver(DragEventArgs e)
    {
        _dragOverGroupArea = true;
    }

    private void HandleGroupAreaDragLeave(DragEventArgs e)
    {
        _dragOverGroupArea = false;
    }

    private async Task HandleGroupAreaDrop(DragEventArgs e)
    {
        _dragOverGroupArea = false;

        if (!string.IsNullOrEmpty(_draggingColumnField))
        {
            await AddGroup(_draggingColumnField);
            _draggingColumnField = null;
        }
        else if (!string.IsNullOrEmpty(_draggingGroupChipField))
        {
            var existing = _groupDescriptors.FirstOrDefault(g => g.Field == _draggingGroupChipField);
            if (existing != null)
            {
                _groupDescriptors.Remove(existing);
                _groupDescriptors.Add(existing);
            }
            _draggingGroupChipField = null;
        }

        ClearHeaderDropIndicator();
    }

    private async Task AddGroup(string colField)
    {
        if (_groupDescriptors.Any(g => g.Field == colField))
            return;

        var col = VisibleColumns.FirstOrDefault(c => c.Field == colField);
        if (col == null || !col.AllowGrouping) return;

        if (EventsRef?.Grouping.HasDelegate == true)
        {
            var args = new GroupEventArgs { Field = colField };
            await EventsRef.Grouping.InvokeAsync(args);
            if (args.Cancel) return;
        }

        _groupDescriptors.Add(new GroupDescriptor
        {
            Field = colField,
            HeaderText = col.DisplayHeader
        });
        _pageState.CurrentPage = 1;

        if (EventsRef?.Grouped.HasDelegate == true)
            await EventsRef.Grouped.InvokeAsync(new GroupEventArgs { Field = colField });
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private async Task RemoveGroup(string colField)
    {
        if (EventsRef?.Ungrouping.HasDelegate == true)
        {
            var args = new GroupEventArgs { Field = colField };
            await EventsRef.Ungrouping.InvokeAsync(args);
            if (args.Cancel) return;
        }

        _groupDescriptors.RemoveAll(g => g.Field == colField);
        _pageState.CurrentPage = 1;

        if (EventsRef?.Ungrouped.HasDelegate == true)
            await EventsRef.Ungrouped.InvokeAsync(new GroupEventArgs { Field = colField });
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private static int ComputeFirstHeaderAggregateColumnIndex(
        List<AggregateRow> headerAggRows, List<GridColumn> visibleCols)
    {
        for (int i = 0; i < visibleCols.Count; i++)
        {
            var field = visibleCols[i].Field;
            foreach (var aggRow in headerAggRows)
            {
                if (aggRow.Columns.Any(a => a.Field == field))
                    return i;
            }
        }
        return -1;
    }

    private async Task ClearAllGroupsAsync()
    {
        if (_groupDescriptors.Count == 0) return;
        var fields = _groupDescriptors.Select(g => g.Field).ToList();
        foreach (var f in fields)
            await RemoveGroup(f);
    }

    private void ToggleGroupCollapse(GroupResult<TValue> group)
    {
        group.IsCollapsed = !group.IsCollapsed;
        if (group.IsCollapsed)
            _collapsedGroupPaths.Add(group.GroupPath);
        else
            _collapsedGroupPaths.Remove(group.GroupPath);

        _allGroupsCollapsed = false;
        _expandAllGroups = false;
    }

    private void ApplyGroupCollapseState(IEnumerable<GroupResult<TValue>> groups, bool collapsed)
    {
        foreach (var group in groups)
        {
            group.IsCollapsed = collapsed;
            if (collapsed)
                _collapsedGroupPaths.Add(group.GroupPath);
            else
                _collapsedGroupPaths.Remove(group.GroupPath);

            if (group.SubGroups.Any())
                ApplyGroupCollapseState(group.SubGroups, collapsed);
        }
    }

    private RenderFragment RenderGroupedRows(IEnumerable<GroupResult<TValue>> groups, int level) => builder =>
    {
        foreach (var group in groups)
        {
            builder.OpenElement(0, "tr");
            builder.AddAttribute(1, "class", "fx-group-header-row");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => ToggleGroupCollapse(group)));

            for (int i = 0; i < level; i++)
            {
                builder.OpenElement(10, "td");
                builder.AddAttribute(11, "class", "fx-cell fx-group-indent");
                builder.AddAttribute(12, "style", "width:32px;");
                builder.CloseElement();
            }

            var totalSpan = TotalColumnCount - level;
            var labelColspan = totalSpan;
            var headerAggForSpan = AggregateRows is { Count: > 0 }
                ? AggregateRows.Where(r => r.ShowInGroupHeader).ToList()
                : new List<AggregateRow>();
            if (headerAggForSpan.Count > 0 && group.Aggregates.Count > 0)
            {
                var visibleColsForSpan = VisibleColumns.ToList();
                var firstAggIdx = ComputeFirstHeaderAggregateColumnIndex(headerAggForSpan, visibleColsForSpan);
                if (firstAggIdx >= 0)
                {
                    var nonAggSlots = totalSpan - visibleColsForSpan.Count + firstAggIdx;
                    if (nonAggSlots >= 1) labelColspan = nonAggSlots;
                }
            }
            builder.OpenElement(20, "td");
            builder.AddAttribute(21, "colspan", labelColspan);
            builder.AddAttribute(22, "class", "fx-cell fx-group-header-cell");

            if (ExpandIconTemplate != null)
            {
                builder.AddContent(30, ExpandIconTemplate, !group.IsCollapsed);
            }
            else
            {
                var iconGlyph = group.IsCollapsed ? ResolveCollapsedGlyph() : ResolveExpandedGlyph();
                var iconStyleClass = GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus
                    ? "fx-group-expand-icon-plusminus"
                    : "fx-group-expand-icon-triangle";
                var iconInlineStyle = ResolveExpandIconStyle();

                builder.OpenElement(30, "span");
                builder.AddAttribute(31, "class", $"fx-group-expand-icon {iconStyleClass} {(group.IsCollapsed ? "collapsed" : "expanded")}");
                if (!string.IsNullOrEmpty(iconInlineStyle))
                    builder.AddAttribute(32, "style", iconInlineStyle);
                builder.AddContent(33, iconGlyph);
                builder.CloseElement();
            }

            builder.OpenElement(40, "span");
            builder.AddAttribute(41, "class", "fx-group-header-value");
            if (!string.IsNullOrEmpty(GroupItemTextStyle))
                builder.AddAttribute(42, "style", GroupItemTextStyle);
            builder.AddContent(43, $"{group.Key}");
            builder.CloseElement();

            builder.OpenElement(50, "span");
            builder.AddAttribute(51, "class", "fx-group-header-right");

            if (ShowGroupCount)
            {
                builder.OpenElement(52, "span");
                builder.AddAttribute(53, "class", "fx-group-count");
                builder.AddContent(54, $"({group.Count} items)");
                builder.CloseElement();
            }

            if (AggregateRows is { Count: > 0 } && group.Aggregates.Count > 0)
            {
                var captionRows = AggregateRows.Where(r => r.ShowInGroupCaption).ToList();
                foreach (var aggRow in captionRows)
                {
                    foreach (var aggCol in aggRow.Columns)
                    {
                        var key = $"{aggCol.Field}_{aggCol.Type}";
                        if (group.Aggregates.TryGetValue(key, out var val) && val != null)
                        {
                            builder.OpenElement(55, "span");
                            builder.AddAttribute(56, "class", "fx-group-caption-agg");
                            if (!string.IsNullOrEmpty(GroupTotalTextStyle))
                                builder.AddAttribute(57, "style", GroupTotalTextStyle);
                            builder.AddContent(58, FormatAggregateValue(aggCol, val, aggCol.GroupCaptionTemplate));
                            builder.CloseElement();
                        }
                    }
                }
            }

            builder.CloseElement(); // right container

            builder.CloseElement(); // td (label cell)

            var headerAggRows = AggregateRows is { Count: > 0 }
                ? AggregateRows.Where(r => r.ShowInGroupHeader).ToList()
                : new List<AggregateRow>();
            if (headerAggRows.Count > 0 && group.Aggregates.Count > 0)
            {
                var visibleCols = VisibleColumns.ToList();
                var firstAggIdx = ComputeFirstHeaderAggregateColumnIndex(headerAggRows, visibleCols);
                if (firstAggIdx >= 0)
                {
                    for (int ci = firstAggIdx; ci < visibleCols.Count; ci++)
                    {
                        var col = visibleCols[ci];
                        AggregateColumn? aggCol = null;
                        foreach (var aggRow in headerAggRows)
                        {
                            aggCol = aggRow.Columns.FirstOrDefault(a => a.Field == col.Field);
                            if (aggCol != null) break;
                        }

                        builder.OpenElement(155, "td");
                        builder.AddAttribute(156, "class", "fx-cell fx-group-header-aggregate-cell");
                        builder.AddAttribute(157, "style", CombineStyles(col.GetCellStyle(), GroupTotalTextStyle));

                        if (aggCol != null)
                        {
                            var key = $"{aggCol.Field}_{aggCol.Type}";
                            if (group.Aggregates.TryGetValue(key, out var val) && val != null)
                            {
                                builder.OpenElement(158, "span");
                                builder.AddAttribute(159, "class", "fx-aggregate-value");
                                builder.AddContent(160, FormatAggregateValue(aggCol, val,
                                    aggCol.GroupCaptionTemplate ?? aggCol.GroupFooterTemplate));
                                builder.CloseElement();
                            }
                        }

                        builder.CloseElement(); // td
                    }
                }
            }

            builder.CloseElement(); // tr

            if (!group.IsCollapsed)
            {
                if (group.SubGroups.Any())
                {
                    builder.AddContent(60, RenderGroupedRows(group.SubGroups, level + 1));
                }
                else
                {
                    var rowIdx = 0;
                    foreach (var item in group.Items)
                    {
                        var currentIdx = rowIdx;
                        var resolvedRowIdx = ResolveRowIndex(item, currentIdx);
                        var isSelected = _selectedItems.Contains(item);
                        var rowCssClass = GetRowCssClass(item, resolvedRowIdx);

                        builder.OpenElement(70, "tr");
                        builder.AddAttribute(71, "class",
                            $"fx-row {(rowIdx % 2 == 1 && EnableAltRow ? "fx-alt-row" : "")} {(isSelected && HighlightSelectedRows ? "fx-selected" : "")} {(EnableHover ? "fx-hover" : "")} {rowCssClass}");
                        builder.AddAttribute(72, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleRowClick(item, currentIdx, e)));
                        builder.AddAttribute(74, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, (Action<MouseEventArgs>)(e => HandleRowMouseDown(item, currentIdx, e))));
                        builder.AddAttribute(75, "onmouseenter", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleRowMouseEnter(item, currentIdx, e)));
                        var rowStyle = GetRowStyle(item, currentIdx, isSelected);
                        if (rowStyle.Length > 0)
                            builder.AddAttribute(73, "style", rowStyle);

                        if (AllowGrouping && HideGroupedColumns && GroupedLayoutColumns.Count > 0)
                        {
                            foreach (var gcol in GroupedLayoutColumns)
                            {
                                builder.OpenElement(80, "td");
                                builder.AddAttribute(81, "class", "fx-cell fx-group-indent");
                                builder.AddAttribute(82, "style", GetGroupedPlaceholderStyle(gcol));
                                builder.CloseElement();
                            }
                        }

                        if (ShowCheckboxColumn)
                        {
                            builder.OpenElement(90, "td");
                            builder.AddAttribute(91, "class", "fx-cell fx-checkbox-cell");
                            builder.AddAttribute(92, "style", "width:50px;");
                            builder.OpenComponent<CheckBoxControl>(93);
                            builder.AddAttribute(94, "Checked", isSelected);
                            builder.AddAttribute(95, "CheckedChanged",
                                EventCallback.Factory.Create<bool>(this,
                                    _ => ToggleRowSelection(item, currentIdx)));
                            builder.AddAttribute(96, "TabIndex", -1);
                            builder.AddAttribute(97, "StopClickPropagation", true);
                            builder.AddAttribute(98, "StopMouseDownPropagation", true);
                            builder.CloseComponent();
                            builder.CloseElement();
                        }

                        var colIdx = 0;
                        foreach (var col in VisibleColumns)
                        {
                            var capturedColIdx = colIdx;
                            var capturedCol = col;
                            var capturedItemForEdit = item;
                            var isBatchEditing = IsBatchEditing(item, col.Field);
                            var isBatchDropdownEditing = isBatchEditing && col.EditOptions?.Any() == true;
                            var isTypeAheadPreview = IsTypeAheadPreviewCell(item, col);
                            var isCellSelected = _selectedCells.Contains((resolvedRowIdx, capturedColIdx));
                            var isActiveCell = _activeCell.HasValue
                                && _activeCell.Value.RowIndex == resolvedRowIdx
                                && _activeCell.Value.CellIndex == capturedColIdx;
                            var showsEditableCue = CanShowEditableCellCue(capturedCol);
                            var editableClass = showsEditableCue ? " fx-cell-editable" : string.Empty;
                            var activeClass = isActiveCell
                                ? showsEditableCue ? " fx-cell-active fx-cell-active-editable" : " fx-cell-active"
                                : string.Empty;
                            var typeAheadClass = isTypeAheadPreview ? " fx-typeahead-preview-cell" : string.Empty;
                            var cellClass = "fx-cell"
                                + (isCellSelected ? " fx-cell-selected" : string.Empty)
                                + (isBatchEditing ? " fx-batch-editing" : string.Empty)
                                + (isBatchDropdownEditing ? " fx-batch-dropdown-editing" : string.Empty)
                                + activeClass + editableClass + typeAheadClass;
                            builder.OpenElement(100, "td");
                            builder.AddAttribute(101, "class", cellClass);
                            if (!string.IsNullOrEmpty(capturedCol.Field))
                                builder.AddAttribute(106, "data-field", capturedCol.Field);
                            builder.AddAttribute(102, "style", col.GetCellStyle());
                            builder.AddAttribute(107, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellMouseDown(item, resolvedRowIdx, capturedColIdx, e)));
                            builder.AddEventStopPropagationAttribute(108, "onmousedown", true);
                            builder.AddAttribute(109, "onmouseenter", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellMouseEnter(item, resolvedRowIdx, capturedColIdx, e)));

                            if (ShouldHandleCellDoubleClick(capturedCol))
                            {
                                builder.AddAttribute(104, "ondblclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellDblClick(capturedItemForEdit, resolvedRowIdx, capturedCol, e)));
                                builder.AddEventStopPropagationAttribute(110, "ondblclick", true);
                            }
                            builder.AddAttribute(103, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, args => HandleCellClick(item, resolvedRowIdx, capturedColIdx, args)));
                            builder.AddEventStopPropagationAttribute(105, "onclick", true);

                            if (isBatchEditing)
                            {
                                RenderBatchEditor(builder, 145, capturedItemForEdit, resolvedRowIdx, capturedCol);
                            }
                            else if (isTypeAheadPreview)
                            {
                                builder.OpenElement(153, "span");
                                builder.AddAttribute(154, "class", "fx-typeahead-preview-value");
                                builder.AddContent(155, _typeAheadBuffer);
                                builder.CloseElement();
                            }
                            else if (col.Commands is { Count: > 0 })
                            {
                                foreach (var cmd in col.Commands)
                                {
                                    var cmdType = cmd.Type;
                                    var capturedItem = item;
                                    var capturedIdx = currentIdx;
                                    builder.OpenElement(110, "button");
                                    builder.AddAttribute(111, "class", $"fx-cmd-btn fx-cmd-{cmdType.ToLower()}");
                                    builder.AddAttribute(112, "onclick", EventCallback.Factory.Create(this, () => HandleCommand(cmdType, capturedItem, capturedIdx)));
                                    builder.AddEventStopPropagationAttribute(114, "onclick", true);
                                    builder.AddEventStopPropagationAttribute(115, "onmousedown", true);
                                    builder.AddContent(113, cmdType);
                                    builder.CloseElement();
                                }
                            }
                            else if (col.EffectiveTemplate != null)
                            {
                                builder.AddContent(120, col.EffectiveTemplate((object)item!));
                            }
                            else if (col.Type == ColumnType.CheckBox)
                            {
                                if (!TryGetCheckboxDisplayValue(item, col.Field, out var checkedValue))
                                {
                                    RenderDisplayCellContent(builder, 130, item, col);
                                    builder.CloseElement();
                                    colIdx++;
                                    continue;
                                }

                                var cbItem = item;
                                var cbCol = capturedCol;
                                builder.OpenComponent<CheckBoxControl>(130);
                                builder.AddAttribute(131, "Disabled", !CanToggleCheckboxColumn(cbCol));
                                builder.AddAttribute(132, "Checked", checkedValue);
                                builder.AddAttribute(133, "TabIndex", CanToggleCheckboxColumn(cbCol) ? 0 : -1);
                                if (CanToggleCheckboxColumn(cbCol))
                                {
                                    builder.AddAttribute(134, "CheckedChanged",
                                        EventCallback.Factory.Create<bool>(this,
                                            value => HandleCheckboxToggle(cbItem, cbCol, value)));
                                    builder.AddAttribute(136, "OnFocus",
                                        EventCallback.Factory.Create<FocusEventArgs>(this,
                                            _ => ActivateCheckboxCellAsync(cbItem, resolvedRowIdx, capturedColIdx, false)));
                                    builder.AddAttribute(137, "OnKeyDown",
                                        EventCallback.Factory.Create<KeyboardEventArgs>(this,
                                            e => HandleCheckboxKeyDown(cbItem, resolvedRowIdx, capturedColIdx, cbCol, e)));
                                    builder.AddAttribute(138, "StopClickPropagation", true);
                                    builder.AddAttribute(139, "StopMouseDownPropagation", true);
                                    builder.AddAttribute(140, "StopKeyDownPropagation", true);
                                    builder.AddAttribute(141, "PreventKeyDownDefault", true);
                                }
                                builder.CloseComponent();
                            }
                            else
                            {
                                RenderDisplayCellContent(builder, 140, item, col);
                            }

                            builder.CloseElement(); // td
                            colIdx++;
                        }

                        builder.CloseElement(); // tr
                        rowIdx++;
                    }
                }

                if (AggregateRows is { Count: > 0 } && group.Aggregates.Count > 0)
                {
                    var footerRows = AggregateRows.Where(r => r.ShowInGroupFooter).ToList();
                    foreach (var aggRow in footerRows)
                    {
                        builder.OpenElement(200, "tr");
                        builder.AddAttribute(201, "class", "fx-group-footer-row");

                        for (int i = 0; i < _groupDescriptors.Count; i++)
                        {
                            builder.OpenElement(210, "td");
                            builder.AddAttribute(211, "class", "fx-cell fx-group-indent");
                            builder.AddAttribute(212, "style", "width:32px;");
                            builder.CloseElement();
                        }

                        foreach (var col in VisibleColumns)
                        {
                            var aggCol = aggRow.Columns.FirstOrDefault(a => a.Field == col.Field);
                            builder.OpenElement(220, "td");
                            builder.AddAttribute(221, "class", "fx-cell fx-aggregate-cell");
                            builder.AddAttribute(222, "style", CombineStyles(col.GetCellStyle(), GroupTotalTextStyle));

                            if (aggCol != null)
                            {
                                var key = $"{aggCol.Field}_{aggCol.Type}";
                                group.Aggregates.TryGetValue(key, out var val);
                                builder.OpenElement(230, "span");
                                builder.AddAttribute(231, "class", "fx-aggregate-value");
                                builder.AddContent(232, FormatAggregateValue(aggCol, val, aggCol.GroupFooterTemplate));
                                builder.CloseElement();
                            }

                            builder.CloseElement(); // td
                        }

                        builder.CloseElement(); // tr
                    }
                }
            }
        }
    };

    private RenderFragment RenderDataCells(TValue item, int rowIndex, Func<GridColumn, GridColumn> transform) => builder =>
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var visibleColumns = VisibleColumns.ToList();
        var colIdx = 0;
        foreach (var col in visibleColumns)
        {
            var capturedCol = col;
            var capturedColIdx = colIdx;
            var isLastDataCell = colIdx == visibleColumns.Count - 1;
            var isBatchEditing = IsBatchEditing(item, col.Field);
            var isBatchDropdownEditing = isBatchEditing && capturedCol.EditOptions?.Any() == true;
            var isTypeAheadPreview = IsTypeAheadPreviewCell(item, col);

            builder.OpenElement(0, "td");
            var isCellSelected = _selectedCells.Contains((resolvedRowIndex, colIdx));
            var isActiveCell = _activeCell.HasValue
                && _activeCell.Value.RowIndex == resolvedRowIndex
                && _activeCell.Value.CellIndex == colIdx;
            var showsEditableCue = CanShowEditableCellCue(capturedCol);
            var editableClass = showsEditableCue ? " fx-cell-editable" : string.Empty;
            var activeClass = isActiveCell
                ? showsEditableCue ? " fx-cell-active fx-cell-active-editable" : " fx-cell-active"
                : string.Empty;
            var typeAheadClass = isTypeAheadPreview ? " fx-typeahead-preview-cell" : string.Empty;
            var cellClass = "fx-cell"
                + (isCellSelected ? " fx-cell-selected" : string.Empty)
                + (isBatchEditing ? " fx-batch-editing" : string.Empty)
                + (isBatchDropdownEditing ? " fx-batch-dropdown-editing" : string.Empty)
                + activeClass + editableClass + typeAheadClass;
            builder.AddAttribute(1, "class", cellClass);
            if (!string.IsNullOrEmpty(capturedCol.Field))
                builder.AddAttribute(7, "data-field", capturedCol.Field);
            builder.AddAttribute(2, "style", col.GetCellStyle());
            builder.AddAttribute(8, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellMouseDown(item, resolvedRowIndex, capturedColIdx, e)));
            builder.AddEventStopPropagationAttribute(9, "onmousedown", true);
            builder.AddAttribute(10, "onmouseenter", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellMouseEnter(item, resolvedRowIndex, capturedColIdx, e)));
            if (col.ClipMode == ClipMode.EllipsisWithTooltip)
                builder.AddAttribute(3, "title", GetCellDisplayValue(item, col));

            if (ShouldHandleCellDoubleClick(capturedCol))
            {
                builder.AddAttribute(4, "ondblclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellDblClick(item, resolvedRowIndex, capturedCol, e)));
                builder.AddEventStopPropagationAttribute(11, "ondblclick", true);
            }
            builder.AddAttribute(5, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, args => HandleCellClick(item, resolvedRowIndex, capturedColIdx, args)));
            builder.AddEventStopPropagationAttribute(6, "onclick", true);

            if (isBatchEditing)
            {
                RenderBatchEditor(builder, 45, item, resolvedRowIndex, capturedCol);
            }
            else if (isTypeAheadPreview)
            {
                builder.OpenElement(53, "span");
                builder.AddAttribute(54, "class", "fx-typeahead-preview-value");
                builder.AddContent(55, _typeAheadBuffer);
                builder.CloseElement();
            }
            else if (col.Commands is { Count: > 0 })
            {
                foreach (var cmd in col.Commands)
                {
                    var cmdType = cmd.Type;
                    var capturedItem = item;
                    builder.OpenElement(10, "button");
                    builder.AddAttribute(11, "class", $"fx-cmd-btn fx-cmd-{cmdType.ToLower()}");
                    builder.AddAttribute(12, "onclick", EventCallback.Factory.Create(this, () => HandleCommand(cmdType, capturedItem, 0)));
                    builder.AddEventStopPropagationAttribute(14, "onclick", true);
                    builder.AddEventStopPropagationAttribute(15, "onmousedown", true);
                    builder.AddContent(13, cmdType);
                    builder.CloseElement();
                }
            }
            else if (col.EffectiveTemplate != null)
            {
                builder.AddContent(20, col.EffectiveTemplate((object)item!));
            }
            else if (col.Type == ColumnType.CheckBox)
            {
                if (!TryGetCheckboxDisplayValue(item, col.Field, out var checkedValue))
                {
                    RenderDisplayCellContent(builder, 30, item, col);
                    builder.CloseElement();
                    colIdx++;
                    continue;
                }

                var cbItem = item;
                var cbCol = col;
                builder.OpenComponent<CheckBoxControl>(30);
                builder.AddAttribute(31, "Disabled", !CanToggleCheckboxColumn(cbCol));
                builder.AddAttribute(32, "Checked", checkedValue);
                builder.AddAttribute(33, "TabIndex", CanToggleCheckboxColumn(cbCol) ? 0 : -1);
                if (CanToggleCheckboxColumn(cbCol))
                {
                    builder.AddAttribute(34, "CheckedChanged",
                        EventCallback.Factory.Create<bool>(this,
                            value => HandleCheckboxToggle(cbItem, cbCol, value)));
                    builder.AddAttribute(36, "OnFocus",
                        EventCallback.Factory.Create<FocusEventArgs>(this,
                            _ => ActivateCheckboxCellAsync(cbItem, resolvedRowIndex, capturedColIdx, false)));
                    builder.AddAttribute(37, "OnKeyDown",
                        EventCallback.Factory.Create<KeyboardEventArgs>(this,
                            e => HandleCheckboxKeyDown(cbItem, resolvedRowIndex, capturedColIdx, cbCol, e)));
                    builder.AddAttribute(38, "StopClickPropagation", true);
                    builder.AddAttribute(39, "StopMouseDownPropagation", true);
                    builder.AddAttribute(40, "StopKeyDownPropagation", true);
                    builder.AddAttribute(41, "PreventKeyDownDefault", true);
                }
                builder.CloseComponent();
            }
            else
            {
                RenderDisplayCellContent(builder, 40, item, col);
            }

            if (AllowRowResizing && isLastDataCell)
            {
                builder.OpenElement(900, "span");
                builder.AddAttribute(901, "class", "fx-row-resize-handle");
                builder.AddAttribute(902, "title", "Resize row");
                builder.AddAttribute(903, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, e => StartRowResize(item, resolvedRowIndex, e)));
                builder.AddEventStopPropagationAttribute(904, "onmousedown", true);
                builder.AddEventPreventDefaultAttribute(905, "onmousedown", true);
                builder.CloseElement();
            }

            builder.CloseElement(); // td
            colIdx++;
        }
    };

    private RenderFragment RenderRowSelectorHandleCell(TValue item, int rowIndex) => builder =>
    {
        var isSelected = _selectedItems.Contains(item);
        var isEmphasized = IsRowSelectorHandleEmphasized(item);
        var canShowHandle = CanShowRowSelectorHandle(item);
        var cellClass = "fx-cell fx-row-selector-cell"
            + (isSelected ? " fx-row-selector-selected" : string.Empty)
            + (isEmphasized ? " fx-row-selector-emphasis" : string.Empty);

        builder.OpenElement(0, "td");
        builder.AddAttribute(1, "class", cellClass);
        builder.AddAttribute(2, "style", RowSelectorHandleColumnStyle);

        if (canShowHandle)
        {
            builder.OpenElement(3, "button");
            builder.AddAttribute(4, "type", "button");
            builder.AddAttribute(5, "class", $"fx-row-selector-handle fx-row-selector-handle-{RowSelectorHandleShapeClass}");
            builder.AddAttribute(6, "title", "Select row");
            builder.AddAttribute(7, "aria-label", "Select row");
            builder.AddAttribute(8, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleRowSelectorHandleClick(item, rowIndex, e)));
            builder.AddAttribute(15, "style", GetRowSelectorHandleStyle(isSelected, isEmphasized));
            builder.AddEventStopPropagationAttribute(9, "onclick", true);
            builder.AddAttribute(10, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs _) => { }));
            builder.AddEventStopPropagationAttribute(11, "onmousedown", true);
            builder.AddEventPreventDefaultAttribute(12, "onmousedown", true);
            builder.OpenElement(13, "span");
            builder.AddAttribute(14, "class", "fx-row-selector-handle-mark");
            builder.AddAttribute(16, "style", GetRowSelectorHandleMarkStyle(isSelected));
            builder.CloseElement();
            builder.CloseElement();
        }

        builder.CloseElement();
    };

    private void StartResize(GridColumn col, MouseEventArgs e)
    {
        _resizingCol = col;
        _resizeStartX = e.ClientX;

        if (col.RuntimeWidth.HasValue)
            _resizeStartWidth = col.RuntimeWidth.Value;
        else if (!string.IsNullOrEmpty(col.Width))
        {
            var w = col.Width.Replace("px", "").Replace("%", "").Trim();
            if (double.TryParse(w, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                _resizeStartWidth = parsed;
            else
                _resizeStartWidth = 150;
        }
        else
            _resizeStartWidth = 150;
    }

    private async Task HandleResizeMove(MouseEventArgs e)
    {
        if (_resizingCol == null) return;

        var delta = e.ClientX - _resizeStartX;
        var newWidth = Math.Max(40, _resizeStartWidth + delta);

        if (EventsRef?.ColumnResizing.HasDelegate == true)
        {
            var args = new ResizeEventArgs
            {
                Field = _resizingCol.Field,
                OldWidth = _resizingCol.RuntimeWidth ?? _resizeStartWidth,
                NewWidth = newWidth
            };
            await EventsRef.ColumnResizing.InvokeAsync(args);
            if (args.Cancel) return;
        }

        _resizingCol.RuntimeWidth = newWidth;
    }

    private async Task EndResize(MouseEventArgs e)
    {
        if (_resizingCol != null && EventsRef?.ColumnResized.HasDelegate == true)
        {
            await EventsRef.ColumnResized.InvokeAsync(new ResizeEventArgs
            {
                Field = _resizingCol.Field,
                OldWidth = _resizeStartWidth,
                NewWidth = _resizingCol.RuntimeWidth ?? _resizeStartWidth
            });
        }
        _resizingCol = null;
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private void StartRowResize(TValue item, int resolvedRowIndex, MouseEventArgs e)
    {
        if (!AllowRowResizing)
            return;

        _resizingRowItem = item;
        _resizingRowIndex = resolvedRowIndex;
        _rowResizeStartY = e.ClientY;
        _rowResizeStartHeight = GetEffectiveRowHeight(item, resolvedRowIndex)
            ?? Math.Max(MinRowHeight, RowHeight > 0 ? RowHeight : 24);
    }

    private async Task HandleGridResizeMove(MouseEventArgs e)
    {
        if (_resizingCol != null)
        {
            await HandleResizeMove(e);
            return;
        }

        if (_resizingRowIndex >= 0)
            await HandleRowResizeMove(e);
    }

    private async Task EndGridResize(MouseEventArgs e)
    {
        if (_resizingCol != null)
        {
            await EndResize(e);
            return;
        }

        if (_resizingRowIndex >= 0)
            await EndRowResize(e);
    }

    private async Task HandleRowResizeMove(MouseEventArgs e)
    {
        if (_resizingRowIndex < 0)
            return;

        var delta = e.ClientY - _rowResizeStartY;
        var newHeight = Math.Max(MinRowHeight, _rowResizeStartHeight + delta);

        if (EventsRef?.RowResizing.HasDelegate == true || RowResizing.HasDelegate)
        {
            var args = new RowResizeEventArgs<TValue>
            {
                Data = _resizingRowItem,
                RowIndex = _resizingRowIndex,
                OldHeight = _rowResizeStartHeight,
                NewHeight = newHeight
            };
            if (EventsRef?.RowResizing.HasDelegate == true)
                await EventsRef.RowResizing.InvokeAsync(args);
            if (args.Cancel)
                return;

            if (RowResizing.HasDelegate)
                await RowResizing.InvokeAsync(args);
            if (args.Cancel)
                return;

            newHeight = Math.Max(MinRowHeight, args.NewHeight);
        }

        _runtimeRowHeights[_resizingRowIndex] = newHeight;
    }

    private async Task EndRowResize(MouseEventArgs e)
    {
        if (_resizingRowIndex >= 0 && (EventsRef?.RowResized.HasDelegate == true || RowResized.HasDelegate))
        {
            _runtimeRowHeights.TryGetValue(_resizingRowIndex, out var runtimeHeight);
            var args = new RowResizeEventArgs<TValue>
            {
                Data = _resizingRowItem,
                RowIndex = _resizingRowIndex,
                OldHeight = _rowResizeStartHeight,
                NewHeight = runtimeHeight <= 0 ? _rowResizeStartHeight : runtimeHeight
            };

            if (EventsRef?.RowResized.HasDelegate == true)
                await EventsRef.RowResized.InvokeAsync(args);
            if (RowResized.HasDelegate)
                await RowResized.InvokeAsync(args);
        }

        _resizingRowItem = default;
        _resizingRowIndex = -1;
        await FireLayoutChangedAsync();
    }

    private RenderFragment RenderEditRow() => builder =>
    {
        builder.OpenElement(0, "tr");
        builder.AddAttribute(1, "class", "fx-row fx-edit-row");

        if (ShowRowReorderColumn)
        {
            builder.OpenElement(9, "td");
            builder.AddAttribute(10, "class", "fx-cell fx-row-reorder-cell disabled");
            builder.AddAttribute(11, "style", RowReorderColumnStyle);
            builder.CloseElement();
        }

        if (ShowRowSelectorHandleColumn)
        {
            builder.OpenElement(6, "td");
            builder.AddAttribute(7, "class", "fx-cell fx-row-selector-cell");
            builder.AddAttribute(8, "style", RowSelectorHandleColumnStyle);
            builder.CloseElement();
        }

        if (ShowCheckboxColumn)
        {
            builder.OpenElement(2, "td");
            builder.AddAttribute(3, "class", "fx-cell");
            builder.CloseElement();
        }

        if (AllowGrouping && _groupDescriptors.Count > 0)
        {
            for (int i = 0; i < _groupDescriptors.Count; i++)
            {
                builder.OpenElement(4, "td");
                builder.AddAttribute(5, "class", "fx-cell fx-group-indent");
                builder.CloseElement();
            }
        }

        foreach (var col in VisibleColumns)
        {
            builder.OpenElement(10, "td");
            builder.AddAttribute(11, "class", "fx-cell fx-edit-cell");

            if (col.Commands is { Count: > 0 })
            {
                builder.OpenElement(20, "button");
                builder.AddAttribute(21, "class", "fx-cmd-btn fx-cmd-save");
                builder.AddAttribute(22, "onclick", EventCallback.Factory.Create(this, SaveEdit));
                builder.AddContent(23, "Save");
                builder.CloseElement();

                builder.OpenElement(30, "button");
                builder.AddAttribute(31, "class", "fx-cmd-btn fx-cmd-cancel");
                builder.AddAttribute(32, "onclick", EventCallback.Factory.Create(this, CancelEdit));
                builder.AddContent(33, "Cancel");
                builder.CloseElement();
            }
            else if (!string.IsNullOrEmpty(col.Field) && col.AllowEditing && !col.IsPrimaryKey)
            {
                if (col.EditTemplate != null && _editItem != null)
                {
                    builder.AddContent(40, col.EditTemplate((object)_editItem));
                }
                else
                {
                    var inputType = GetEditorInputType(col);

                    builder.OpenElement(50, "input");
                    builder.AddAttribute(51, "type", inputType);
                    builder.AddAttribute(52, "class", "fx-edit-input");
                    builder.AddAttribute(53, "value", GetPropertyValue(_editItem, col.Field));
                    builder.AddAttribute(55, "style", GetEditorInputStyle(col));
                    if (col.Type == ColumnType.Number && !col.ShowNumericSpinner)
                        builder.AddAttribute(56, "inputmode", "decimal");
                    builder.AddAttribute(54, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
                        e => SetPropertyValue(_editItem, col.Field, e.Value?.ToString())));
                    builder.CloseElement();
                }
            }
            else
            {
                builder.AddContent(60, GetPropertyValue(_editItem, col.Field)?.ToString() ?? "");
            }

            builder.CloseElement(); // td
        }

        builder.CloseElement(); // tr
    };

    private static string GetEditorInputType(GridColumn col)
    {
        if (col.Type == ColumnType.Date)
            return "date";

        return col.Type == ColumnType.Number && col.ShowNumericSpinner
            ? "number"
            : "text";
    }

    private static string GetEditorInputStyle(GridColumn col)
    {
        var align = col.TextAlign switch
        {
            TextAlign.Center => "center",
            TextAlign.Right => "right",
            _ => "left"
        };

        return $"text-align:{align};";
    }

    private ColumnState GetColumnState(string field)
    {
        if (!_columnStates.TryGetValue(field, out var state))
        {
            state = new ColumnState { Field = field };
            _columnStates[field] = state;
        }
        return state;
    }

    private static readonly ConcurrentDictionary<(Type ItemType, string FieldKey), PropertyAccessor?> PropertyAccessorCache = new();

    private sealed class PropertyAccessor
    {
        public PropertyAccessor(Type propertyType, Func<object, object?>? getter, Action<object, object?>? setter)
        {
            PropertyType = propertyType;
            Getter = getter;
            Setter = setter;
        }

        public Type PropertyType { get; }
        public Func<object, object?>? Getter { get; }
        public Action<object, object?>? Setter { get; }
        public bool CanRead => Getter != null;
        public bool CanWrite => Setter != null;
    }

    private static object? GetPropertyValue(object? item, string field)
    {
        if (item == null || string.IsNullOrEmpty(field)) return null;
        if (TryGetDictionaryValue(item, field, out var dictValue))
            return dictValue;

        var accessor = GetPropertyAccessor(item.GetType(), field);
        if (accessor?.CanRead != true)
            return null;

        try
        {
            return accessor.Getter!(item);
        }
        catch
        {
            return null;
        }
    }

    private static void SetPropertyValue(object? item, string field, string? value)
    {
        _ = SetPropertyObjectValue(item, field, value);
    }

    private static bool SetPropertyObjectValue(object? item, string field, object? value)
    {
        if (item == null || string.IsNullOrEmpty(field))
            return false;

        if (TrySetDictionaryValue(item, field, value))
            return true;

        var accessor = GetPropertyAccessor(item.GetType(), field);
        if (accessor?.CanWrite != true)
            return false;

        try
        {
            var converted = ConvertToPropertyType(value, accessor.PropertyType);
            accessor.Setter!(item, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool GetBoolValue(object? item, string field)
    {
        var val = GetPropertyValue(item, field);
        return val switch
        {
            bool b => b,
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            decimal d => d != 0,
            double d => Math.Abs(d) > double.Epsilon,
            float f => Math.Abs(f) > float.Epsilon,
            string s when bool.TryParse(s, out var b) => b,
            string s when int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) => i != 0,
            _ => false
        };
    }

    private static bool TryGetDictionaryValue(object item, string field, out object? value)
    {
        if (item is IDictionary<string, object> dict)
        {
            if (dict.TryGetValue(field, out value))
                return true;
            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, field, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
        }
        else if (item is IDictionary<string, object?> dictNullable)
        {
            if (dictNullable.TryGetValue(field, out value))
                return true;
            foreach (var kvp in dictNullable)
            {
                if (string.Equals(kvp.Key, field, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static bool TrySetDictionaryValue(object item, string field, object? value)
    {
        if (item is IDictionary<string, object> dict)
        {
            dict[field] = value ?? "";
            return true;
        }
        if (item is IDictionary<string, object?> dictNullable)
        {
            dictNullable[field] = value;
            return true;
        }
        return false;
    }

    private static PropertyAccessor? GetPropertyAccessor(Type itemType, string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        var key = (ItemType: itemType, FieldKey: field.ToUpperInvariant());
        return PropertyAccessorCache.GetOrAdd(key, static cacheKey => CreatePropertyAccessor(cacheKey.ItemType, cacheKey.FieldKey));
    }

    private static PropertyAccessor? CreatePropertyAccessor(Type itemType, string field)
    {
        var prop = itemType.GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || prop.GetIndexParameters().Length > 0)
            return null;

        Func<object, object?>? getter = null;
        if (prop.CanRead)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var castInstance = Expression.Convert(instance, itemType);
            var propertyValue = Expression.Property(castInstance, prop);
            var boxed = Expression.Convert(propertyValue, typeof(object));
            getter = Expression.Lambda<Func<object, object?>>(boxed, instance).Compile();
        }

        Action<object, object?>? setter = null;
        if (prop.CanWrite)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var value = Expression.Parameter(typeof(object), "value");
            var castInstance = Expression.Convert(instance, itemType);
            var castValue = Expression.Convert(value, prop.PropertyType);
            var assign = Expression.Assign(Expression.Property(castInstance, prop), castValue);
            setter = Expression.Lambda<Action<object, object?>>(assign, instance, value).Compile();
        }

        return new PropertyAccessor(prop.PropertyType, getter, setter);
    }

    private static object? ConvertToPropertyType(object? value, Type propertyType)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(propertyType);
        var targetType = nullableUnderlying ?? propertyType;
        var allowsNull = nullableUnderlying != null || !propertyType.IsValueType;

        if (value == null)
            return allowsNull ? null : Activator.CreateInstance(targetType);

        if (targetType.IsInstanceOfType(value))
            return value;

        if (value is string s)
        {
            if (string.IsNullOrEmpty(s))
                return allowsNull ? null : Activator.CreateInstance(targetType);

            if (targetType == typeof(DateTime))
                return DateTime.Parse(s, CultureInfo.InvariantCulture);

            if (targetType == typeof(Guid))
                return Guid.Parse(s);

            if (targetType == typeof(bool))
            {
                if (bool.TryParse(s, out var parsedBool))
                    return parsedBool;
                if (string.Equals(s, "1", StringComparison.Ordinal))
                    return true;
                if (string.Equals(s, "0", StringComparison.Ordinal))
                    return false;
            }

            if (targetType.IsEnum)
                return Enum.Parse(targetType, s, ignoreCase: true);

            return Convert.ChangeType(s, targetType, CultureInfo.InvariantCulture);
        }

        if (targetType.IsEnum)
        {
            var enumUnderlying = Enum.GetUnderlyingType(targetType);
            var numeric = Convert.ChangeType(value, enumUnderlying, CultureInfo.InvariantCulture);
            return Enum.ToObject(targetType, numeric!);
        }

        if (targetType == typeof(Guid))
            return value is Guid g ? g : Guid.Parse(value.ToString() ?? string.Empty);

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private string GetTableStyle()
    {
        var totalWidth = GetTotalColumnWidthPx();
        if (totalWidth <= 0)
            return WidthMode == GridWidthMode.FitColumns ? "width:auto;min-width:0;" : "width:100%;";

        var widthPx = totalWidth.ToString("0.##", CultureInfo.InvariantCulture);
        return WidthMode == GridWidthMode.FitColumns
            ? $"width:{widthPx}px;min-width:{widthPx}px;max-width:none;"
            : $"width:100%;min-width:{widthPx}px;max-width:none;";
    }

    private string GetScrollSurfaceStyle()
    {
        var totalWidth = GetTotalColumnWidthPx();
        if (totalWidth <= 0)
            return WidthMode == GridWidthMode.FitColumns ? "width:auto;min-width:0;" : "width:100%;";

        var widthPx = totalWidth.ToString("0.##", CultureInfo.InvariantCulture);
        if (WidthMode != GridWidthMode.FitColumns)
            return $"width:100%;min-width:{widthPx}px;max-width:none;";

        var surfaceWidth = $"calc({widthPx}px + var(--fx-grid-scrollbar-gutter-width, 13px))";
        return $"width:{surfaceWidth};min-width:{surfaceWidth};max-width:none;";
    }

    private async Task ScrollGridBodyByAsync(int direction)
    {
        var module = await GetGridJsModuleAsync();
        if (module == null)
            return;

        try
        {
            await module.InvokeVoidAsync("scrollGridBodyByLine", _gridHostElement, direction);
        }
        catch
        {
        }
    }

    private string GetGroupedPlaceholderStyle(GridColumn col)
    {
        var width = GetColumnWidthPx(col);
        if (width <= 0)
            width = 120;
        return $"width:{width}px;min-width:{width}px;max-width:{width}px;";
    }

    private double GetTotalColumnWidthPx()
    {
        var total = 0d;
        if (ShowCheckboxColumn)
            total += 50;
        if (ShowRowReorderColumn)
            total += RowReorderColumnWidth;
        if (ShowRowSelectorHandleColumn)
            total += ResolvedRowSelectorHandleWidth;

        if (AllowGrouping && HideGroupedColumns && GroupedLayoutColumns.Count > 0)
        {
            foreach (var gcol in GroupedLayoutColumns)
            {
                var width = GetColumnWidthPx(gcol);
                total += width > 0 ? width : 120;
            }
        }

        foreach (var col in VisibleColumns)
        {
            var width = GetColumnWidthPx(col);
            if (width > 0)
                total += width;
        }
        return total;
    }

    private static double GetColumnWidthPx(GridColumn col)
    {
        if (col.RuntimeWidth.HasValue)
            return col.RuntimeWidth.Value;

        var parsed = TryParseWidthPx(col.Width);
        return parsed ?? 0;
    }

    private static double? TryParseWidthPx(string? width)
    {
        if (string.IsNullOrWhiteSpace(width))
            return null;
        var trimmed = width.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            return px;
        return null;
    }

    private bool EnsureAutoColumnWidths()
    {
        if (_columnsContainer == null)
            return false;

        var sample = (DataSource?.Take(50).ToList()) ?? new List<TValue>();
        var changed = false;

        foreach (var col in _columnsContainer.Columns)
        {
            if (!col.Visible)
                continue;
            if (col.RuntimeWidth.HasValue)
                continue;
            if (!string.IsNullOrWhiteSpace(col.Width))
                continue;

            var width = EstimateColumnWidth(col, sample);
            col.RuntimeWidth = width;
            changed = true;
        }

        return changed;
    }

    private double EstimateColumnWidth(GridColumn col, IReadOnlyList<TValue> sample)
    {
        var maxLen = col.DisplayHeader?.Length ?? 0;

        foreach (var item in sample)
        {
            var val = ResolveCellValue(item, col);
            if (val == null)
                continue;
            var text = Convert.ToString(val, CultureInfo.CurrentCulture) ?? "";
            if (text.Length > maxLen)
                maxLen = text.Length;
        }

        var px = (maxLen * 7.6) + 36;
        if (col.Type == ColumnType.Number)
            px += 12;
        return Math.Clamp(px, 80, 520);
    }

    private string GetCellDisplayValue(object? item, GridColumn col)
    {
        var val = ResolveCellValue(item, col);
        if (val == null) return "";

        if (!string.IsNullOrEmpty(col.Format))
        {
            if (val is IFormattable formattable)
                return formattable.ToString(col.Format, CultureInfo.CurrentCulture);
        }

        if (col.Type == ColumnType.Number)
            return FormatPlainNumber(val);

        return val.ToString() ?? "";
    }

    private static string FormatPlainNumber(object value)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        if (value is decimal decimalValue)
            return decimalValue.ToString("0.#############################", CultureInfo.InvariantCulture);

        if (value is double doubleValue)
            return doubleValue.ToString("0.#############################", CultureInfo.InvariantCulture);

        if (value is float floatValue)
            return floatValue.ToString("0.#############################", CultureInfo.InvariantCulture);

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";

        return value.ToString() ?? "";
    }

    private object? ResolveCellValue(object? item, GridColumn col)
    {
        if (!string.IsNullOrWhiteSpace(col.Formula))
            return EvaluateFormula(item, col.Formula);

        var val = GetPropertyValue(item, col.Field);
        if (AllowCellFormulas && val is string text && text.TrimStart().StartsWith('='))
            return EvaluateFormula(item, text);

        return val;
    }

    private object? EvaluateFormula(object? item, string formula)
    {
        if (item == null || string.IsNullOrWhiteSpace(formula))
            return null;

        var expression = formula.Trim();
        if (expression.StartsWith('='))
            expression = expression[1..];

        expression = ReplaceBracketedFormulaReferences(item, expression);
        expression = ReplaceBareFormulaReferences(item, expression);

        try
        {
            var result = new System.Data.DataTable().Compute(expression, null);
            return result == DBNull.Value ? null : result;
        }
        catch
        {
            return $"#{formula.TrimStart('=')}";
        }
    }

    private string ReplaceBracketedFormulaReferences(object item, string expression)
    {
        return Regex.Replace(expression, @"\[(?<field>[^\]]+)\]", match =>
        {
            var field = match.Groups["field"].Value;
            return FormulaValueLiteral(GetPropertyValue(item, field));
        });
    }

    private string ReplaceBareFormulaReferences(object item, string expression)
    {
        foreach (var field in GetFormulaCandidateFields(item).OrderByDescending(field => field.Length))
        {
            var pattern = $@"(?<![\w.]){Regex.Escape(field)}(?![\w.])";
            expression = Regex.Replace(expression, pattern, _ => FormulaValueLiteral(GetPropertyValue(item, field)));
        }

        return expression;
    }

    private IEnumerable<string> GetFormulaCandidateFields(object item)
    {
        if (item is IDictionary<string, object> dict)
            return dict.Keys;
        if (item is IDictionary<string, object?> nullableDict)
            return nullableDict.Keys;

        return item.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => property.Name);
    }

    private static string FormulaValueLiteral(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "0";
        if (value is bool boolValue)
            return boolValue ? "1" : "0";
        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "0";

        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.CurrentCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private TValue CreateNewItem()
    {
        if (NewItemFactory != null)
            return NewItemFactory();

        return Activator.CreateInstance<TValue>()
            ?? throw new InvalidOperationException($"Unable to create a new instance of {typeof(TValue).Name}. Configure NewItemFactory for this grid.");
    }

    private TValue CloneItem(TValue source)
    {
        if (CloneFactory != null)
            return CloneFactory(source);

        var clone = CreateNewItem();

        if (source is IDictionary<string, object?> srcDictN
            && clone is IDictionary<string, object?> tgtDictN)
        {
            tgtDictN.Clear();
            foreach (var kvp in srcDictN)
                tgtDictN[kvp.Key] = kvp.Value;
        }
        else if (source is IDictionary<string, object> srcDict
            && clone is IDictionary<string, object> tgtDict)
        {
            tgtDict.Clear();
            foreach (var kvp in srcDict)
                tgtDict[kvp.Key] = kvp.Value;
        }
        else
        {
            CopyProperties(source!, clone!);
        }

        return clone;
    }

    private static void CopyProperties(object source, object target)
    {
        foreach (var prop in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }

    public int TotalPages => _pageState.TotalPages;
    public int CurrentPage => _pageState.CurrentPage;

    public Task GoToPageAsync(int page) => GoToPage(page);

    public IEnumerable<TValue> GetSelectedRecords() => _selectedItems.ToList();

    public Task<List<TValue>> GetSelectedRecordsAsync() =>
        Task.FromResult(_selectedItems.ToList());

    public Task<List<(int RowIndex, int CellIndex)>> GetSelectedRowCellIndexesAsync() =>
        Task.FromResult(_selectedCells.ToList());

    public int? GetCurrentRowIndex() => _lastSelectedRowIndex;

    public void ClearSelection()
    {
        _selectedItems.Clear();
        ResetRowSelectionTypeAheadTarget();
        _ = NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
    }

    public async Task ClearSelectionAsync()
    {
        _selectedItems.Clear();
        _selectedCells.Clear();
        ResetRowSelectionTypeAheadTarget();
        await InvokeAsync(StateHasChanged);
        await NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
    }

    public void SelectRow(int rowIndex)
    {
        var list = PagedData.ToList();
        if (rowIndex >= 0 && rowIndex < list.Count)
        {
            var item = list[rowIndex];
            if ((SelectionSettingsRef?.Type ?? SelectionType.Single) == SelectionType.Single)
                _selectedItems.Clear();

            if (!_selectedItems.Contains(item))
                _selectedItems.Add(item);

            _lastSelectedItem = item;
            _lastSelectedRowIndex = ResolveRowIndex(item, rowIndex);
            _ = InvokeAsync(StateHasChanged);
            _ = NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
        }
    }

    private async Task NotifySelectionChangedAsync(GridSelectionChangeSource source = GridSelectionChangeSource.Unknown)
    {
        if (_selectedItems.Count <= 1)
            ResetRowSelectionTypeAheadTarget();

        if (_selectedItems.Count <= 1 && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = "";
            await NotifyTypeAheadChangedAsync();
        }

        var count = _selectedItems.Count;

        if (EventsRef?.SelectionChanged.HasDelegate == true)
            await EventsRef.SelectionChanged.InvokeAsync(count);

        if (EventsRef?.SelectionChangedDetailed.HasDelegate == true)
            await EventsRef.SelectionChangedDetailed.InvokeAsync(new GridSelectionChangedArgs
            {
                Count = count,
                Source = source
            });
    }

    public Task SelectCellAsync((int RowIndex, int CellIndex) cell, bool isCtrlPressed = false)
    {
        if (!isCtrlPressed)
        {
            _selectedCells.Clear();
        }

        if (!_selectedCells.Contains(cell))
        {
            _selectedCells.Add(cell);
        }

        return InvokeAsync(StateHasChanged);
    }

    public Task SelectCellsAsync(IEnumerable<(int RowIndex, int CellIndex)> cells)
    {
        _selectedCells.Clear();
        _selectedCells.AddRange(cells);
        return InvokeAsync(StateHasChanged);
    }

    public async Task SortByColumnAsync(string field, SortDirection direction)
    {
        foreach (var kvp in _columnStates) kvp.Value.SortDirection = null;
        GetColumnState(field).SortDirection = direction;
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task FilterByColumnAsync(string field, string value)
    {
        var state = GetColumnState(field);
        state.FilterValue = value;
        state.FilterOperator = TextFilterOperator.Contains;
        state.CheckedNumericRangeKeys.Clear();
        state.UseNumericRangeFilter = false;
        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearFilteringAsync()
    {
        ClearAllFilterState();
        await InvokeAsync(StateHasChanged);
    }

    public async Task AddRecordAsync(TValue record)
    {
        var list = DataSource as IList<TValue>;
        list?.Add(record);
        await InvokeAsync(StateHasChanged);
    }

    public Task<TValue> AppendRow(string? editField = null, bool beginEdit = true) =>
        AppendRowAsync(editField, beginEdit);

    public Task<TValue> AppendRow(TValue record, string? editField = null, bool beginEdit = true) =>
        AppendRowAsync(record, editField, beginEdit);

    public Task<TValue> InsertRow(int rowIndex, string? editField = null, bool beginEdit = true) =>
        InsertRowAsync(rowIndex, editField, beginEdit);

    public Task<TValue> InsertRow(int rowIndex, TValue record, string? editField = null, bool beginEdit = true) =>
        InsertRowAsync(rowIndex, record, editField, beginEdit);

    public Task<TValue> AppendRowAsync(string? editField = null, bool beginEdit = true) =>
        AppendRowAsync(CreateNewItem(), editField, beginEdit);

    public Task<TValue> AppendRowAsync(TValue record, string? editField = null, bool beginEdit = true)
    {
        var list = GetMutableDataSource();
        return InsertRowAsync(list.Count, record, editField, beginEdit);
    }

    public Task<TValue> InsertRowAsync(int rowIndex, string? editField = null, bool beginEdit = true) =>
        InsertRowAsync(rowIndex, CreateNewItem(), editField, beginEdit);

    public async Task<TValue> InsertRowAsync(int rowIndex, TValue record, string? editField = null, bool beginEdit = true)
    {
        var list = GetMutableDataSource();
        var insertIndex = Math.Clamp(rowIndex, 0, list.Count);
        list.Insert(insertIndex, record);

        if (beginEdit && !string.IsNullOrWhiteSpace(editField))
        {
            await BeginEditCellAsync(record, editField);
        }
        else if (beginEdit)
        {
            await BeginEditFirstProgrammaticCellAsync(record);
        }
        else
        {
            await InvokeAsync(StateHasChanged);
        }

        return record;
    }

    public async Task DeleteRecordAsync(TValue record)
    {
        var list = DataSource as IList<TValue>;
        list?.Remove(record);
        var wasSelected = _selectedItems.Remove(record);
        await InvokeAsync(StateHasChanged);
        if (wasSelected) await NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
    }

    public async Task GroupByColumnAsync(string field)
    {
        await AddGroup(field);
        await InvokeAsync(StateHasChanged);
    }

    public async Task UngroupColumnAsync(string field)
    {
        await RemoveGroup(field);
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearGroupingAsync()
    {
        _groupDescriptors.Clear();
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task RefreshAsync() => await InvokeAsync(StateHasChanged);

    public Task Refresh() => RefreshAsync();

    public Task AutoFitColumnsAsync()
    {
        return Task.CompletedTask;
    }

    public async Task ExportAsync(GridExportFormat format, string? fileName = null, string title = "Export")
    {
        var table = BuildExportTable(title);
        var result = GridExporter.Export(table, format, fileName);
        await GridExporter.DownloadAsync(JsRuntime, result);
    }

    public GridExportResult CreateExport(GridExportFormat format, string? fileName = null, string title = "Export")
    {
        var table = BuildExportTable(title);
        return GridExporter.Export(table, format, fileName);
    }

    public Task ExportToCsvAsync(string fileName = "export.csv") =>
        ExportAsync(GridExportFormat.Csv, fileName);

    public Task ExportToTsvAsync(string fileName = "export.tsv") =>
        ExportAsync(GridExportFormat.Tsv, fileName);

    public Task ExportToXlsxAsync(string fileName = "export.xlsx") =>
        ExportAsync(GridExportFormat.Xlsx, fileName);

    public Task ExportToExcelAsync(string fileName = "export.xls") =>
        ExportToXlsAsync(fileName);

    public Task ExportToXlsAsync(string fileName = "export.xls") =>
        ExportAsync(GridExportFormat.Xls, fileName);

    public Task ExportToHtmlAsync(string fileName = "export.html") =>
        ExportAsync(GridExportFormat.Html, fileName);

    public Task ExportToJsonAsync(string fileName = "export.json") =>
        ExportAsync(GridExportFormat.Json, fileName);

    public Task ExportToPdfAsync(string title = "Export") =>
        ExportAsync(GridExportFormat.Pdf, "export.pdf", title);

    public Task ExportToPdfAsync(string fileName, string title) =>
        ExportAsync(GridExportFormat.Pdf, fileName, title);

    public Task ExportToPdfFileAsync(string fileName = "export.pdf", string title = "Export") =>
        ExportAsync(GridExportFormat.Pdf, fileName, title);

    private GridExportTable BuildExportTable(string title)
    {
        var table = new GridExportTable
        {
            Title = title,
            SheetName = title
        };

        var cols = VisibleColumns.ToList();
        foreach (var col in cols)
            table.Columns.Add(new GridExportColumn(col.DisplayHeader, textAlign: col.TextAlign));

        foreach (var item in SortedData)
            table.Rows.Add(new GridExportRow(cols.Select(col => GetCellDisplayValue(item, col))));

        return table;
    }

    private async Task EnsureTrailingNewRowIfNeededAsync()
    {
        if (!EnsureTrailingNewRow
            || _ensuringTrailingNewRow
            || EditSettingsRef?.AllowAdding != true
            || DataSource is not IList<TValue> list
            || Columns.Count == 0)
            return;

        if (_hasTrailingNewRowItem)
        {
            var markerIndex = FindTrailingNewRowIndex(list);
            if (markerIndex >= 0)
            {
                if (!IsTrackedTrailingNewRowStillBlank(list[markerIndex]))
                {
                    ClearTrailingNewRowMarker();
                }
                else
                {
                    if (markerIndex != list.Count - 1)
                    {
                        var marker = list[markerIndex];
                        list.RemoveAt(markerIndex);
                        list.Add(marker);
                        SyncDataSourceChangeTrackers();
                        await InvokeAsync(StateHasChanged);
                    }

                    return;
                }
            }

            ClearTrailingNewRowMarker();
        }

        var row = CreateNewItem();
        if (row is null)
            return;

        _ensuringTrailingNewRow = true;
        try
        {
            await AppendRowAsync(row, beginEdit: false);
            TrackTrailingNewRow(row);
            SyncDataSourceChangeTrackers();
        }
        finally
        {
            _ensuringTrailingNewRow = false;
        }
    }

    private bool IsTrackedTrailingNewRowStillBlank(TValue row)
    {
        if (IsTrailingNewRow == null)
            return true;

        try
        {
            return IsTrailingNewRow(row);
        }
        catch
        {
            return true;
        }
    }

    private int FindTrailingNewRowIndex(IList<TValue> list)
    {
        if (!_hasTrailingNewRowItem)
            return -1;

        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<TValue>.Default.Equals(list[i], _trailingNewRowItem!))
                return i;
        }

        return -1;
    }

    private void TrackTrailingNewRow(TValue row)
    {
        _trailingNewRowItem = row;
        _hasTrailingNewRowItem = true;
    }

    private void ClearTrailingNewRowMarker()
    {
        _trailingNewRowItem = default;
        _hasTrailingNewRowItem = false;
    }

    private IList<TValue> GetMutableDataSource()
    {
        if (DataSource is IList<TValue> list)
            return list;

        throw new InvalidOperationException(
            $"{GetType().Name} row insertion requires a mutable DataSource that implements IList<{typeof(TValue).Name}>.");
    }

    private async Task BeginEditFirstProgrammaticCellAsync(TValue row)
    {
        var rowIndex = ResolveRowIndex(row, -1);
        if (rowIndex < 0)
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (IsPagingActive && _pageState.PageSize > 0)
        {
            var targetPage = (rowIndex / _pageState.PageSize) + 1;
            if (targetPage != _pageState.CurrentPage)
                await GoToPage(targetPage);
        }

        var columns = VisibleColumns.ToList();
        for (var cellIndex = 0; cellIndex < columns.Count; cellIndex++)
        {
            var column = columns[cellIndex];
            if (!IsKeyboardEditTargetColumn(column))
                continue;

            if (!await TryActivateKeyboardEditTargetAsync(row, rowIndex, cellIndex, column, scrollIntoView: true))
                continue;

            if (_batchEditItem != null
                && string.Equals(_batchEditField, column.Field, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (CanToggleCheckboxColumn(column)
                || column.ShowEditButton
                || column.EffectiveTemplate != null
                || column.Commands is { Count: > 0 })
            {
                SyncDataSourceChangeTrackers();
                return;
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task SelectProgrammaticCellAsync(TValue item, string field)
    {
        var rowIndex = ResolveRowIndex(item, -1);
        if (rowIndex < 0)
            return;

        var visibleColumns = VisibleColumns.ToList();
        var cellIndex = visibleColumns.FindIndex(column =>
            string.Equals(column.Field, field, StringComparison.OrdinalIgnoreCase));
        if (cellIndex < 0)
            return;

        _selectedItems.Clear();
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
            _selectedItems.Add(item);
        _lastSelectedItem = item;
        _lastSelectedRowIndex = rowIndex;

        _selectedCells.Clear();
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
            _selectedCells.Add((rowIndex, cellIndex));
        _activeCell = (rowIndex, cellIndex);
        _lastSelectedCell = (rowIndex, cellIndex);

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            var value = GetPropertyValue(item, field);
            await EventsRef.CellSelected.InvokeAsync(new CellSelectEventArgs<TValue>
            {
                Data = item,
                RowIndex = rowIndex,
                CellIndex = cellIndex,
                CurrentValue = value
            });
        }

        if (SelectionSettingsRef?.Mode != SelectionMode.Cell
            && EventsRef?.RowSelected.HasDelegate == true)
        {
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue>
            {
                Data = item,
                RowIndex = rowIndex
            });
        }

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
    }

    public async Task EndEditAsync()
    {
        await CommitBatchEdit();
        _isEditing = false;
        _editingRowIndex = -1;
        _editItem = default;
        await InvokeAsync(StateHasChanged);
    }

    public Task ExpandAllGroupAsync()
    {
        _expandAllGroups = true;
        _allGroupsCollapsed = false;
        _collapsedGroupPaths.Clear();
        return InvokeAsync(StateHasChanged);
    }

    public async Task BeginEditCellAsync(TValue row, string field)
    {
        if (row == null || string.IsNullOrEmpty(field)) return;

        var col = VisibleColumns.FirstOrDefault(
            c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        if (col == null || !col.AllowEditing || col.IsPrimaryKey) return;

        var rowIndex = ResolveRowIndex(row, -1);
        if (rowIndex < 0) return;

        if (IsPagingActive && _pageState.PageSize > 0)
        {
            var targetPage = (rowIndex / _pageState.PageSize) + 1;
            if (targetPage != _pageState.CurrentPage)
                await GoToPage(targetPage);
        }

        await SelectProgrammaticCellAsync(row, col.Field);
        if (CanToggleCheckboxColumn(col))
        {
            await FocusGridHostAsync();
            SyncDataSourceChangeTrackers();
            await InvokeAsync(StateHasChanged);
            return;
        }

        await StartBatchEdit(row, rowIndex, col);

        if (_pendingBatchEditFocus && _batchEditField == col.Field)
            _pendingBatchEditScrollIntoView = true;

        SyncDataSourceChangeTrackers();

        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceChangeTrackers()
    {
        _lastSelectionDataSource = DataSource;
        _lastSelectionDataSourceSignature = ComputeSelectionDataSourceSignature();
        _selectionDataSourceCaptured = true;
        _lastFilterDataSource = DataSource;
        _filterDataSourceCaptured = true;
    }

    public async Task ScrollIntoViewAsync(int columnIndex, int rowIndex)
    {
        var visibleCols = VisibleColumns.ToList();
        if (columnIndex < 0 || columnIndex >= visibleCols.Count) return;

        var item = GetItemAtResolvedRowIndex(rowIndex);
        if (item == null) return;

        await BeginEditCellAsync(item, visibleCols[columnIndex].Field);
    }

    public Task<object?> GetCellValueByIndexAsync(int rowIndex, int columnIndex)
    {
        var data = DataSource?.ToList() ?? [];
        if (rowIndex < 0 || rowIndex >= data.Count)
        {
            return Task.FromResult<object?>(null);
        }

        var columns = Columns;
        if (columnIndex < 0 || columnIndex >= columns.Count)
        {
            return Task.FromResult<object?>(null);
        }

        var field = columns[columnIndex].Field;
        var item = data[rowIndex];
        return Task.FromResult(GetPropertyValue(item, field));
    }

    public Task UpdateCellAsync(int rowIndex, string field, object? value)
    {
        var data = DataSource as IList<TValue> ?? DataSource?.ToList();
        if (data == null || rowIndex < 0 || rowIndex >= data.Count)
        {
            return Task.CompletedTask;
        }

        var item = data[rowIndex];
        if (item == null)
        {
            return Task.CompletedTask;
        }

        if (!SetPropertyObjectValue(item, field, value))
        {
            return Task.CompletedTask;
        }

        return InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        try
        {
            if (_headerDragPreviewRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterHeaderDragPreview", _gridHostElement);
            }
            if (_rowDragSelectionAutoScrollRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterRowDragSelectionAutoScroll", _gridHostElement);
            }
            if (_gridKeyboardTrapRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridKeyboardTrap", _gridHostElement);
            }
        }
        catch (Exception)
        {
        }

        _headerDragPreviewRegistered = false;
        _rowDragSelectionAutoScrollRegistered = false;
        _gridKeyboardTrapRegistered = false;

        if (_gridJsModule != null)
        {
            try
            {
                await _gridJsModule.DisposeAsync();
            }
            catch (Exception)
            {
            }
            _gridJsModule = null;
        }

        _gridDotNetRef?.Dispose();
        _gridDotNetRef = null;
    }
}
