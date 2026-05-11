using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue> : IGridOwner
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

    // ── Injectables ─────────────────────────────────────────────────────
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    // ── Parameters ───────────────────────────────────────────────────────

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public int RowHeight { get; set; }

    // Feature flags
    [Parameter] public bool AllowSorting { get; set; }
    [Parameter] public bool AllowMultiSorting { get; set; }
    [Parameter] public bool AllowFiltering { get; set; }
    [Parameter] public bool AllowPaging { get; set; }
    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public bool AllowGrouping { get; set; }
    [Parameter] public bool AllowResizing { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;
    [Parameter] public bool EnableAltRow { get; set; } = true;
    [Parameter] public bool ShowSearchBar { get; set; }
    [Parameter] public GridLines GridLines { get; set; } = GridLines.Default;
    [Parameter] public List<string>? Toolbar { get; set; }

    /// <summary>
    /// Show the built-in grid toolbar (Expand All / Collapse All icons, plus any
    /// custom <see cref="Toolbar"/> items and the search bar). Defaults to
    /// <c>null</c> meaning auto-show whenever AllowGrouping is enabled or the
    /// caller supplied custom <see cref="Toolbar"/> items / <see cref="ShowSearchBar"/>.
    /// Set to <c>true</c> to force-show or <c>false</c> to force-hide.
    /// </summary>
    [Parameter] public bool? ShowGridToolbar { get; set; }

    /// <summary>
    /// Show the built-in Expand All icon button in the grid toolbar.
    /// Defaults to true when AllowGrouping is enabled.
    /// </summary>
    [Parameter] public bool ShowExpandAllButton { get; set; } = true;

    /// <summary>
    /// Show the built-in Collapse All icon button in the grid toolbar.
    /// Defaults to true when AllowGrouping is enabled.
    /// </summary>
    [Parameter] public bool ShowCollapseAllButton { get; set; } = true;

    // Resolved visibility. Render the toolbar when a caller has supplied items
    // or asked for a search bar (or explicitly forced it via ShowGridToolbar).
    // Grouping no longer auto-shows the toolbar — its expand/collapse pair was
    // redundant with the toggle in the group-drop area, so the toolbar bar
    // would otherwise render empty for grouping-only grids.
    internal bool ShouldRenderToolbar =>
        ShowGridToolbar ?? ((Toolbar is { Count: > 0 }) || ShowSearchBar);

    /// <summary>Initial group columns (field names).</summary>
    [Parameter] public List<string>? GroupColumns { get; set; }

    /// <summary>Aggregate row definitions for group footers and grid footer.</summary>
    [Parameter] public List<AggregateRow>? AggregateRows { get; set; }

    /// <summary>Show an Expand All / Collapse All toggle button in the group drop area.</summary>
    [Parameter] public bool ShowGroupExpandCollapse { get; set; } = true;

    /// <summary>When true, columns currently used for grouping are hidden from the data grid.</summary>
    [Parameter] public bool HideGroupedColumns { get; set; } = true;

    /// <summary>When true, the user can drag a column header onto another to
    /// reorder columns. Default true. (Drag-to-group is always wired separately
    /// through the group drop area when <see cref="AllowGrouping"/> is on.)</summary>
    [Parameter] public bool AllowColumnReorder { get; set; } = true;

    /// <summary>Optional schema for the Choose Columns dialog. Hosts that
    /// have a saved layout with columns the grid isn't currently rendering
    /// (e.g. legacy "Hidden=true" entries) pass them here so the dialog can
    /// list them with unchecked boxes. When null the dialog falls back to the
    /// rendered <c>&lt;GridColumn&gt;</c> children.</summary>
    [Parameter] public IEnumerable<ChooseColumnDescriptor>? AvailableColumns { get; set; }

    /// <summary>Fires when the user clicks OK in the Choose Columns dialog.
    /// When subscribed, the host is fully responsible for applying the new
    /// visibility/order to its layout system (and re-rendering the grid).
    /// When NOT subscribed, the grid falls back to its built-in behaviour:
    /// applies visibility overrides locally and reorders the underlying
    /// <c>GridColumnsBase._columns</c> list.</summary>
    [Parameter] public EventCallback<ChooseColumnsResult> OnColumnsChosen { get; set; }

    /// <summary>Fires whenever the user mutates a persistable aspect of the
    /// grid (column reorder, resize, group/ungroup, hide via header menu,
    /// rename via header menu). The argument is a snapshot of the grid's
    /// current state — column order, visibility, widths, header overrides,
    /// group columns. Hosts that own their layout via a service like
    /// <c>GridLayoutService</c> subscribe to this and persist on each event.
    ///
    /// <para>Distinct from <see cref="OnColumnsChosen"/>, which is specific
    /// to the Choose Columns dialog. <c>OnLayoutChanged</c> covers all
    /// other user mutations.</para></summary>
    [Parameter] public EventCallback<GridSettings> OnLayoutChanged { get; set; }

    /// <summary>Opt-in persistence key for this grid's user-modifiable state
    /// (column order, visibility, widths, header renames, group columns).
    /// When non-empty AND an <see cref="IGridSettingsStore"/> is registered
    /// in DI, the grid auto-loads on first render and auto-saves on every
    /// user manipulation.
    ///
    /// <para>Format: <c>{FormName}.{gridName}</c> with optional
    /// <c>.{instanceKey}</c> suffix — same convention as the legacy
    /// <c>AppGridLayout</c> table used by HomeFront's existing
    /// <c>GridLayoutService</c>. Examples: <c>"FAssembly.gComponents"</c>,
    /// <c>"FAssembly.gItems.(0)"</c>.</para></summary>
    [Parameter] public string? PersistenceKey { get; set; }

    // Resolved lazily from the service provider so the grid still works in
    // apps that haven't registered an IGridSettingsStore. A direct
    // `[Inject] IGridSettingsStore?` would force every consumer to register
    // one, throwing InvalidOperationException at render time when missing —
    // which is exactly the bug we hit after disabling the cache layer.
    [Inject] private IServiceProvider Services { get; set; } = default!;
    private IGridSettingsStore? GridSettingsStore =>
        Services?.GetService(typeof(IGridSettingsStore)) as IGridSettingsStore;

    /// <summary>True after the persisted settings (if any) have been loaded
    /// and applied for the current <see cref="PersistenceKey"/>. Suppressed
    /// to prevent firing a save during the initial apply.</summary>
    private bool _gridSettingsLoaded;
    private string? _gridSettingsLoadedKey;
    /// <summary>The most-recently loaded-or-saved settings. Re-applied to
    /// new column instances when the host rebuilds the grid mid-session
    /// (e.g. FAssembly bumping ItemsGridLayoutVersion after a Pricing
    /// Community change reloads the column dict from the database).</summary>
    private GridSettings? _lastAppliedSettings;
    /// <summary>Hash of the live column-Field sequence at the moment we last
    /// applied <see cref="_lastAppliedSettings"/>. When the actual sequence
    /// drifts from this — only happens when the host swaps in a new column
    /// list — we re-run the apply so the user's saved order/widths land on
    /// the new instances.</summary>
    private string? _lastAppliedColumnSignature;

    /// <summary>Show item count on group header rows.</summary>
    [Parameter] public bool ShowGroupCount { get; set; }

    // ── Chart View ────────────────────────────────────────────────────────
    // When ShowAsChart is true the grid replaces its normal table rendering
    // with a stack of mini bar-charts, one per data row. Each row's
    // ChartValueFields property values become its bars; ChartLabelField is
    // the row's label. The rest of the grid (toolbar, grouping bar, etc.)
    // stays untouched so consumers can still toggle sort / search / save.

    /// <summary>When true, render data rows as bar charts instead of a table.</summary>
    [Parameter] public bool ShowAsChart { get; set; }

    /// <summary>Property names whose numeric values become bars (one bar per field) in chart mode.</summary>
    [Parameter] public IList<string>? ChartValueFields { get; set; }

    /// <summary>Property name to use as each row's chart label. Falls back to the first visible column's field.</summary>
    [Parameter] public string? ChartLabelField { get; set; }

    /// <summary>Optional human-readable axis labels for each value field.</summary>
    [Parameter] public IList<string>? ChartValueLabels { get; set; }

    /// <summary>Bar color in chart mode. Defaults to brand blue.</summary>
    [Parameter] public string ChartBarColor { get; set; } = "#2563eb";

    /// <summary>Show numeric values above each bar in chart mode.</summary>
    [Parameter] public bool ChartShowValues { get; set; } = false;

    /// <summary>
    /// When true, all groups start collapsed on first render. Default is
    /// <c>false</c> — groups start expanded so the user immediately sees the
    /// detail rows under each grouping. Callers that prefer the collapsed-on-
    /// load summary view can set this to <c>true</c>.
    /// </summary>
    [Parameter] public bool DefaultGroupsCollapsed { get; set; } = false;

    /// <summary>
    /// Optional color applied to the *values* of the grouped column (the
    /// group-header row text and the chips in the grouping bar). Default is
    /// empty — the toolkit applies no color, so the value inherits whatever
    /// the surrounding row text uses. Consumers wire their brand color
    /// explicitly (FlexCore is a toolbox; brand colors are app concerns).
    /// </summary>
    [Parameter] public string GroupedColumnColor { get; set; } = "";

    /// <summary>
    /// Built-in preset for the group expand/collapse indicator on group-header
    /// rows. Pick one of <see cref="GroupExpandIconStyle.PlusMinus"/> or
    /// <see cref="GroupExpandIconStyle.Triangle"/>. To go beyond the presets,
    /// use the <see cref="ExpandIconTemplate"/> / glyph / style parameters
    /// below — those override this preset.
    /// </summary>
    [Parameter] public GroupExpandIconStyle GroupExpandIconStyle { get; set; } = GroupExpandIconStyle.PlusMinus;

    // ── Caller-supplied glyph/icon overrides ─────────────────────────────
    // These let consumers swap in any glyph or markup without changing the
    // toolkit. Resolution order (highest to lowest):
    //   1. ExpandIconTemplate    — full RenderFragment(bool isExpanded)
    //   2. CollapsedGlyph / ExpandedGlyph + ExpandIconStyle (string knobs)
    //   3. GroupExpandIconStyle preset (built-in default)

    /// <summary>
    /// Glyph rendered when a group is collapsed. Null falls back to the
    /// <see cref="GroupExpandIconStyle"/> preset ("+" or "▶").
    /// </summary>
    [Parameter] public string? CollapsedGlyph { get; set; }

    /// <summary>
    /// Glyph rendered when a group is expanded. Null falls back to the
    /// <see cref="GroupExpandIconStyle"/> preset ("−" or "▼").
    /// </summary>
    [Parameter] public string? ExpandedGlyph { get; set; }

    /// <summary>
    /// Inline CSS style applied to the expand/collapse glyph span. Null falls
    /// back to a sensible toolkit default for the chosen
    /// <see cref="GroupExpandIconStyle"/>.
    /// </summary>
    [Parameter] public string? ExpandIconStyle { get; set; }

    /// <summary>
    /// Optional RenderFragment that completely overrides the expand/collapse
    /// icon. Receives a bool indicating whether the group is currently
    /// expanded (true) or collapsed (false). The fragment is responsible for
    /// its own markup and click handling — typical use: render an &lt;i&gt;
    /// font-icon, an &lt;img&gt;, or any custom glyph.
    /// </summary>
    [Parameter] public RenderFragment<bool>? ExpandIconTemplate { get; set; }

    // Resolved values used by RenderGroupedRows.
    internal string ResolveCollapsedGlyph() =>
        CollapsedGlyph ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "+" : "▶");

    internal string ResolveExpandedGlyph() =>
        ExpandedGlyph ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "−" : "▼");

    internal string? ResolveExpandIconStyle() =>
        ExpandIconStyle ?? (GroupExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? HfGridIconStyles.PlusMinus
            : null);   // Triangle → no inline style; CSS class drives it

    // A lighter tint of GroupedColumnColor used for the chip background.
    // Uses color-mix() so any caller-supplied colour produces a sensible soft
    // tint without needing colour-space math in C#.
    private string GroupedColumnColorSoft => $"color-mix(in srgb, {GroupedColumnColor} 12%, white)";

    // Inline style for the host div. Only emits the GroupedColumnColor CSS
    // variables when the caller actually supplied a color — empty-string
    // assignments (e.g. "--fx-grid-group-color: ;") suppress the var() fallback
    // in the stylesheet, leaving the chip background unset and invisible.
    private string HostStyle
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(GroupedColumnColor))
            {
                sb.Append("--fx-grid-group-color:").Append(GroupedColumnColor).Append("; ");
                sb.Append("--fx-grid-group-color-soft:").Append(GroupedColumnColorSoft).Append("; ");
            }
            if (!string.IsNullOrEmpty(Height)) sb.Append("height:").Append(Height).Append("; ");
            if (!string.IsNullOrEmpty(Width))  sb.Append("width:").Append(Width).Append("; ");
            return sb.ToString();
        }
    }

    // Track whether we've already applied DefaultGroupsCollapsed so it only
    // fires once (first render) — after that the user controls collapse state.
    private bool _appliedDefaultCollapsed;

    // Child settings components
    [Parameter] public FilterSettings? FilterSettingsRef { get; set; }
    [Parameter] public PageSettings? PageSettingsRef { get; set; }
    [Parameter] public EditSettings? EditSettingsRef { get; set; }
    [Parameter] public SelectionSettings? SelectionSettingsRef { get; set; }
    [Parameter] public GridControlEvents<TValue>? EventsRef { get; set; }

    // Direct event callbacks (shorthand without EventsRef)
    [Parameter] public EventCallback<string> OnToolbarItemClick { get; set; }
    /// <summary>Factory used to create a new row item when adding rows.</summary>
    [Parameter] public Func<TValue>? NewItemFactory { get; set; }
    /// <summary>Factory used to clone an existing row before editing.</summary>
    [Parameter] public Func<TValue, TValue>? CloneFactory { get; set; }

    // ── Internal State ───────────────────────────────────────────────────

    internal GridColumnsBase? _columnsContainer;
    private readonly Dictionary<string, ColumnState> _columnStates = new();
    private readonly PageState _pageState = new();
    private readonly HashSet<TValue> _selectedItems = new();
    private readonly List<(int RowIndex, int CellIndex)> _selectedCells = new();
    private (int RowIndex, int CellIndex)? _activeCell;
    private bool _expandAllGroups;
    private bool _allGroupsCollapsed;
    private readonly HashSet<string> _collapsedGroupPaths = new(StringComparer.OrdinalIgnoreCase);
    private (int RowIndex, int CellIndex)? _lastSelectedCell;
    private int? _lastSelectedRowIndex;

    // Editing
    private bool _isEditing;
    private int _editingRowIndex = -1;
    private TValue? _editItem;

    // Batch editing (cell-level inline editing)
    private TValue? _batchEditItem;
    private int _batchEditRowIndex = -1;
    private string? _batchEditField;
    private string? _batchEditValue;

    // Filtering popup
    private string? _filterPopupField;

    // Type-ahead buffer (multi-select numeric input)
    private string _typeAheadBuffer = "";

    // Search
    private string? SearchText;
    private CancellationTokenSource? _searchCts;

    // ── Grouping State ───────────────────────────────────────────────────
    private readonly List<GroupDescriptor> _groupDescriptors = new();
    private string? _draggingColumnField;
    private string? _draggingGroupChipField;
    private bool _dragOverGroupArea;

    // ── Resize State ─────────────────────────────────────────────────────
    private GridColumn? _resizingCol;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private bool _autoWidthPending = true;

    // ── Computed Properties ──────────────────────────────────────────────

    private bool ShowCheckboxColumn =>
        SelectionSettingsRef?.CheckboxOnly == true ||
        VisibleColumns.Any(c => c.Type == ColumnType.CheckBox && string.IsNullOrEmpty(c.Field));

    private FilterType ResolvedFilterType =>
        FilterSettingsRef?.Type ?? FilterType.FilterBar;

    private int[] ResolvedPageSizes =>
        PageSettingsRef?.PageSizes ?? [5, 10, 20, 50];

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

    private int GroupedPlaceholderCount =>
        (AllowGrouping && HideGroupedColumns) ? _groupDescriptors.Count : 0;

    private int TotalColumnCount =>
        VisibleColumns.Count()
        + (ShowCheckboxColumn ? 1 : 0)
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

    // ── Data Pipeline: DataSource → Filter → Search → Sort → Page ────────

    private IEnumerable<TValue> FilteredData
    {
        get
        {
            var data = DataSource ?? Enumerable.Empty<TValue>();

            // Apply column filters
            foreach (var kvp in _columnStates)
            {
                var colField = kvp.Key;
                var state = kvp.Value;

                if (!string.IsNullOrEmpty(state.FilterValue))
                {
                    var filterVal = state.FilterValue;
                    data = data.Where(item =>
                    {
                        var val = GetPropertyValue(item, colField)?.ToString() ?? "";
                        return val.Contains(filterVal, StringComparison.OrdinalIgnoreCase);
                    });
                }

                if (state.CheckedFilterValues.Count > 0)
                {
                    var checkedVals = state.CheckedFilterValues;
                    data = data.Where(item =>
                    {
                        var val = GetPropertyValue(item, colField)?.ToString() ?? "";
                        return checkedVals.Contains(val);
                    });
                }
            }

            // Apply global search
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

            if (!AllowPaging)
                return data;

            return data
                .Skip((_pageState.CurrentPage - 1) * _pageState.PageSize)
                .Take(_pageState.PageSize);
        }
    }

    // ── Grouped Data ─────────────────────────────────────────────────────

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

                // Compute aggregates for this group across all its items (including sub-groups)
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

        // NOTE: We deliberately do NOT reset _expandAllGroups / _allGroupsCollapsed
        // here. Doing so caused two bugs:
        //
        //  1. DefaultGroupsCollapsed appeared to do nothing — OnParametersSet sets
        //     _allGroupsCollapsed=true, but the FIRST BuildGroups runs against the
        //     not-yet-loaded data (empty), so ApplyGroupCollapseState has no groups
        //     to mark, _collapsedGroupPaths stays empty, and the reset then wipes
        //     the flag. When data finally arrived, both the flag and the path set
        //     were empty → groups rendered expanded.
        //
        //  2. The toggle button in the group drop area read _allGroupsCollapsed to
        //     decide its icon and toggle branch. With the flag wiped every render,
        //     it always saw "false" and always took the "collapse" branch — so the
        //     toolbar Collapse All click stuck and expand-all became unreachable.
        //
        // Individual toggles (ToggleGroupCollapse) clear the bulk flags themselves
        // when the user expands/collapses a single group, so the flags are now
        // managed exclusively by the explicit user actions and the lifecycle.
        return groups;
    }

    /// <summary>
    /// Compute aggregate values for a list of items based on AggregateRows config.
    /// </summary>
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

    /// <summary>
    /// Format an aggregate value using the column's format and template.
    /// </summary>
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

    /// <summary>
    /// Toggle expand/collapse all groups.
    /// </summary>
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

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override void OnInitialized()
    {
        _pageState.PageSize = PageSettingsRef?.PageSize ?? 10;

        // Apply initial group columns
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

    // Tracks the GroupColumns reference we last synced into _groupDescriptors
    // so the existing OnParametersSet can detect a fresh list (host reloaded
    // its layout) without confusing it with the stable reference held during
    // a session.
    private List<string>? _lastSyncedGroupColumns;

    private void SyncGroupDescriptorsFromParameter()
    {
        // Re-sync _groupDescriptors when the host swaps in a new GroupColumns
        // list (e.g. FAssembly's LoadItemsGridLayoutAsync rebuilds it from the
        // freshly-loaded dict after a Pricing Community / Show Pricing change).
        // Without this, _groupDescriptors keeps its stale state from before the
        // reload and the saved grouping silently disappears even though the
        // database had it persisted correctly. Reference-equality check skips
        // the sync during a normal session — drag-to-group mutates
        // _groupDescriptors locally and the host doesn't reassign GroupColumns
        // until the next reload, so the reference stays the same and we don't
        // clobber the user's in-progress changes.
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
        // Capture the columns container from child content
        if (firstRender)
        {
            // Re-apply initial group columns now that columns are loaded
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

        // Once columns have rendered for the first time after a new
        // PersistenceKey is supplied, pull the saved settings.
        if (!string.IsNullOrEmpty(PersistenceKey)
            && PersistenceKey != _gridSettingsLoadedKey
            && _columnsContainer != null
            && Columns.Count > 0
            && GridSettingsStore != null)
        {
            _gridSettingsLoadedKey = PersistenceKey;
            await LoadGridSettingsAsync();
        }
        // After every render: if the host swapped in a new column list (e.g.
        // FAssembly bumped ItemsGridLayoutVersion after a Pricing Community
        // change), re-apply our cached settings so user customizations don't
        // get lost mid-session.
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
    }

    /// <summary>Pulls the saved <see cref="GridSettings"/> for the current
    /// <see cref="PersistenceKey"/> and applies them: column order, visibility
    /// overrides, header overrides, runtime widths, group descriptors. Sets
    /// <see cref="_gridSettingsLoaded"/> at the end so subsequent user
    /// mutations trigger a save (this initial apply must NOT save itself).</summary>
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
            // Don't take down the grid if persistence fails — just behave as
            // if no saved settings existed. Mark loaded so saves still fire
            // for ongoing user mutations.
            _gridSettingsLoaded = true;
        }
    }

    /// <summary>Walks <see cref="GridSettings"/> and applies each piece to
    /// the current column instances. Shared between the initial load and the
    /// post-rebuild re-apply path.</summary>
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

    /// <summary>Re-applies the cached saved settings whenever the host
    /// rebuilds the columns (a fresh <c>&lt;GridColumnsBase&gt;</c> keyed on a
    /// version counter — e.g. FAssembly's <c>ItemsGridLayoutVersion</c> after
    /// a Pricing Community change reloads the layout dict from the database).
    /// New <see cref="GridColumn"/> instances arrive in the host's default
    /// order with no <c>RuntimeWidth</c>; without this pass the user's
    /// last-saved order/widths would be silently lost mid-session.</summary>
    private void ReapplyAfterRebuildIfNeeded()
    {
        if (!_gridSettingsLoaded || _lastAppliedSettings == null || Columns.Count == 0)
            return;
        var sig = ComputeColumnSignature();
        if (sig == _lastAppliedColumnSignature) return;
        ApplyLoadedSettings(_lastAppliedSettings);
        // After ReorderColumns the signature has changed again — capture
        // the post-apply value so we don't loop on the next render.
        _lastAppliedColumnSignature = ComputeColumnSignature();
        StateHasChanged();
    }

    /// <summary>Variant of <see cref="SaveGridSettingsAsync"/> that takes the
    /// post-mutation column list as an explicit snapshot rather than reading
    /// the live <see cref="Columns"/> collection. Used after
    /// <see cref="OnColumnsChosen"/> fires, where the host's update is async
    /// (a version bump that triggers Blazor to rebuild the columns) and the
    /// live collection still reflects the pre-OK state.</summary>
    private async Task SaveSnapshotSettingsAsync(List<ChooseColumnDescriptor> snapshot)
    {
        if (!_gridSettingsLoaded || string.IsNullOrEmpty(PersistenceKey) || GridSettingsStore == null)
            return;

        var settings = new GridSettings
        {
            ColumnOrder     = snapshot.Select(c => c.Field).Where(f => !string.IsNullOrEmpty(f)).ToList(),
            Visibility      = snapshot.ToDictionary(c => c.Field, c => c.Visible),
            // Carry forward any existing user state for fields the dialog
            // didn't touch — widths, header overrides, group columns.
            Widths          = Columns.Where(c => c.RuntimeWidth.HasValue && !string.IsNullOrEmpty(c.Field))
                                     .ToDictionary(c => c.Field, c => c.RuntimeWidth!.Value),
            HeaderOverrides = _headerOverrides.Count > 0 ? new Dictionary<string, string>(_headerOverrides) : null,
            GroupColumns    = _groupDescriptors.Select(g => g.Field).ToList()
        };
        _lastAppliedSettings = settings;
        // Don't capture column signature here — the host's column rebuild is
        // async, so the live signature will diverge in a moment and trigger
        // ReapplyAfterRebuildIfNeeded as intended.
        try { await GridSettingsStore.SaveAsync(PersistenceKey, settings); }
        catch (Exception) { /* persistence shouldn't surface errors */ }
    }

    /// <summary>Writes the grid's current user-modifiable state to the
    /// configured store. No-op when persistence is off or the initial load
    /// hasn't happened yet (we don't want to save the unloaded blank state
    /// over a previously-saved one).</summary>
    /// <summary>Builds a snapshot of every persistable aspect of the grid's
    /// current state. Shared by the IGridSettingsStore save path and the
    /// <see cref="OnLayoutChanged"/> event so both stay in sync.</summary>
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

    /// <summary>Fires <see cref="OnLayoutChanged"/> with the current snapshot
    /// when a host has subscribed. Called from every user-mutation handler
    /// other than <see cref="ChooseColumnsOk"/> (which uses
    /// <see cref="OnColumnsChosen"/> with the dialog snapshot instead, since
    /// the host's rebuild is async at that point).</summary>
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
        // Refresh the cache so the post-rebuild re-apply uses the latest
        // user state (otherwise a host-triggered rebuild after a user mutation
        // would re-apply pre-mutation state from the previous load).
        _lastAppliedSettings = settings;
        _lastAppliedColumnSignature = ComputeColumnSignature();
        try
        {
            await GridSettingsStore.SaveAsync(PersistenceKey, settings);
        }
        catch (Exception)
        {
            // Persistence failures shouldn't reach the user — the worst case
            // is the user's customization doesn't survive next session.
        }
    }

    protected override void OnParametersSet()
    {
        _pageState.PageSize = PageSettingsRef?.PageSize ?? _pageState.PageSize;
        _autoWidthPending = true;

        // Apply DefaultGroupsCollapsed once on first parameter set so all groups
        // start collapsed. Subsequent parameter changes won't re-collapse — the
        // user's manual expand/collapse state takes over.
        if (DefaultGroupsCollapsed && !_appliedDefaultCollapsed)
        {
            _allGroupsCollapsed = true;
            _expandAllGroups = false;
            _appliedDefaultCollapsed = true;
        }

        SyncGroupDescriptorsFromParameter();
    }

    // ── Sorting ──────────────────────────────────────────────────────────

    private async Task HandleSort(GridColumn col)
    {
        if (!AllowSorting || !col.AllowSorting || string.IsNullOrEmpty(col.Field))
            return;

        var state = GetColumnState(col.Field);

        // Fire Sorting event
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

        // Clear other sorts unless multi-sort
        if (!AllowMultiSorting)
        {
            foreach (var kvp in _columnStates)
                if (kvp.Key != col.Field)
                    kvp.Value.SortDirection = null;
        }

        // Toggle
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

    // ── Filtering ────────────────────────────────────────────────────────

    private async Task ApplyFilter(string field, string? value)
    {
        var state = GetColumnState(field);

        if (EventsRef?.Filtering.HasDelegate == true)
        {
            var args = new FilterEventArgs { Field = field, Value = value };
            await EventsRef.Filtering.InvokeAsync(args);
            if (args.Cancel) return;
        }

        state.FilterValue = string.IsNullOrWhiteSpace(value) ? null : value;
        _pageState.CurrentPage = 1;

        if (EventsRef?.Filtered.HasDelegate == true)
            await EventsRef.Filtered.InvokeAsync(new FilterEventArgs { Field = field, Value = value });
    }

    private void ToggleFilterPopup(string field)
    {
        _filterPopupField = _filterPopupField == field ? null : field;
    }

    private void CloseFilterPopup() => _filterPopupField = null;

    private void ToggleCheckboxFilter(string field, string value)
    {
        var state = GetColumnState(field);
        if (!state.CheckedFilterValues.Remove(value))
            state.CheckedFilterValues.Add(value);
        _pageState.CurrentPage = 1;
    }

    private void ClearFilter(string field)
    {
        var state = GetColumnState(field);
        state.FilterValue = null;
        state.CheckedFilterValues.Clear();
        _pageState.CurrentPage = 1;
    }

    private List<string> GetDistinctValues(string field)
    {
        return (DataSource ?? Enumerable.Empty<TValue>())
            .Select(item => GetPropertyValue(item, field)?.ToString() ?? "")
            .Distinct()
            .OrderBy(v => v)
            .ToList();
    }

    // ── Search ───────────────────────────────────────────────────────────

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

    // ── Paging ───────────────────────────────────────────────────────────

    private async Task GoToPage(int page)
    {
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

    private IEnumerable<int> GetPageNumbers()
    {
        var total = _pageState.TotalPages;
        var current = _pageState.CurrentPage;
        var count = PageSettingsRef?.PageCount ?? 5;

        var start = Math.Max(1, current - count / 2);
        var end = Math.Min(total, start + count - 1);
        start = Math.Max(1, end - count + 1);

        return Enumerable.Range(start, end - start + 1);
    }

    // ── Selection ────────────────────────────────────────────────────────

    private async Task HandleRowClick(TValue item, int rowIndex, MouseEventArgs? mouseArgs = null)
    {
        // Commit any in-progress batch cell edit when clicking away
        await CommitBatchEdit();

        if (EventsRef?.OnRecordClick.HasDelegate == true)
            await EventsRef.OnRecordClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex });

        if (!AllowSelection) return;
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell && SelectionSettingsRef?.CheckboxOnly != true)
            return;

        await SelectRow(item, rowIndex, mouseArgs);
    }

    private async Task HandleRowDblClick(TValue item, int rowIndex)
    {
        if (EventsRef?.OnRecordDoubleClick.HasDelegate == true)
            await EventsRef.OnRecordDoubleClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex });

        if (EditSettingsRef?.AllowEditing == true && EditSettingsRef.AllowEditOnDblClick)
            await StartEdit(item, rowIndex);
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
                // Plain click (no modifiers):
                //  - If clicking on a row that is already part of a multi-selection,
                //    preserve the selection so the user can edit the cell and have
                //    the change propagate to all selected rows.  Selection will be
                //    cleared when the edit is committed (Enter / OnCellSave).
                //  - Otherwise, clear previous selection and select only clicked row
                //    (standard Excel / file-explorer behavior).
                if (_selectedItems.Count > 1 && _selectedItems.Contains(item))
                {
                    // Keep multi-selection intact — user clicked an already-selected row
                }
                else
                {
                    var wasSelected = _selectedItems.Contains(item);
                    _selectedItems.Clear();
                    if (!wasSelected || SelectionSettingsRef?.EnableToggle != true)
                        _selectedItems.Add(item);
                }
            }
            else if (isShift && _lastSelectedRowIndex.HasValue)
            {
                // Range selection: select all rows between last selected and current
                var allData = PagedData.ToList();
                var resolvedCurrent = ResolveRowIndex(item, rowIndex);
                var start = Math.Min(_lastSelectedRowIndex.Value, resolvedCurrent);
                var end = Math.Max(_lastSelectedRowIndex.Value, resolvedCurrent);

                if (!isCtrl) _selectedItems.Clear();

                var sourceData = (DataSource as IList<TValue>) ?? (DataSource?.ToList() ?? new List<TValue>());
                for (var i = start; i <= end && i < sourceData.Count; i++)
                {
                    _selectedItems.Add(sourceData[i]);
                }
            }
            else if (isCtrl)
            {
                // Toggle single item
                if (!_selectedItems.Remove(item))
                    _selectedItems.Add(item);
            }
            else
            {
                if (!_selectedItems.Remove(item))
                    _selectedItems.Add(item);
            }
        }

        _lastSelectedRowIndex = ResolveRowIndex(item, rowIndex);

        if (EventsRef?.RowSelected.HasDelegate == true)
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex });
    }

    private async Task HandleCellClick(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        if (!AllowSelection)
            return;

        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        SetActiveEditableCell(resolvedRowIndex, cellIndex);

        // Commit any in-progress batch cell edit when clicking a different cell.
        // (StartBatchEdit also calls CommitBatchEdit internally, but this covers
        // clicks on non-editable cells that wouldn't reach StartBatchEdit.)
        await CommitBatchEdit();

        // Determine if the clicked cell is editable (batch mode)
        var clickedCol = VisibleColumns.ElementAtOrDefault(cellIndex);
        var isEditableCell = EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && clickedCol != null
            && clickedCol.AllowEditing
            && !clickedCol.IsPrimaryKey
            && !string.IsNullOrEmpty(clickedCol.Field);

        // Handle row selection for Row-mode grids.
        // Cell clicks use stopPropagation so the <tr> onclick (HandleRowClick)
        // does NOT fire — we must handle selection here.
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell &&
            SelectionSettingsRef?.CheckboxOnly != true)
        {
            // If clicking an editable cell on a row that's already part of a
            // multi-selection, preserve the selection for mass-editing.
            // Selection will be cleared when the edit is committed.
            var preserveSelection = isEditableCell
                && _selectedItems.Count > 1
                && _selectedItems.Contains(item);

            if (!preserveSelection)
            {
                if (EventsRef?.OnRecordClick.HasDelegate == true)
                    await EventsRef.OnRecordClick.InvokeAsync(new CellClickEventArgs<TValue> { Data = item, RowIndex = rowIndex });

                await SelectRow(item, rowIndex, args);
            }
        }

        // Start batch edit on single click so the cursor appears immediately.
        if (isEditableCell)
        {
            await StartBatchEdit(item, resolvedRowIndex, clickedCol!);
            return;
        }

        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
            return;

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

        _lastSelectedCell = (resolvedRowIndex, cellIndex);

        if (EventsRef?.CellSelected.HasDelegate == true)
        {
            var value = GetPropertyValue(item, Columns.Count > cellIndex ? Columns[cellIndex].Field : "");
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
    }

    private void SetActiveEditableCell(int rowIndex, int cellIndex)
    {
        var col = VisibleColumns.ElementAtOrDefault(cellIndex);
        if (col != null && col.AllowEditing)
            _activeCell = (rowIndex, cellIndex);
        else
            _activeCell = null;
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

    private async Task ToggleRowSelection(TValue item, int rowIndex)
    {
        await SelectRow(item, rowIndex);
    }

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        if (AllRowsSelected)
            _selectedItems.Clear();
        else
            foreach (var item in PagedData)
                _selectedItems.Add(item);
    }

    // ── Editing ──────────────────────────────────────────────────────────

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
            // Update existing
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
                var actualIdx = AllowPaging
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
            // Add new
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
            // In a real implementation, show a confirmation dialog.
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

    // ── Batch Cell Editing ─────────────────────────────────────────────

    private async Task StartBatchEdit(TValue item, int rowIndex, GridColumn col)
    {
        if (!col.AllowEditing || string.IsNullOrEmpty(col.Field) || col.IsPrimaryKey) return;
        if (EditSettingsRef?.AllowEditing != true || EditSettingsRef.Mode != EditMode.Batch) return;

        // Save previous edit if any
        await CommitBatchEdit();

        if (EventsRef?.OnCellEdit.HasDelegate == true)
        {
            var args = new CellEditArgs<TValue> { Data = item, ColumnName = col.Field };
            await EventsRef.OnCellEdit.InvokeAsync(args);
        }

        _batchEditItem = item;
        _batchEditRowIndex = ResolveRowIndex(item, rowIndex);
        _batchEditField = col.Field;
        _batchEditValue = GetPropertyValue(item, col.Field)?.ToString() ?? "";
    }

    private async Task CommitBatchEdit()
    {
        if (_batchEditItem == null || string.IsNullOrEmpty(_batchEditField)) return;

        var item = _batchEditItem;
        var field = _batchEditField;
        var newValue = _batchEditValue ?? "";

        // Compare via raw object value, not stringified — formatted display
        // ("5.00") would fail string-equality against the user's typed "5"
        // even though they're the same number. We only skip the property write
        // when the values are genuinely equivalent.
        var oldValueObj = GetPropertyValue(item, field);
        var oldValueStr = oldValueObj?.ToString() ?? "";
        var changed = !string.Equals(oldValueStr, newValue, StringComparison.Ordinal);

        Console.WriteLine($"[GridControl.CommitBatchEdit] field='{field}' old='{oldValueStr}' new='{newValue}' changed={changed} hasOnCellSave={EventsRef?.OnCellSave.HasDelegate == true}");

        if (changed)
            SetPropertyValue(item, field, newValue);

        // Always fire OnCellSave when the user explicitly committed (Enter /
        // Tab / blur). The host handler is the source of truth for "is this
        // worth marking dirty" — not us. Without this, an Enter on a value
        // that string-formats the same as before silently does nothing and
        // the user thinks the edit was lost.
        if (EventsRef?.OnCellSave.HasDelegate == true)
        {
            await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
            {
                Data = item,
                ColumnName = field,
                Value = newValue
            });
        }

        _batchEditItem = default;
        _batchEditRowIndex = -1;
        _batchEditField = null;
        _batchEditValue = null;
    }

    private void UpdateBatchEditValue(ChangeEventArgs e)
    {
        _batchEditValue = e.Value?.ToString() ?? "";
    }

    private async Task HandleBatchEditKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" || e.Key == "Tab" || e.Key == "NumpadEnter")
        {
            await CommitBatchEdit();
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Key == "Escape")
        {
            // Cancel edit without saving
            _batchEditItem = default;
            _batchEditRowIndex = -1;
            _batchEditField = null;
            _batchEditValue = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private bool IsBatchEditing(TValue item, string field)
    {
        if (_batchEditItem == null || _batchEditField != field) return false;
        return EqualityComparer<TValue>.Default.Equals(_batchEditItem, item);
    }

    // ── Toolbar ──────────────────────────────────────────────────────────

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
                await ExportToExcelAsync();
                break;
            case "pdf":
                await ExportToPdfAsync();
                break;
        }

        if (OnToolbarItemClick.HasDelegate)
            await OnToolbarItemClick.InvokeAsync(item);
    }

    // ── Keyboard Navigation ──────────────────────────────────────────────

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && _isEditing)
        {
            CancelEdit();
            return;
        }

        // Type-ahead: when multiple rows are selected and no batch edit is active,
        // let user type digits and press Enter to apply the value.
        if (_selectedItems.Count > 1 && _batchEditItem == null)
        {
            if (e.Key.Length == 1 && (char.IsDigit(e.Key[0]) || e.Key[0] == '.'))
            {
                // Prevent multiple decimal points
                if (e.Key == "." && _typeAheadBuffer.Contains('.'))
                    return;
                _typeAheadBuffer += e.Key;
                return;
            }

            if (e.Key == "Backspace" && _typeAheadBuffer.Length > 0)
            {
                _typeAheadBuffer = _typeAheadBuffer[..^1];
                return;
            }

            if (e.Key == "Escape" && _typeAheadBuffer.Length > 0)
            {
                _typeAheadBuffer = "";
                return;
            }

            if (e.Key == "Enter" && _typeAheadBuffer.Length > 0)
            {
                if (EventsRef?.OnTypeAheadCommit.HasDelegate == true)
                {
                    await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
                    {
                        SelectedItems = _selectedItems.ToList(),
                        Value = _typeAheadBuffer
                    });
                }
                _typeAheadBuffer = "";
                return;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ── DRAG & DROP GROUPING ─────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════

    private void StartColumnDrag(string colField)
    {
        _draggingColumnField = colField;
        _draggingGroupChipField = null;
    }

    private void EndColumnDrag()
    {
        _draggingColumnField = null;
        _dragOverGroupArea = false;
    }

    /// <summary>Handles a drop on a header cell — reorders the dragged column
    /// to land immediately before the target column. Wired to each header's
    /// <c>@ondrop</c>; the matching <c>@ondragover:preventDefault</c> on those
    /// cells lets the drop event fire.</summary>
    private async Task HandleHeaderDrop(string targetField)
    {
        if (!AllowColumnReorder) return;
        var fromField = _draggingColumnField;
        if (string.IsNullOrEmpty(fromField)) return;
        if (string.Equals(fromField, targetField, StringComparison.Ordinal)) return;
        if (_columnsContainer == null) return;
        if (_columnsContainer.ReorderColumn(fromField, targetField))
        {
            StateHasChanged();
            await SaveGridSettingsAsync();
            await FireLayoutChangedAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ── HEADER CONTEXT MENU ──────────────────────────────────────────────
    // Right-click on a column header opens a small menu offering grouping,
    // expand/collapse, hide-this-column, insert-a-column (from hidden ones),
    // rename-this-column, plus print and save-as. Generic across all grids.
    // ══════════════════════════════════════════════════════════════════════

    private bool _showHeaderContextMenu;
    private string _headerContextMenuField = "";
    private double _headerContextMenuX;
    private double _headerContextMenuY;
    // Reserved for the upcoming "insert column" submenu (currently the parent
    // header-menu item triggers HeaderMenuInsertColumn directly without a
    // submenu; this flag will gate the submenu UI when that lands).
#pragma warning disable CS0414
    private bool _showInsertColumnSubmenu;
#pragma warning restore CS0414
    private bool _showRenameColumn;
    private string _renameColumnDraft = "";

    /// <summary>Per-grid map of caller-supplied HeaderText overrides applied
    /// at runtime via the "Rename this column" menu item.</summary>
    private readonly Dictionary<string, string> _headerOverrides =
        new(StringComparer.Ordinal);

    /// <summary>Per-grid map of runtime visibility overrides applied via the
    /// header menu's Hide / Insert items. Keyed by Field. Mutating the
    /// underlying <see cref="GridColumn.Visible"/> [Parameter] directly is
    /// futile because Blazor resets it from the consumer's Razor template on
    /// each parent re-render — this map survives those resets.</summary>
    private readonly Dictionary<string, bool> _visibilityOverrides =
        new(StringComparer.Ordinal);

    /// <summary>True when the column should render — checks the runtime
    /// override first, falls back to the column's declared <c>Visible</c>.</summary>
    internal bool IsColumnVisible(GridColumn col)
        => _visibilityOverrides.TryGetValue(col.Field, out var ov) ? ov : col.Visible;

    /// <summary>Right-click on a column header opens the context menu.</summary>
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
            // Can't hide the last visible column — grids need at least one.
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

    /// <summary>Toggles grouping on the right-clicked column.</summary>
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
        // Override the column's [Parameter] Visible — see _visibilityOverrides
        // doc comment for why we don't mutate col.Visible directly.
        _visibilityOverrides[col.Field] = false;
        StateHasChanged();
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private void HeaderMenuToggleInsertSubmenu()
    {
        // Legacy submenu was a quick "show one of these hidden columns" picker.
        // Replaced by the Choose Columns dialog (HeaderMenuOpenChooseColumns)
        // which lists ALL columns (visible + hidden) per the legacy VB6 UX.
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

    // ── Choose Columns dialog ──────────────────────────────────────────
    // Mirrors the legacy VB6 dialog — a checkbox list of every column with
    // Move Up / Move Down / Show / Hide / Show All / Hide All / Restore
    // Default Layout actions and OK/Cancel commit.

    /// <summary>One row in the Choose Columns dialog — a working copy that the
    /// user mutates before pressing OK.</summary>
    private sealed class ChooseColumnRow
    {
        public string Field { get; init; } = "";
        public string Header { get; init; } = "";
        public bool Visible { get; set; }
    }

    private bool _showChooseColumnsDialog;
    private List<ChooseColumnRow> _chooseColumnsRows = new();
    private string _chooseColumnsSelectedField = "";

    /// <summary>Snapshot of the column order and visibility taken the first time
    /// any Choose Columns dialog opens — drives "Restore Default Layout".</summary>
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

        // Prefer the host-supplied AvailableColumns schema when present — that
        // includes legacy hidden columns the grid isn't currently rendering.
        // Fall back to whatever <GridColumn> children are wired up.
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
            // Host owns the visual layout (e.g. FAssembly with its saved-layout
            // dict + version bump that triggers a rebuild). Hand off, then ALSO
            // persist via the configured store so the user's pick survives
            // logout — using the dialog SNAPSHOT directly because the host's
            // re-render is async and the live `Columns` collection still
            // reflects the pre-OK order at this moment.
            await OnColumnsChosen.InvokeAsync(new ChooseColumnsResult { Columns = snapshot });
            await SaveSnapshotSettingsAsync(snapshot);
        }
        else
        {
            // Built-in path for grids that just declare their columns inline:
            // apply visibility overrides + reorder the underlying column list.
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
        try { await JsRuntime.InvokeVoidAsync("window.print"); } catch { /* JS not available */ }
    }

    private async Task HeaderMenuSaveAs()
    {
        CloseHeaderContextMenu();
        // Default: trigger the browser print dialog so the user can pick "Save as PDF".
        // Matches the "Save As..." convention from the legacy app. Consumers that
        // want a richer export (CSV/Excel) can override by intercepting in their
        // toolbar, since this menu is generic across all grids.
        try { await JsRuntime.InvokeVoidAsync("window.print"); } catch { /* JS not available */ }
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
            // Re-ordering groups (simple: move to end)
            var existing = _groupDescriptors.FirstOrDefault(g => g.Field == _draggingGroupChipField);
            if (existing != null)
            {
                _groupDescriptors.Remove(existing);
                _groupDescriptors.Add(existing);
            }
            _draggingGroupChipField = null;
        }
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

    /// <summary>Returns the index (within <paramref name="visibleCols"/>) of the
    /// first column that has an <see cref="AggregateColumn"/> configured by any
    /// of <paramref name="headerAggRows"/>; -1 if none. Used to compute the
    /// shrunk colspan for the group label cell so the column-aligned aggregate
    /// cells can sit on the same row.</summary>
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

    /// <summary>Removes every active group descriptor — wired to the "Clear"
    /// button shown alongside the chips when at least one group is active.</summary>
    private async Task ClearAllGroupsAsync()
    {
        if (_groupDescriptors.Count == 0) return;
        // Snapshot first — RemoveGroup mutates the list during iteration.
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

        // Once the user toggles any individual group, the global collapse-all /
        // expand-all flags must release — otherwise the next render's
        // ApplyGroupCollapseState call would force every group back to the
        // global state and the user's click would silently revert. Per-group
        // state from _collapsedGroupPaths takes over from here.
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

    // ── Render Grouped Rows ──────────────────────────────────────────────

    private RenderFragment RenderGroupedRows(IEnumerable<GroupResult<TValue>> groups, int level) => builder =>
    {
        foreach (var group in groups)
        {
            // Group header row
            builder.OpenElement(0, "tr");
            builder.AddAttribute(1, "class", "fx-group-header-row");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => ToggleGroupCollapse(group)));

            // Indent cells for nesting
            for (int i = 0; i < level; i++)
            {
                builder.OpenElement(10, "td");
                builder.AddAttribute(11, "class", "fx-cell fx-group-indent");
                builder.AddAttribute(12, "style", "width:32px;");
                builder.CloseElement();
            }

            // Expand/collapse + group header.
            // The label cell normally spans every remaining column (colspan = TotalColumnCount - level).
            // When ShowInGroupHeader aggregates are configured we SHRINK that colspan so the
            // aggregate <td>s emitted further down can sit on the same <tr> aligned with their
            // data columns. The label cell still covers everything to the LEFT of the first
            // aggregate column (indent + grouped placeholders + leading non-aggregate columns).
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

            // Expand/collapse glyph. Three layers of customization:
            //   1. ExpandIconTemplate (RenderFragment<bool>) — caller-supplied,
            //      ultimate override (any markup, including <img>, font icons)
            //   2. CollapsedGlyph / ExpandedGlyph + ExpandIconStyle — string
            //      knobs to swap the glyph and/or its inline CSS
            //   3. GroupExpandIconStyle preset (PlusMinus / Triangle) — the
            //      built-in defaults if neither of the above is set
            //
            // Inline styles are used (vs external CSS class) because the icon
            // span is emitted via RenderTreeBuilder, where Blazor's
            // component-scoped CSS attribute is not reliably applied — inline
            // styles always win.

            if (ExpandIconTemplate != null)
            {
                // Caller takes full responsibility for markup and click handling.
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

            // The grouped column's VALUE is rendered as a plain span, with an
            // optional inline color override when the consumer set
            // GroupedColumnColor (e.g. the app's brand blue). When that
            // parameter is empty (toolkit default) no color is applied — the
            // value inherits from the surrounding row text color.
            builder.OpenElement(40, "span");
            builder.AddAttribute(41, "class", "fx-group-header-value");
            if (!string.IsNullOrEmpty(GroupedColumnColor))
                builder.AddAttribute(42, "style", $"color:{GroupedColumnColor};");
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

            // Inline caption aggregates (e.g. "Sum: $1,234.56" next to group header)
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
                            builder.AddContent(57, FormatAggregateValue(aggCol, val, aggCol.GroupCaptionTemplate));
                            builder.CloseElement();
                        }
                    }
                }
            }

            builder.CloseElement(); // right container

            builder.CloseElement(); // td (label cell)

            // ── Inline column-aligned aggregates on the group header row ───
            // When any AggregateRow has ShowInGroupHeader=true, emit one <td>
            // per visible column from the first aggregate column onward —
            // all on the SAME <tr> as the group label. The label cell above
            // already used a SHRUNK colspan (computed below) so these cells
            // line up with their data columns. Cells without a configured
            // AggregateColumn stay empty.
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
                        builder.AddAttribute(157, "style", col.GetCellStyle());

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
                // If this group has sub-groups, recurse
                if (group.SubGroups.Any())
                {
                    builder.AddContent(60, RenderGroupedRows(group.SubGroups, level + 1));
                }
                else
                {
                    // Render actual data rows
                    var rowIdx = 0;
                    foreach (var item in group.Items)
                    {
                        var currentIdx = rowIdx;
                        var resolvedRowIdx = ResolveRowIndex(item, currentIdx);
                        var isSelected = _selectedItems.Contains(item);

                        builder.OpenElement(70, "tr");
                        builder.AddAttribute(71, "class",
                            $"fx-row {(rowIdx % 2 == 1 && EnableAltRow ? "fx-alt-row" : "")} {(isSelected ? "fx-selected" : "")} {(EnableHover ? "fx-hover" : "")}");
                        builder.AddAttribute(72, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleRowClick(item, currentIdx, e)));
                        if (RowHeight > 0)
                            builder.AddAttribute(73, "style", $"height:{RowHeight}px;");

                        // Grouped column placeholders (align data columns when grouped columns are hidden)
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

                        // Checkbox column
                        if (ShowCheckboxColumn)
                        {
                            builder.OpenElement(90, "td");
                            builder.AddAttribute(91, "class", "fx-cell fx-checkbox-cell");
                            builder.AddAttribute(92, "style", "width:50px;");
                            builder.OpenElement(93, "input");
                            builder.AddAttribute(94, "type", "checkbox");
                            builder.AddAttribute(95, "checked", isSelected);
                            builder.CloseElement();
                            builder.CloseElement();
                        }

                        // Data cells
                        var colIdx = 0;
                        foreach (var col in VisibleColumns)
                        {
                            var capturedColIdx = colIdx;
                            var capturedCol = col;
                            var capturedItemForEdit = item;
                            var isBatchEditing = IsBatchEditing(item, col.Field);
                            var isCellSelected = _selectedCells.Contains((resolvedRowIdx, capturedColIdx));
                            var isActiveCell = _activeCell.HasValue
                                && _activeCell.Value.RowIndex == resolvedRowIdx
                                && _activeCell.Value.CellIndex == capturedColIdx
                                && capturedCol.AllowEditing;
                            var editableClass = capturedCol.AllowEditing ? " fx-cell-editable" : string.Empty;
                            builder.OpenElement(100, "td");
                            builder.AddAttribute(101, "class",
                                (isCellSelected ? "fx-cell fx-cell-selected"
                                : isBatchEditing ? "fx-cell fx-batch-editing"
                                : isActiveCell ? "fx-cell fx-cell-active-editable"
                                : "fx-cell") + editableClass);
                            builder.AddAttribute(102, "style", col.GetCellStyle());

                            // Batch edit on double-click
                            if (EditSettingsRef?.Mode == EditMode.Batch && col.AllowEditing && !col.IsPrimaryKey && !string.IsNullOrEmpty(col.Field))
                            {
                                builder.AddAttribute(104, "ondblclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => StartBatchEdit(capturedItemForEdit, resolvedRowIdx, capturedCol)));
                            }
                            builder.AddAttribute(103, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, args => HandleCellClick(item, resolvedRowIdx, capturedColIdx, args)));
                            builder.AddEventStopPropagationAttribute(105, "onclick", true);

                            if (isBatchEditing)
                            {
                                var inputType = col.Type == ColumnType.Number ? "number"
                                    : col.Type == ColumnType.Date ? "date" : "text";
                                builder.OpenElement(145, "input");
                                builder.AddAttribute(146, "type", inputType);
                                builder.AddAttribute(147, "class", "fx-batch-input");
                                builder.AddAttribute(148, "value", _batchEditValue);
                                builder.AddAttribute(149, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, UpdateBatchEditValue));
                                builder.AddAttribute(150, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleBatchEditKeyDown));
                                builder.AddAttribute(151, "onblur", EventCallback.Factory.Create(this, CommitBatchEdit));
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
                                builder.OpenElement(130, "input");
                                builder.AddAttribute(131, "type", "checkbox");
                                builder.AddAttribute(132, "disabled", true);
                                builder.AddAttribute(133, "checked", GetBoolValue(item, col.Field));
                                builder.CloseElement();
                            }
                            else
                            {
                                builder.AddContent(140, GetCellDisplayValue(item, col));
                            }

                            builder.CloseElement(); // td
                            colIdx++;
                        }

                        builder.CloseElement(); // tr
                        rowIdx++;
                    }
                }

                // ── Group Footer (aggregate totals for this group) ──
                if (AggregateRows is { Count: > 0 } && group.Aggregates.Count > 0)
                {
                    var footerRows = AggregateRows.Where(r => r.ShowInGroupFooter).ToList();
                    foreach (var aggRow in footerRows)
                    {
                        builder.OpenElement(200, "tr");
                        builder.AddAttribute(201, "class", "fx-group-footer-row");

                        // Indent cells
                        for (int i = 0; i < _groupDescriptors.Count; i++)
                        {
                            builder.OpenElement(210, "td");
                            builder.AddAttribute(211, "class", "fx-cell fx-group-indent");
                            builder.AddAttribute(212, "style", "width:32px;");
                            builder.CloseElement();
                        }

                        // Render aggregate cells aligned with columns
                        foreach (var col in VisibleColumns)
                        {
                            var aggCol = aggRow.Columns.FirstOrDefault(a => a.Field == col.Field);
                            builder.OpenElement(220, "td");
                            builder.AddAttribute(221, "class", "fx-cell fx-aggregate-cell");
                            builder.AddAttribute(222, "style", col.GetCellStyle());

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

    // ── Render Data Cells (flat mode) ────────────────────────────────────

    private RenderFragment RenderDataCells(TValue item, int rowIndex, Func<GridColumn, GridColumn> transform) => builder =>
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var colIdx = 0;
        foreach (var col in VisibleColumns)
        {
            var capturedCol = col;
            var capturedColIdx = colIdx;
            var isBatchEditing = IsBatchEditing(item, col.Field);

            builder.OpenElement(0, "td");
            var isCellSelected = _selectedCells.Contains((resolvedRowIndex, colIdx));
            var isActiveCell = _activeCell.HasValue
                && _activeCell.Value.RowIndex == resolvedRowIndex
                && _activeCell.Value.CellIndex == colIdx
                && capturedCol.AllowEditing;
            var editableClass = capturedCol.AllowEditing ? " fx-cell-editable" : string.Empty;
            builder.AddAttribute(1, "class",
                (isCellSelected ? "fx-cell fx-cell-selected"
                : isBatchEditing ? "fx-cell fx-batch-editing"
                : isActiveCell ? "fx-cell fx-cell-active-editable"
                : "fx-cell") + editableClass);
            builder.AddAttribute(2, "style", col.GetCellStyle());
            if (col.ClipMode == ClipMode.EllipsisWithTooltip)
                builder.AddAttribute(3, "title", GetCellDisplayValue(item, col));

            // For batch mode, double-click also opens edit (fallback)
            if (EditSettingsRef?.Mode == EditMode.Batch && col.AllowEditing && !col.IsPrimaryKey && !string.IsNullOrEmpty(col.Field))
            {
                builder.AddAttribute(4, "ondblclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => StartBatchEdit(item, resolvedRowIndex, capturedCol)));
            }
            builder.AddAttribute(5, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, args => HandleCellClick(item, resolvedRowIndex, capturedColIdx, args)));
            builder.AddEventStopPropagationAttribute(6, "onclick", true);

            if (isBatchEditing)
            {
                // Render inline edit input
                var inputType = col.Type == ColumnType.Number ? "number"
                    : col.Type == ColumnType.Date ? "date" : "text";
                builder.OpenElement(45, "input");
                builder.AddAttribute(46, "type", inputType);
                builder.AddAttribute(47, "class", "fx-batch-input");
                builder.AddAttribute(48, "value", _batchEditValue);
                builder.AddAttribute(49, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, UpdateBatchEditValue));
                builder.AddAttribute(50, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleBatchEditKeyDown));
                builder.AddAttribute(51, "onblur", EventCallback.Factory.Create(this, CommitBatchEdit));
                builder.AddElementReferenceCapture(52, _ => { }); // auto-focus handled via CSS
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
                builder.OpenElement(30, "input");
                builder.AddAttribute(31, "type", "checkbox");
                builder.AddAttribute(32, "disabled", true);
                builder.AddAttribute(33, "checked", GetBoolValue(item, col.Field));
                builder.CloseElement();
            }
            else
            {
                builder.AddContent(40, GetCellDisplayValue(item, col));
            }

            builder.CloseElement(); // td
            colIdx++;
        }
    };

    // ══════════════════════════════════════════════════════════════════════
    // ── COLUMN RESIZE ────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════

    private void StartResize(GridColumn col, MouseEventArgs e)
    {
        _resizingCol = col;
        _resizeStartX = e.ClientX;

        // Parse starting width
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

    // ── Render Helpers ───────────────────────────────────────────────────

    private RenderFragment RenderEditRow() => builder =>
    {
        builder.OpenElement(0, "tr");
        builder.AddAttribute(1, "class", "fx-row fx-edit-row");

        if (ShowCheckboxColumn)
        {
            builder.OpenElement(2, "td");
            builder.AddAttribute(3, "class", "fx-cell");
            builder.CloseElement();
        }

        // Group indent cells for edit row
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
                    var inputType = col.Type == ColumnType.Number ? "number"
                        : col.Type == ColumnType.Date ? "date" : "text";

                    builder.OpenElement(50, "input");
                    builder.AddAttribute(51, "type", inputType);
                    builder.AddAttribute(52, "class", "fx-edit-input");
                    builder.AddAttribute(53, "value", GetPropertyValue(_editItem, col.Field));
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

    // ── Reflection Helpers ───────────────────────────────────────────────

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
        return val is true;
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
            return "width:100%;";
        return $"width:{totalWidth}px;min-width:100%;";
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
            var val = GetPropertyValue(item, col.Field);
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
        var val = GetPropertyValue(item, col.Field);
        if (val == null) return "";

        if (!string.IsNullOrEmpty(col.Format))
        {
            if (val is IFormattable formattable)
                return formattable.ToString(col.Format, CultureInfo.CurrentCulture);
        }

        return val.ToString() ?? "";
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
        CopyProperties(source!, clone!);
        return clone;
    }

    private static void CopyProperties(object source, object target)
    {
        foreach (var prop in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }

    // ── Public API Methods (SyncFusion equivalent) ───────────────────────
    public int TotalPages => _pageState.TotalPages;
    public int CurrentPage => _pageState.CurrentPage;

    public Task GoToPageAsync(int page) => GoToPage(page);

    public IEnumerable<TValue> GetSelectedRecords() => _selectedItems.ToList();

    public Task<List<TValue>> GetSelectedRecordsAsync() =>
        Task.FromResult(_selectedItems.ToList());

    public Task<List<(int RowIndex, int CellIndex)>> GetSelectedRowCellIndexesAsync() =>
        Task.FromResult(_selectedCells.ToList());

    public int? GetCurrentRowIndex() => _lastSelectedRowIndex;

    public void ClearSelection() => _selectedItems.Clear();

    public Task ClearSelectionAsync()
    {
        _selectedItems.Clear();
        _selectedCells.Clear();
        return InvokeAsync(StateHasChanged);
    }

    public void SelectRow(int rowIndex)
    {
        var list = PagedData.ToList();
        if (rowIndex >= 0 && rowIndex < list.Count)
            _selectedItems.Add(list[rowIndex]);
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
        GetColumnState(field).FilterValue = value;
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearFilteringAsync()
    {
        foreach (var kvp in _columnStates)
        {
            kvp.Value.FilterValue = null;
            kvp.Value.CheckedFilterValues.Clear();
        }
        _pageState.CurrentPage = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task AddRecordAsync(TValue record)
    {
        var list = DataSource as IList<TValue>;
        list?.Add(record);
        await InvokeAsync(StateHasChanged);
    }

    public async Task DeleteRecordAsync(TValue record)
    {
        var list = DataSource as IList<TValue>;
        list?.Remove(record);
        _selectedItems.Remove(record);
        await InvokeAsync(StateHasChanged);
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

    /// <summary>Export grid data to CSV and trigger browser download.</summary>
    public async Task ExportToCsvAsync(string fileName = "export.csv")
    {
        var sb = new StringBuilder();
        var cols = VisibleColumns.ToList();
        // Header row
        sb.AppendLine(string.Join(",", cols.Select(c => EscapeCsvField(c.DisplayHeader))));
        // Data rows — all filtered+sorted data (not just current page)
        foreach (var item in SortedData)
        {
            var values = cols.Select(c => EscapeCsvField(GetCellDisplayValue(item, c)));
            sb.AppendLine(string.Join(",", values));
        }
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        await JsRuntime.InvokeVoidAsync("hfGridExportDownload", fileName, base64, "text/csv");
    }

    /// <summary>Export grid data to Excel-compatible HTML (.xls) and trigger browser download.</summary>
    public async Task ExportToExcelAsync(string fileName = "export.xls")
    {
        var sb = new StringBuilder();
        var cols = VisibleColumns.ToList();
        sb.Append("<table border='1'><thead><tr>");
        foreach (var c in cols)
            sb.Append($"<th>{System.Net.WebUtility.HtmlEncode(c.DisplayHeader)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var item in SortedData)
        {
            sb.Append("<tr>");
            foreach (var c in cols)
                sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(GetCellDisplayValue(item, c))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        await JsRuntime.InvokeVoidAsync("hfGridExportDownload", fileName, base64, "application/vnd.ms-excel");
    }

    /// <summary>Export grid data to a printable HTML table and open the browser print dialog (Save as PDF).</summary>
    public async Task ExportToPdfAsync(string title = "Export")
    {
        var sb = new StringBuilder();
        var cols = VisibleColumns.ToList();
        sb.Append("<html><head><title>").Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title>");
        sb.Append("<style>table{border-collapse:collapse;width:100%;font-size:11px;font-family:Arial,sans-serif}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}th{background:#f0f0f0;font-weight:bold}@@media print{body{margin:0}}</style>");
        sb.Append("</head><body>");
        sb.Append("<table><thead><tr>");
        foreach (var c in cols)
            sb.Append($"<th>{System.Net.WebUtility.HtmlEncode(c.DisplayHeader)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var item in SortedData)
        {
            sb.Append("<tr>");
            foreach (var c in cols)
                sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(GetCellDisplayValue(item, c))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        await JsRuntime.InvokeVoidAsync("hfGridExportPdf", base64);
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return "\"\"";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    public Task EndEditAsync()
    {
        _isEditing = false;
        _editingRowIndex = -1;
        _editItem = default;
        return InvokeAsync(StateHasChanged);
    }

    public Task ExpandAllGroupAsync()
    {
        _expandAllGroups = true;
        _allGroupsCollapsed = false;
        _collapsedGroupPaths.Clear();
        return InvokeAsync(StateHasChanged);
    }

    public Task ScrollIntoViewAsync(int columnIndex, int rowIndex)
    {
        return Task.CompletedTask;
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
}
