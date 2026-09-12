using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

/// <summary>
/// TreeGridControl — hierarchical data grid with expand/collapse, parent/child mapping,
/// typed filtering, sibling sorting, paging, export, and row selection.
/// </summary>
public partial class TreeGridControl<TValue> : ComponentBase, ITreeGridControlOwner
{
    // ── Parameters ───────────────────────────────────────────────────────

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Property name of the unique ID field (e.g. "NodeID").</summary>
    [Parameter] public string IdMapping { get; set; } = "";

    /// <summary>Property name of the parent ID field (e.g. "ParentID").</summary>
    [Parameter] public string ParentIdMapping { get; set; } = "";

    /// <summary>Index (0-based) of the column that renders the tree expand/collapse icons.</summary>
    [Parameter] public int TreeColumnIndex { get; set; } = 0;

    /// <summary>Start with all nodes collapsed.</summary>
    [Parameter] public bool EnableCollapseAll { get; set; }

    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;
    [Parameter] public bool ToggleOnRowClick { get; set; } = true;
    [Parameter] public int TabIndex { get; set; } = 0;
    [Parameter] public bool AllowSorting { get; set; }
    [Parameter] public bool AllowFiltering { get; set; }
    [Parameter] public bool AllowResizing { get; set; }
    [Parameter] public bool ShowColumnOptionsButton { get; set; }
    [Parameter] public bool ShowGridOptionsRail { get; set; }
    [Parameter] public bool ShowColumnHeaders { get; set; } = true;
    [Parameter] public bool ShowHeaderFilterIcon { get; set; } = true;
    [Parameter] public List<string>? Toolbar { get; set; }
    [Parameter] public IReadOnlyList<GridToolbarItem>? ToolbarItems { get; set; }
    [Parameter] public bool? ShowGridToolbar { get; set; }

    /// <summary>
    /// Extra CSS class added to the root .fx-treegrid element. Use
    /// "fx-treegrid-compact" for the dense VB6-style row layout.
    /// </summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>
    /// Pixels of horizontal indent added per tree level (before the expand
    /// icon). Default 16 — matches VB6's tight tree. Older callers using
    /// 40px-per-level can override to 40 to keep the previous wider look.
    /// </summary>
    [Parameter] public int IndentPerLevel { get; set; } = 16;

    /// <summary>
    /// Built-in preset for the per-node expand/collapse icon. Use the
    /// override parameters below for full customisation.
    /// </summary>
    [Parameter] public GroupExpandIconStyle NodeExpandIconStyle { get; set; } = GroupExpandIconStyle.PlusMinus;

    // ── Caller-supplied glyph/icon overrides ─────────────────────────────
    // Resolution order (highest to lowest):
    //   1. ExpandIconTemplate / LeafIconTemplate — full RenderFragment override
    //   2. CollapsedGlyph / ExpandedGlyph / LeafGlyph + *Style strings
    //   3. NodeExpandIconStyle preset (toolkit default)

    /// <summary>Glyph rendered for a collapsed parent node. Null → preset default.</summary>
    [Parameter] public string? CollapsedGlyph { get; set; }

    /// <summary>Glyph rendered for an expanded parent node. Null → preset default.</summary>
    [Parameter] public string? ExpandedGlyph { get; set; }

    /// <summary>Optional glyph rendered for leaf nodes. Null → empty placeholder of icon-width.</summary>
    [Parameter] public string? LeafGlyph { get; set; }

    /// <summary>Inline CSS style for the parent expand/collapse icon span.</summary>
    [Parameter] public string? ExpandIconStyle { get; set; }

    /// <summary>Inline CSS style for the leaf-node icon / placeholder span.</summary>
    [Parameter] public string? LeafIconStyle { get; set; }

    /// <summary>
    /// RenderFragment that fully overrides the parent expand/collapse icon.
    /// Receives a bool indicating whether the node is currently expanded.
    /// The fragment is responsible for click handling.
    /// </summary>
    [Parameter] public RenderFragment<bool>? ExpandIconTemplate { get; set; }

    /// <summary>RenderFragment that fully overrides the leaf-node icon.</summary>
    [Parameter] public RenderFragment? LeafIconTemplate { get; set; }

    /// <summary>Function delegate returning custom icon path for a node item.</summary>
    [Parameter] public Func<TValue, bool, string?>? GetNodeIcon { get; set; }

    /// <summary>
    /// Optional predicate that treats a row as expandable even when its children
    /// are not currently present in the data source.
    /// </summary>
    [Parameter] public Func<TValue, bool>? TreatAsParent { get; set; }

    /// <summary>
    /// Allows folder artwork returned by GetNodeIcon, or the built-in folder icon,
    /// to change when a node expands. Default false keeps the folder stable while
    /// the plus/minus glyph shows expand state.
    /// </summary>
    [Parameter] public bool ChangeNodeIconOnExpand { get; set; }

    /// <summary>
    /// Render folder/leaf icons for leaf rows. VB6 outline grids often show
    /// folder icons only on category rows and leave children as plain text.
    /// </summary>
    [Parameter] public bool ShowLeafNodeIcons { get; set; } = true;

    // Resolved values used by TreeGridControl.razor markup.
    internal string ResolveCollapsedGlyph() =>
        CollapsedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "+" : "▶");

    internal string ResolveExpandedGlyph() =>
        ExpandedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "\u2212" : "▼");

    internal string? ResolveExpandIconStyle() =>
        ExpandIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? FxGridIconStyles.PlusMinus
            : null);

    internal string? ResolveLeafIconStyle() =>
        LeafIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? FxGridIconStyles.LeafSpacer
            : null);

    internal string GetHeaderIconButtonCss(TreeGridColumn column) =>
        string.IsNullOrWhiteSpace(column.HeaderIconCssClass)
            ? "fx-treegrid-header-icon-button"
            : $"fx-treegrid-header-icon-button {column.HeaderIconCssClass.Trim()}";

    internal string? ResolveHeaderIconSrc(TreeGridColumn column)
    {
        if (!string.IsNullOrWhiteSpace(column.HeaderIconSrc))
            return column.HeaderIconSrc;

        return column.HeaderIconKind switch
        {
            TreeGridHeaderIconKind.ExpandAll => $"{StaticAssetRoot}/images/16/expand_all.svg",
            TreeGridHeaderIconKind.CollapseAll => $"{StaticAssetRoot}/images/16/collapse_all.svg",
            _ => null
        };
    }

    internal async Task HandleHeaderIconClickAsync(TreeGridColumn column)
    {
        if (column.HeaderIconClicked.HasDelegate)
            await column.HeaderIconClicked.InvokeAsync();
    }

    private string StaticAssetRoot =>
        _staticAssetRoot ??= $"_content/{GetType().Assembly.GetName().Name}";

    internal string YellowFolderIconSrc => $"{StaticAssetRoot}/images/16/folder-open.ico";
    internal string TreeOpenFolderIconSrc => $"{StaticAssetRoot}/images/16/folder-open.ico";
    internal string TreeClosedFolderIconSrc => $"{StaticAssetRoot}/images/32/folder.ico";

    internal string? ResolveNodeIcon(TreeNode<TValue> node) =>
        GetNodeIcon?.Invoke(node.Data, ChangeNodeIconOnExpand && node.IsExpanded);

    internal string ResolveTreeFolderIconSrc(TreeNode<TValue> node) =>
        ChangeNodeIconOnExpand && node.IsExpanded ? TreeOpenFolderIconSrc : TreeClosedFolderIconSrc;

    internal string ResolveFolderIconState(TreeNode<TValue> node) =>
        ChangeNodeIconOnExpand && node.IsExpanded ? "open" : "closed";

    internal bool UseYellowFolderIcons =>
        CssClass?.Contains("fx-treegrid-yellow-folder", StringComparison.OrdinalIgnoreCase) == true;

    internal bool UseTreeFolderIcons =>
        UseYellowFolderIcons ||
        CssClass?.Contains("fx-treegrid-blue-leaf-folder", StringComparison.OrdinalIgnoreCase) == true;

    internal bool UseDottedTreeLines =>
        CssClass?.Contains("fx-treegrid-dotted-lines", StringComparison.OrdinalIgnoreCase) == true;

    internal bool IsCompact =>
        CssClass?.Contains("fx-treegrid-compact", StringComparison.OrdinalIgnoreCase) == true;



    // Selection settings
    [Parameter] public SelectionMode SelectionMode { get; set; } = SelectionMode.Row;

    /// <summary>
    /// Optional row CSS callback. Mirrors GridControl's RowCssClassSelector so
    /// callers can mark a persistent business state independently of transient
    /// tree selection.
    /// </summary>
    [Parameter] public Func<TValue, int, string?>? RowCssClassSelector { get; set; }

    // Events
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowSelected { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowDeselected { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowDoubleClicked { get; set; }
    /// <summary>Fires when a row is right-clicked, AFTER the row has been selected —
    /// VB6 VSFlexGrid BeforeMouseDown parity (.Row = .MouseRow), so a context menu
    /// opened by an ancestor's @oncontextmenu acts on the row under the cursor
    /// (HHM-88, owner-authorized 2026-07-29). Purely additive: when no delegate is
    /// attached, right-click behavior is unchanged. The event does not stop
    /// propagation; the browser contextmenu event still bubbles to page handlers.</summary>
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowRightClicked { get; set; }
    /// <summary>Keyboard activation event. Enter invokes this for the selected row
    /// without changing its expanded state. When it has no subscriber, Enter falls
    /// back to <see cref="RowDoubleClicked"/> so mouse and keyboard activation can
    /// share the same host handler.</summary>
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowActivated { get; set; }
    /// <summary>Fires for every keydown reaching the tree grid, BEFORE the built-in
    /// tree navigation runs — the host-form hook for form-specific keys (VB6
    /// gData_KeyDown parity, e.g. Delete clearing a cell value). Purely additive:
    /// built-in navigation still runs after the callback.</summary>
    [Parameter] public EventCallback<KeyboardEventArgs> OnHostKeyDown { get; set; }

    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Expanded { get; set; }
    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Collapsed { get; set; }
    [Parameter] public EventCallback<string> OnToolbarItemClick { get; set; }
    [Parameter] public EventCallback<GridToolbarClickEventArgs> ToolbarItemClicked { get; set; }

    // ── Internal State ──────────────────────────────────────────────────

    private List<TreeNode<TValue>> _flatNodes = new();
    private TValue? _selectedItem;
    private int _selectedIndex = -1;
    internal List<TreeGridColumn> _columns = new();
    private IEnumerable<TValue>? _previousDataSource;
    private bool _treeBuilt;
    private int _lastRenderedColumnCount;
    private string? _staticAssetRoot;
    private ElementReference _treeGridElement;
    private TreeNode<TValue>? _pendingKeyboardFocusNode;
    private bool _pendingTreeFocus;
    private readonly Dictionary<string, ColumnState> _columnStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _visibilityOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _columnWidthOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _filterPopupField;
    private string _filterDraft = "";
    private bool _treeColumnPanelOpen;
    private string _columnPanelSearch = "";
    private bool _isColumnResizing;
    private TreeGridColumn? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private string? _openToolbarMenuKey;

    // ── Column Registration ─────────────────────────────────────────────

    public void AddColumn(TreeGridColumn column)
    {
        if (!_columns.Contains(column))
            _columns.Add(column);
    }

    public void RemoveColumn(TreeGridColumn column)
    {
        _columns.Remove(column);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        foreach (var node in _flatNodes.Where(n => n.IsExpanded && NeedsChildLoad(n) && !IsLoading(n)).ToArray())
            await SetNodeExpandedAsync(node, true);
        if (_disposed) return;
        if (_columns.Count != _lastRenderedColumnCount)
        {
            _lastRenderedColumnCount = _columns.Count;
            StateHasChanged();
        }

        // Deferred no-trap refocus from ClearActiveCellEdit — runs after the
        // editor's unmount render so the DOM removal can't steal focus back.
        var treeScrollModule = await GetLegacyScrollModuleAsync();
        // Disposed during the import (the tab closed): the element is gone and a
        // DotNetObjectReference created now would never be released.
        if (_disposed) return;
        if (_refocusAfterCellEditClose)
        {
            _refocusAfterCellEditClose = false;
            // Only the module can tell whether focus is still here or was lost; with
            // no module the tree does NOT pull focus back — the user may have Tabbed
            // to another field on purpose.
            if (treeScrollModule is not null)
                try { await treeScrollModule.InvokeVoidAsync("focusTreeIfFocusLost", _treeGridElement); } catch { }
        }

        if (!_disposed && !_treeKeyboardNavigationEnabled && treeScrollModule is not null)
        {
            try
            {
                _selfRef ??= DotNetObjectReference.Create(this);
                await treeScrollModule.InvokeVoidAsync(
                    "enableTreeKeyboardNavigation", _treeGridElement, _scrollViewportElement, _selfRef);
                _treeKeyboardNavigationEnabled = true;
            }
            catch { }
        }

        if (treeScrollModule is not null && (FrozenColumns > 0 || ShowCheckboxes || _columns.Any(c => FrozenPosition(c) is not null)))
            try { await treeScrollModule.InvokeVoidAsync("syncTreeGridLayout", _treeGridElement); } catch { }
        if (_focusEditorPending && _focusEditor is not null)
        {
            _focusEditorPending = false; _pendingTreeFocus = false; _pendingKeyboardFocusNode = null;
            await _focusEditor.FocusAsync();
        }

        if (_pendingTreeFocus)
        {
            _pendingTreeFocus = false;
            _pendingKeyboardFocusNode = null;
            try { await _treeGridElement.FocusAsync(preventScroll: true); } catch { }
        }
        else if (_pendingKeyboardFocusNode is { } pendingNode)
        {
            _pendingKeyboardFocusNode = null;
            pendingNode = _flatNodes.FirstOrDefault(n => Equals(n.Id, pendingNode.Id)) ?? pendingNode;
            try
            {
                if (treeScrollModule is not null)
                    await treeScrollModule.InvokeVoidAsync(
                        "focusTreeRow", _scrollViewportElement, pendingNode.RowElement);
                else
                    await pendingNode.RowElement.FocusAsync(preventScroll: false);
            }
            catch
            {
                try { await pendingNode.RowElement.FocusAsync(preventScroll: false); } catch { }
            }
        }

        if (ShowLegacyScrollBar)
            await SyncLegacyScrollBarAsync();
    }

    // ── Legacy scrollbar (opt-in; inert unless ShowLegacyScrollBar is set) ──
    // A browser-drawn scrollbar dispatches no mouse events, so it can carry
    // neither a context menu nor Win9x styling. This one is ordinary DOM.

    /// <summary>Replaces the native vertical scrollbar with a DOM-drawn Win9x-style one.</summary>
    [Parameter] public bool ShowLegacyScrollBar { get; set; }

    /// <summary>Standard Windows scroll menu on right-click. Needs <see cref="ShowLegacyScrollBar"/>.</summary>
    [Parameter] public bool EnableLegacyScrollBarMenu { get; set; } = true;

    /// <summary>Row height in px used by the line-scroll commands and the arrow buttons.</summary>
    [Parameter] public double LegacyScrollLineHeight { get; set; } = 17;

    /// <summary>Raised before the built-in menu, so a host can show its own instead.</summary>
    [Parameter] public EventCallback<MouseEventArgs> LegacyScrollBarRightClicked { get; set; }

    [Inject] private IJSRuntime? LegacyScrollJs { get; set; }

    // Derived from the assembly, never a literal: the same source ships as FlexKit
    // and as FlexCore, and a literal serves 404 in the other one (silently — every
    // caller degrades to "no module").
    private static readonly string LegacyScrollBarJsModulePath =
        FxJsAsset.Versioned($"./_content/{typeof(TreeGridControl<TValue>).Assembly.GetName().Name}/legacy-scrollbar.js");
    private IJSObjectReference? _legacyScrollModule;
    private bool _treeKeyboardNavigationEnabled;

    private ElementReference _scrollViewportElement;
    private ElementReference _legacyTrackElement;

    private bool _legacyScrollable;
    private double _legacyThumbTopPct;
    private double _legacyThumbHeightPct = 100;
    private bool _legacyThumbDragging;
    private double _legacyDragStartClientY;
    private double _legacyDragStartScrollTop;

    private bool _showLegacyScrollMenu;
    private double _legacyScrollMenuX;
    private double _legacyScrollMenuY;
    private double _legacyScrollMenuRatio;

    private async Task<IJSObjectReference?> GetLegacyScrollModuleAsync()
    {
        if (LegacyScrollJs is null || _disposed) return null;
        try
        {
            if (_legacyScrollModule is not null) return _legacyScrollModule;
            var module = await LegacyScrollJs.InvokeAsync<IJSObjectReference>("import", LegacyScrollBarJsModulePath);
            if (_disposed)
            {
                // Torn down while the import was in flight: nobody disposes it later.
                try { await module.DisposeAsync(); } catch { }
                return null;
            }
            return _legacyScrollModule = module;
        }
        catch { return null; }   // prerender / torn-down circuit — interop stays inactive
    }

    private async Task<(double Top, double Height, double Client)?> ReadLegacyMetricsAsync()
    {
        var mod = await GetLegacyScrollModuleAsync();
        if (mod is null) return null;
        try
        {
            var m = await mod.InvokeAsync<double[]>("readScrollMetrics", _scrollViewportElement);
            return m is { Length: 3 } ? (m[0], m[1], m[2]) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Recomputes the thumb. Called from OnAfterRenderAsync, so it MUST only raise
    /// StateHasChanged when a number actually moved — otherwise it would re-render on
    /// every render and spin forever.
    /// </summary>
    private async Task SyncLegacyScrollBarAsync()
    {
        var m = await ReadLegacyMetricsAsync();

        var scrollable = _legacyScrollable;
        var top = _legacyThumbTopPct;
        var height = _legacyThumbHeightPct;

        if (m is null || m.Value.Height <= 0 || m.Value.Height - m.Value.Client <= 1)
        {
            scrollable = false;
            top = 0;
            height = 100;
        }
        else
        {
            var overflow = m.Value.Height - m.Value.Client;
            scrollable = true;
            // Windows keeps the thumb grabbable on very long lists — floor it at 10%.
            height = Math.Max(10, m.Value.Client / m.Value.Height * 100);
            top = Math.Clamp(m.Value.Top / overflow, 0, 1) * (100 - height);
        }

        if (scrollable == _legacyScrollable
            && Math.Abs(top - _legacyThumbTopPct) < 0.05
            && Math.Abs(height - _legacyThumbHeightPct) < 0.05)
        {
            return;
        }

        _legacyScrollable = scrollable;
        _legacyThumbTopPct = top;
        _legacyThumbHeightPct = height;
        StateHasChanged();
    }

    private Task HandleLegacyScroll() => ShowLegacyScrollBar ? SyncLegacyScrollBarAsync() : Task.CompletedTask;

    private async Task SetLegacyScrollTopAsync(double top)
    {
        var mod = await GetLegacyScrollModuleAsync();
        if (mod is null) return;
        try { await mod.InvokeVoidAsync("setScrollTop", _scrollViewportElement, top); }
        catch { return; }
        await SyncLegacyScrollBarAsync();
    }

    private async Task ScrollByLineAsync(int direction)
    {
        CloseLegacyScrollMenuForCommand();
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;
        await SetLegacyScrollTopAsync(m.Value.Top + direction * LegacyScrollLineHeight);
    }

    private async Task ScrollByPageAsync(int direction)
    {
        CloseLegacyScrollMenuForCommand();
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;
        await SetLegacyScrollTopAsync(m.Value.Top + direction * m.Value.Client);
    }

    private async Task ScrollToEdgeAsync(bool toTop)
    {
        CloseLegacyScrollMenuForCommand();
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;
        await SetLegacyScrollTopAsync(toTop ? 0 : m.Value.Height);
    }

    /// <summary>Windows "Scroll Here": jump to the fraction of the track that was clicked.</summary>
    private async Task ScrollHereAsync()
    {
        var ratio = _legacyScrollMenuRatio;
        CloseLegacyScrollMenuForCommand();
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;
        await SetLegacyScrollTopAsync(ratio * Math.Max(0, m.Value.Height - m.Value.Client));
    }

    /// <summary>Where a viewport-space Y falls inside the track, 0..1.</summary>
    private async Task<double> LegacyTrackRatioAsync(double clientY)
    {
        var mod = await GetLegacyScrollModuleAsync();
        if (mod is null) return 0;
        try
        {
            var r = await mod.InvokeAsync<double[]>("readElementRect", _legacyTrackElement);
            if (r is not { Length: 4 } || r[3] <= 0) return 0;
            return Math.Clamp((clientY - r[1]) / r[3], 0, 1);
        }
        catch { return 0; }
    }

    private async Task HandleLegacyScrollBarContextMenu(MouseEventArgs e)
    {
        if (LegacyScrollBarRightClicked.HasDelegate)
            await LegacyScrollBarRightClicked.InvokeAsync(e);

        if (!EnableLegacyScrollBarMenu) return;

        _legacyScrollMenuX = e.ClientX;
        _legacyScrollMenuY = e.ClientY;
        _legacyScrollMenuRatio = await LegacyTrackRatioAsync(e.ClientY);
        _showLegacyScrollMenu = true;
        StateHasChanged();
    }

    private void CloseLegacyScrollMenu() => _showLegacyScrollMenu = false;

    private void CloseLegacyScrollMenuForCommand()
    {
        if (_showLegacyScrollMenu)
            _pendingTreeFocus = true;

        _showLegacyScrollMenu = false;
    }

    /// <summary>Clicking the track above/below the thumb pages, as in Windows.</summary>
    private async Task HandleLegacyTrackMouseDown(MouseEventArgs e)
    {
        if (e.Button != 0 || !_legacyScrollable) return;
        var ratio = await LegacyTrackRatioAsync(e.ClientY) * 100;
        await ScrollByPageAsync(ratio < _legacyThumbTopPct ? -1 : 1);
    }

    private async Task HandleLegacyThumbMouseDown(MouseEventArgs e)
    {
        if (e.Button != 0 || !_legacyScrollable) return;
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;
        _legacyDragStartClientY = e.ClientY;
        _legacyDragStartScrollTop = m.Value.Top;
        _legacyThumbDragging = true;
    }

    private async Task HandleLegacyThumbDragMove(MouseEventArgs e)
    {
        if (!_legacyThumbDragging) return;
        var m = await ReadLegacyMetricsAsync();
        if (m is null) return;

        var mod = await GetLegacyScrollModuleAsync();
        if (mod is null) return;
        double[] rect;
        try { rect = await mod.InvokeAsync<double[]>("readElementRect", _legacyTrackElement); }
        catch { return; }
        if (rect is not { Length: 4 } || rect[3] <= 0) return;

        // Thumb travel is the track minus the thumb, so one pixel of drag is worth
        // (overflow / travel) pixels of scroll.
        var travelPx = rect[3] * (1 - _legacyThumbHeightPct / 100);
        if (travelPx <= 0) return;
        var overflow = m.Value.Height - m.Value.Client;
        await SetLegacyScrollTopAsync(_legacyDragStartScrollTop + (e.ClientY - _legacyDragStartClientY) / travelPx * overflow);
    }

    private void EndLegacyThumbDrag() => _legacyThumbDragging = false;

    internal List<TreeGridColumn> VisibleColumns => _columns.Where(IsColumnVisible).OrderBy(c => FrozenPosition(c) switch { FrozenColumnPosition.Left => 0, FrozenColumnPosition.Right => 2, _ => 1 }).ToList();

    internal string TreeGridCssClass
    {
        get
        {
            var parts = new List<string> { "fx-treegrid" };
            if (!string.IsNullOrWhiteSpace(CssClass))
                parts.Add(CssClass.Trim());
            if (ShowGridOptionsRail && ShowColumnOptionsButton)
                parts.Add("fx-treegrid-options-on");
            // SelectionMode.Cell: highlight only the node (tree) cell of the
            // selected row — the vsFlexGrid free-cell-selection look
            // (AfterSelChange ColSel=Col). Row mode keeps the full-row band.
            if (SelectionMode == SelectionMode.Cell)
                parts.Add("fx-treegrid-select-cell");
            if (ShouldRenderToolbar)
                parts.Add("fx-treegrid-toolbar-on");
            if (_isColumnResizing)
                parts.Add("fx-treegrid-resizing");
            return string.Join(" ", parts);
        }
    }

    internal bool ShouldRenderToolbar =>
        ShowGridToolbar ?? (ResolvedToolbarItems.Any());

    internal IEnumerable<GridToolbarItem> ResolvedToolbarItems =>
        ResolveToolbarItems(ToolbarItems, Toolbar);

    internal bool ShouldRenderColumnOptions =>
        ShowColumnOptionsButton && _columns.Any(c => !string.IsNullOrWhiteSpace(c.Field));

    internal IEnumerable<TreeGridColumn> ColumnPanelColumns
    {
        get
        {
            var query = _columns.Where(c => !string.IsNullOrWhiteSpace(c.Field));
            if (!string.IsNullOrWhiteSpace(_columnPanelSearch))
            {
                query = query.Where(c =>
                    c.DisplayHeader.Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase) ||
                    c.Field.Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase));
            }
            return query;
        }
    }

    private bool IsColumnVisible(TreeGridColumn column)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
            return column.Visible;

        return _visibilityOverrides.TryGetValue(column.Field, out var visible)
            ? visible
            : column.Visible;
    }

    private bool CanHideColumn(TreeGridColumn column) =>
        !IsColumnVisible(column) || VisibleColumns.Count > 1;

    private string GetColumnKey(TreeGridColumn column) =>
        !string.IsNullOrWhiteSpace(column.Field)
            ? column.Field
            : column.DisplayHeader;

    private ColumnState GetColumnState(TreeGridColumn column) =>
        GetColumnState(GetColumnKey(column));

    // ── Shared grid toolbar chrome ──────────────────────────────────────

    private IEnumerable<GridToolbarItem> ResolveToolbarItems(
        IReadOnlyList<GridToolbarItem>? richItems,
        IReadOnlyList<string>? textItems)
    {
        if (richItems != null)
        {
            foreach (var item in richItems.Where(i => i.Visible))
                yield return item;
        }

        if (textItems != null)
        {
            foreach (var item in textItems.Where(i => !string.IsNullOrWhiteSpace(i)))
                yield return new GridToolbarItem { Key = item, Text = item };
        }
    }

    internal GridToolbarItem ResolveToolbarItem(GridToolbarItem item)
    {
        var resolved = new GridToolbarItem
        {
            Key = item.Key,
            Text = item.Text,
            Title = item.Title,
            IconSrc = item.IconSrc,
            IconAlt = item.IconAlt,
            Glyph = item.Glyph,
            Action = item.Action,
            Disabled = item.Disabled,
            Visible = item.Visible,
            SeparatorBefore = item.SeparatorBefore,
            SeparatorAfter = item.SeparatorAfter,
            Items = item.Items
        };

        if (string.IsNullOrWhiteSpace(resolved.Key))
            resolved.Key = !string.IsNullOrWhiteSpace(resolved.Text) ? resolved.Text : resolved.Action.ToString();

        if (resolved.Action == GridToolbarAction.Custom)
            resolved.Action = ResolveToolbarAction(resolved.Key);

        ApplyToolbarDefaults(resolved);
        return resolved;
    }

    private GridToolbarAction ResolveToolbarAction(string key)
    {
        var normalized = NormalizeToolbarKey(key);
        return normalized switch
        {
            "expandall" or "expand" => GridToolbarAction.ExpandAll,
            "collapseall" or "collapse" => GridToolbarAction.CollapseAll,
            "toggleexpandcollapse" or "expandcollapse" or "toggleall" => GridToolbarAction.ToggleExpandCollapse,
            "refresh" or "reload" => GridToolbarAction.Refresh,
            "columns" or "columnchooser" or "columnoptions" => GridToolbarAction.Columns,
            "clearfilters" or "clearfilter" => GridToolbarAction.ClearFilters,
            _ => GridToolbarAction.Custom
        };
    }

    private void ApplyToolbarDefaults(GridToolbarItem item)
    {
        switch (item.Action)
        {
            case GridToolbarAction.ExpandAll:
                item.Title ??= "Expand All";
                item.IconSrc ??= $"{StaticAssetRoot}/images/16/expand_all.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.CollapseAll:
                item.Title ??= "Collapse All";
                item.IconSrc ??= $"{StaticAssetRoot}/images/16/collapse_all.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.ToggleExpandCollapse:
                item.Title ??= AreAllExpandableNodesExpanded ? "Collapse All" : "Expand All";
                item.IconSrc ??= AreAllExpandableNodesExpanded
                    ? $"{StaticAssetRoot}/images/16/collapse_all.svg"
                    : $"{StaticAssetRoot}/images/16/expand_all.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.Refresh:
                item.Title ??= "Refresh";
                item.Glyph ??= "↻";
                break;
            case GridToolbarAction.Columns:
                item.Title ??= "Columns";
                item.Glyph ??= "▦";
                break;
            case GridToolbarAction.ClearFilters:
                item.Title ??= "Clear Filters";
                item.Glyph ??= "⌧";
                break;
        }
    }

    internal bool HasHeaderToolbar(TreeGridColumn column) =>
        column.HeaderToolbarItems?.Any(i => i.Visible) == true;

    internal IEnumerable<GridToolbarItem> GetHeaderToolbarItems(TreeGridColumn column) =>
        ResolveToolbarItems(column.HeaderToolbarItems, null);

    internal bool HasToolbarMenu(GridToolbarItem item) =>
        item.Items?.Any(i => i.Visible) == true;

    internal IEnumerable<GridToolbarItem> GetVisibleToolbarChildren(GridToolbarItem item) =>
        item.Items?.Where(i => i.Visible) ?? Enumerable.Empty<GridToolbarItem>();

    internal bool IsToolbarMenuOpen(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column) =>
        string.Equals(_openToolbarMenuKey, GetToolbarMenuKey(item, isHeaderToolbar, column), StringComparison.Ordinal);

    internal string GetToolbarButtonCss(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? "fx-treegrid-toolbar-button has-text" : "fx-treegrid-toolbar-button";

    internal string GetHeaderToolbarButtonCss(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? "fx-treegrid-header-toolbar-button has-text" : "fx-treegrid-header-toolbar-button";

    internal string ResolveToolbarTitle(GridToolbarItem item) =>
        item.Title ?? item.Text ?? item.Key;

    internal string ResolveToolbarAriaLabel(GridToolbarItem item) =>
        ResolveToolbarTitle(item);

    internal string? ResolveToolbarIconSrc(GridToolbarItem item) =>
        item.IconSrc;

    internal string ResolveToolbarIconAlt(GridToolbarItem item) =>
        item.IconAlt ?? "";

    internal string? ResolveToolbarGlyph(GridToolbarItem item) =>
        item.Glyph;

    internal string ResolveToolbarMenuText(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? item.Text : ResolveToolbarTitle(item);

    internal async Task HandleToolbarMenuItemClickAsync(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column)
    {
        _openToolbarMenuKey = null;
        await HandleToolbarItemClickAsync(item, isHeaderToolbar, column, menuChild: true);
    }

    internal async Task HandleToolbarItemClickAsync(
        GridToolbarItem item,
        bool isHeaderToolbar,
        TreeGridColumn? column,
        bool menuChild = false)
    {
        if (item.Disabled)
            return;

        if (!menuChild && HasToolbarMenu(item))
        {
            var key = GetToolbarMenuKey(item, isHeaderToolbar, column);
            _openToolbarMenuKey = string.Equals(_openToolbarMenuKey, key, StringComparison.Ordinal) ? null : key;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var args = new GridToolbarClickEventArgs
        {
            Key = item.Key,
            Action = item.Action,
            Item = item,
            IsHeaderToolbar = isHeaderToolbar,
            ColumnField = column?.Field,
            ColumnHeader = column?.DisplayHeader
        };

        if (isHeaderToolbar && column?.HeaderToolbarItemClicked.HasDelegate == true)
            await column.HeaderToolbarItemClicked.InvokeAsync(args);
        else if (!isHeaderToolbar && ToolbarItemClicked.HasDelegate)
            await ToolbarItemClicked.InvokeAsync(args);

        if (args.Cancel)
            return;

        await RunToolbarActionAsync(item.Action);

        if (!isHeaderToolbar && OnToolbarItemClick.HasDelegate)
            await OnToolbarItemClick.InvokeAsync(item.Key);
    }

    private string GetToolbarMenuKey(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column) =>
        $"{(isHeaderToolbar ? "h" : "t")}:{column?.Field ?? column?.DisplayHeader ?? ""}:{NormalizeToolbarKey(item.Key)}";

    private static string NormalizeToolbarKey(string? key) =>
        new((key ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task RunToolbarActionAsync(GridToolbarAction action)
    {
        switch (action)
        {
            case GridToolbarAction.ExpandAll:
                await ExpandAllAsync();
                break;
            case GridToolbarAction.CollapseAll:
                await CollapseAllAsync();
                break;
            case GridToolbarAction.ToggleExpandCollapse:
                if (AreAllExpandableNodesExpanded)
                    await CollapseAllAsync();
                else
                    await ExpandAllAsync();
                break;
            case GridToolbarAction.Columns:
                if (ShouldRenderColumnOptions)
                {
                    _treeColumnPanelOpen = !_treeColumnPanelOpen;
                    await InvokeAsync(StateHasChanged);
                }
                break;
            case GridToolbarAction.ClearFilters:
                await ClearFiltersAsync();
                break;
        }
    }

    private ColumnState GetColumnState(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            field = "__treegrid_column__";

        if (!_columnStates.TryGetValue(field, out var state))
        {
            state = new ColumnState { Field = field };
            _columnStates[field] = state;
        }

        return state;
    }

    /// <summary>
    /// Resolves TreeColumnIndex to the correct visible column index.
    /// If TreeColumnIndex refers to a hidden column, finds the matching visible index.
    /// </summary>
    internal int ResolvedTreeColumnIndex
    {
        get
        {
            if (TreeColumnIndex < _columns.Count)
            {
                // TreeColumnIndex refers to the overall column list — find its position in visible columns
                var targetCol = _columns[TreeColumnIndex];
                if (targetCol.Visible)
                {
                    var visibleCols = VisibleColumns;
                    for (int i = 0; i < visibleCols.Count; i++)
                    {
                        if (ReferenceEquals(visibleCols[i], targetCol))
                            return i;
                    }
                }
            }
            // Fallback: treat TreeColumnIndex as visible column index directly
            return TreeColumnIndex;
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override void OnParametersSet()
    {
        if (PageSize < 1) throw new ArgumentException("PageSize must be positive.");
        // Only rebuild the tree when DataSource actually changes, to preserve
        // expand/collapse state across re-renders triggered by StateHasChanged.
        if (!_treeBuilt || !ReferenceEquals(DataSource, _previousDataSource))
        {
            var ownPublication = ReferenceEquals(DataSource, _publishedRecords) && _publishedRecords is not null;
            _dataGeneration++; _loadedChildren.Clear(); _loadingChildren.Clear(); _expansionIntent.Clear(); _loadError = null;
            _localRecords = null; _publishedRecords = null;
            if (!ownPublication) { ClearEditSessions(); _completedChildLoads.Clear(); }
            // Preserve existing expand/collapse states before rebuilding
            Dictionary<object, bool>? expandStates = null;
            if (_treeBuilt && _flatNodes.Count > 0)
            {
                expandStates = new Dictionary<object, bool>();
                foreach (var node in _flatNodes)
                {
                    if (node.Id != null)
                        expandStates[node.Id] = node.IsExpanded;
                }
            }

            BuildTree();

            // Restore expand/collapse states from before the rebuild
            if (expandStates != null)
            {
                foreach (var node in _flatNodes)
                {
                    if (node.Id != null && expandStates.TryGetValue(node.Id, out var wasExpanded))
                        node.IsExpanded = wasExpanded;
                }
            }

            _previousDataSource = DataSource;
            _treeBuilt = true;
        }
        ReceiveCheckedItems();
    }

    private static readonly object RootKey = new();

    // ── Tree Building ────────────────────────────────────────────────────

    private void BuildTree()
    {
        var nextNodes = new List<TreeNode<TValue>>();
        if ((DataSource == null && _localRecords == null) || string.IsNullOrEmpty(IdMapping) || string.IsNullOrEmpty(ParentIdMapping))
        {
            _flatNodes = nextNodes;
            return;
        }

        var items = PreviewRecords.ToList();
        var idProp = typeof(TValue).GetProperty(IdMapping);
        var parentIdProp = typeof(TValue).GetProperty(ParentIdMapping);
        if (idProp == null || parentIdProp == null) throw new ArgumentException("Invalid tree ID or parent mapping.");

        // Build lookup: parentId -> children
        var childrenMap = new Dictionary<object, List<(TValue Item, object Id)>>();
        var allItems = new List<(TValue Item, object? Id, object? ParentId)>();

        var uniqueIds = new HashSet<object>();
        foreach (var item in items)
        {
            var id = idProp.GetValue(item);
            if (id is null || !uniqueIds.Add(id)) throw new ArgumentException("Tree IDs must be non-null and unique.");
            var parentId = parentIdProp.GetValue(item);
            allItems.Add((item, id, parentId));

            var parentKey = parentId ?? RootKey;
            if (!childrenMap.ContainsKey(parentKey))
                childrenMap[parentKey] = new();
            childrenMap[parentKey].Add((item, id!));
        }

        // Find root nodes (parentId is null or not found in any id)
        var allIds = new HashSet<object>(allItems.Where(a => a.Id != null).Select(a => a.Id!));
        var roots = allItems.Where(a => a.ParentId == null || !allIds.Contains(a.ParentId)).ToList();
        SortRows(roots, row => row.Item);

        void AddNodes(object? parentKey, int level, IReadOnlyList<bool> ancestorLineContinuations)
        {
            if (level > 512) throw new ArgumentException("Tree depth cannot exceed 512 levels.");
            var key = parentKey ?? RootKey;
            if (!childrenMap.TryGetValue(key, out var children)) return;
            SortRows(children, row => row.Item);

            for (var index = 0; index < children.Count; index++)
            {
                var (item, id) = children[index];
                var hasChildren = (childrenMap.ContainsKey(id) && childrenMap[id].Count > 0)
                    || MayHaveUnloadedChildren(item, id);
                var isLastSibling = index == children.Count - 1;
                nextNodes.Add(new TreeNode<TValue>
                {
                    Data = item,
                    Level = level,
                    HasChildren = hasChildren,
                    IsExpanded = !EnableCollapseAll,
                    Id = id,
                    ParentId = parentKey,
                    IsLastSibling = isLastSibling,
                    AncestorLineContinuations = ancestorLineContinuations.ToArray()
                });

                if (hasChildren)
                {
                    var childAncestorLines = ancestorLineContinuations.Concat(new[] { !isLastSibling }).ToArray();
                    AddNodes(id, level + 1, childAncestorLines);
                }
            }
        }

        // Process roots
        for (var index = 0; index < roots.Count; index++)
        {
            var root = roots[index];
            var hasChildren = (root.Id != null && childrenMap.ContainsKey(root.Id) && childrenMap[root.Id].Count > 0)
                || (root.Id is not null && MayHaveUnloadedChildren(root.Item, root.Id));
            var isLastSibling = index == roots.Count - 1;
            nextNodes.Add(new TreeNode<TValue>
            {
                Data = root.Item,
                Level = 0,
                HasChildren = hasChildren,
                IsExpanded = !EnableCollapseAll,
                Id = root.Id,
                ParentId = root.ParentId,
                IsLastSibling = isLastSibling,
                AncestorLineContinuations = Array.Empty<bool>()
            });

            if (hasChildren && root.Id != null)
                AddNodes(root.Id, 1, new[] { !isLastSibling });
        }
        if (nextNodes.Count != items.Count) throw new ArgumentException("Tree parent mappings contain a cycle.");
        _flatNodes = nextNodes;
    }

    // ── Visible Nodes (respecting expand/collapse) ──────────────────────

    /// <summary>Current display order after hierarchy expansion and sorting.</summary>
    public IReadOnlyList<TValue> GetVisibleRecords() => VisibleNodes.Select(n => n.Data).ToArray();

    private IEnumerable<TreeNode<TValue>> VisibleNodes
    {
        get
        {
            var result = new List<TreeNode<TValue>>();
            var collapsedLevels = new Stack<int>();

            foreach (var node in _flatNodes)
            {
                // Check if this node is hidden by a collapsed ancestor
                while (collapsedLevels.Count > 0 && collapsedLevels.Peek() >= node.Level)
                    collapsedLevels.Pop();

                if (collapsedLevels.Count > 0)
                    continue;

                result.Add(node);

                if (node.HasChildren && !node.IsExpanded)
                    collapsedLevels.Push(node.Level);
            }

            if (!HasActiveFilters)
                return result;

            var included = BuildFilterInclusionSet();
            _filteredParentIds = included.Where(n => n.ParentId is not null).Select(n => n.ParentId!).ToHashSet();
            return _flatNodes.Where(included.Contains).ToList();
        }
    }

    // ── Event Handlers ──────────────────────────────────────────────────

    private async Task ToggleNode(TreeNode<TValue> node)
    {
        if (!await FinishActiveEditorAsync()) return;
        await SetNodeExpandedAsync(node, !node.IsExpanded);
    }

    private async Task HandleExpandIconClick(TreeNode<TValue> node, int visibleIndex)
    {
        await ToggleNode(node);
        await SelectNodeAsync(node, visibleIndex);
    }

    private async Task SetNodeExpandedAsync(TreeNode<TValue> node, bool expanded)
    {
        if (node.Id is null || !node.HasChildren) return;
        var id = node.Id;
        _expansionIntent[id] = expanded;
        if (IsLoading(node)) return;
        if (expanded && NeedsChildLoad(node))
        {
            if (!await LoadNodeChildrenAsync(node)) return;
            node = _flatNodes.First(n => Equals(n.Id, id));
            expanded = _expansionIntent.GetValueOrDefault(id, true);
        }
        else if (node.IsExpanded == expanded) return;
        node.IsExpanded = expanded && node.HasChildren;
        if (node.IsExpanded && Expanded.HasDelegate)
            await Expanded.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });
        else if (!node.IsExpanded && Collapsed.HasDelegate)
            await Collapsed.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });
        StateHasChanged();
    }

    private async Task HandleRowClick(TreeNode<TValue> node, int visibleIndex)
    {
        if (_activeEdit is not null && !await FinishActiveEditorAsync()) return;
        node = _flatNodes.FirstOrDefault(n => Equals(n.Id, node.Id)) ?? node;
        // (Clicks on an editable cell's display button stop propagation and go
        // through ActivateCellEditFromClickAsync instead.)

        // Toggle expand/collapse when clicking anywhere on a parent node row
        if (ToggleOnRowClick && node.HasChildren)
            await ToggleNode(node);

        await SelectNodeAsync(node, visibleIndex);
        await FocusAsync();
    }

    // ── In-cell editing (CellEditTemplate columns): the cell editor host ──────
    // The CONTROL owns what pages used to hand-roll, as a two-phase cell
    // (vsFlexGrid Editable=flexEDKbdMouse with ShowComboButton on):
    //   cursor  — the selected row's editable cell (row mode: the row's single
    //             editable column; Cell mode: the cursor column) shows its
    //             editor CLOSED and inert over the display, so a list or date
    //             cell shows its button the moment it becomes current;
    //   editing — Enter / F2 / F4 / typing / a second click start the edit: the
    //             editor takes focus, opens on request, and reports back through
    //             ICellEditorHost (Enter commits and stays, Escape leaves, Up/Down
    //             leave and move, a pick or close ends the edit).
    // Every phase change is a new generation and a fresh editor instance (the
    // template is keyed on it), so AutoFocus / OpenOnRender / InitialText are
    // first-render facts and a stale editor's callbacks are ignored.
    private object? _editingNodeId;
    private string? _editingField;
    private bool _editingOpenOnRender;
    private string? _editingInitialText;
    private int _cellEditGeneration;
    private TreeGridCellEditContext? _cellEditContext;
    private bool _refocusAfterCellEditClose;
    private DotNetObjectReference<TreeGridControl<TValue>>? _selfRef;

    private bool IsEditingCell => _editingNodeId is not null;

    /// <summary>A row-selection tree whose rows carry cell editors — a property grid
    /// (VB6 gProperties: vsFlexGrid TabBehavior=1). Tab / Shift+Tab move rows and
    /// leave only at the first / last row (data-fx-grid-tab-edge, read by the page
    /// graph and the root listener); Right / Left stay the outline keys (VB6
    /// gProperties_KeyDown). An explorer tree (no editors) and a Cell-mode tree stay
    /// ONE page-level Tab stop (VB6 TabBehavior=0).</summary>
    private bool IsPropertyGrid => SelectionMode == SelectionMode.Row && _columns.Any(c => c.CellEditTemplate != null);

    /// <summary>Which direction a Tab leaves a property grid: "first" / "last" /
    /// "both" / "none" from the current row (no current row: Tab makes the first row
    /// current, Shift+Tab leaves).</summary>
    private string PropertyGridTabEdge
    {
        get
        {
            var visible = VisibleNodes.ToList();
            if (visible.Count == 0) return "both";
            var i = GetSelectedVisibleIndex(visible);
            if (i < 0) return "first";
            var atFirst = i == 0;
            var atLast = i == visible.Count - 1;
            return atFirst && atLast ? "both" : atFirst ? "first" : atLast ? "last" : "none";
        }
    }

    /// <summary>VB6: a grid always has a current cell. A property grid that receives
    /// focus with rows but no current row makes the first row current, so the first
    /// Tab / Down / Enter acts on it. (Explorer trees keep their no-selection state —
    /// selecting there fires their RowSelected side effects.)</summary>
    private async Task HandleRootFocusAsync()
    {
        if (!IsPropertyGrid || _selectedItem is not null) return;
        var visible = VisibleNodes.ToList();
        if (visible.Count > 0) await SelectNodeAsync(visible[0], 0);
    }

    /// <summary>Tab in a property grid walks the rows (vsFlexGrid TabBehavior=1). At
    /// the first / last row the key belongs to the page graph — it took it in the
    /// capture phase, and a Tab that still arrives here (the edge attribute one render
    /// stale) does nothing rather than re-selecting the current row.</summary>
    private async Task MoveSelectionForTabAsync(int delta)
    {
        var visible = VisibleNodes.ToList();
        if (visible.Count == 0) return;
        var i = GetSelectedVisibleIndex(visible);
        var target = i < 0 ? (delta > 0 ? 0 : -1) : i + delta;
        if (target < 0 || target >= visible.Count) return;
        await SelectVisibleNodeAsync(target, visible);
    }

    // The generation of the last edit an EDITOR (or the page, through CloseEditor)
    // ended — as opposed to the tree ending it (selection moved, focus left).
    private int _generationClosedByEditor = -1;

    /// <summary>The guarded focus step a hosted editor asks for (ICellEditorHost.
    /// FocusAsync): focus the element only while the tree still owns the keyboard. When
    /// focus has moved on while the editor was mounting (a Tab at the last row, a click
    /// elsewhere) the editor stays unfocused and the edit ends, as when focus leaves.</summary>
    private async Task FocusCellEditorAsync(TreeGridCellEditContext context, ElementReference element, bool selectText)
    {
        if (!IsCurrentContext(context) || _disposed) return;
        var module = await GetLegacyScrollModuleAsync();
        if (module is null)
        {
            try { await element.FocusAsync(preventScroll: true); } catch { }
            return;
        }
        bool focused;
        try { focused = await module.InvokeAsync<bool>("focusIfTreeOwnsFocus", _treeGridElement, element, selectText); }
        catch { return; }
        if (!focused && IsCurrentContext(context) && context.IsEditing && IsEditingCell)
        {
            EndEditingCore();
            StateHasChanged();
        }
    }

    private bool CellOffersEdit(TreeNode<TValue> node, TreeGridColumn col) =>
        col.CellEditTemplate != null
        && node.Data != null
        && (col.CellEditPredicate == null || col.CellEditPredicate(node.Data));

    /// <summary>The selected row's editable cell — the one Enter, F2, typing and a
    /// second click act on. Row mode: the row's ONE column offering an edit (a
    /// property grid); Cell mode: the cursor column when it offers one.</summary>
    private (TreeNode<TValue> Node, TreeGridColumn Column)? CurrentEditableCell()
    {
        var node = SelectedNode();
        if (node is null) return null;
        if (SelectionMode == SelectionMode.Cell)
        {
            var cols = VisibleColumns;
            var ci = CurrentCellColumnIndex;
            return ci < cols.Count && CellOffersEdit(node, cols[ci]) ? (node, cols[ci]) : null;
        }
        var editable = VisibleColumns.Where(c => CellOffersEdit(node, c)).Take(2).ToList();
        return editable.Count == 1 ? (node, editable[0]) : null;
    }

    // The selected row's node, found once per (flat list, selection) pair: the render
    // asks for it per cell of every selected row. The flat list is only ever replaced
    // whole, so a list identity plus the item identify the answer; a node whose
    // record was swapped underneath is looked up again.
    private List<TreeNode<TValue>>? _selectedNodeList;
    private TValue? _selectedNodeItem;
    private TreeNode<TValue>? _selectedNode;

    private TreeNode<TValue>? SelectedNode()
    {
        if (_selectedItem is null) return null;
        var same = EqualityComparer<TValue>.Default;
        if (!ReferenceEquals(_selectedNodeList, _flatNodes)
            || !same.Equals(_selectedNodeItem, _selectedItem)
            || (_selectedNode is not null && !same.Equals(_selectedNode.Data, _selectedItem)))
        {
            _selectedNodeList = _flatNodes;
            _selectedNodeItem = _selectedItem;
            _selectedNode = _flatNodes.FirstOrDefault(n => same.Equals(n.Data, _selectedItem));
        }
        return _selectedNode;
    }

    private bool IsCellEditHost(TreeNode<TValue> node, TreeGridColumn col) =>
        CurrentEditableCell() is { } cell && Equals(cell.Node.Id, node.Id)
        && string.Equals(cell.Column.Field, col.Field, StringComparison.Ordinal);

    /// <summary>The host object for the current editable cell (null for every other
    /// cell). Derived from the selection each render and cached per generation, so
    /// the cascaded object is stable across renders and a stale editor's context is
    /// a different instance.</summary>
    private TreeGridCellEditContext? GetCellEditContext(TreeNode<TValue> node, TreeGridColumn col)
    {
        if (!IsCellEditHost(node, col)) return null;
        var editing = IsEditingCell && Equals(_editingNodeId, node.Id)
            && string.Equals(_editingField, col.Field, StringComparison.Ordinal);
        if (_cellEditContext is { } c && c.Generation == _cellEditGeneration
            && Equals(c.NodeId, node.Id) && string.Equals(c.Field, col.Field, StringComparison.Ordinal)
            && c.IsEditing == editing)
            return c;

        var context = new TreeGridCellEditContext
        {
            Item = (object)node.Data!,
            Field = col.Field,
            NodeId = node.Id,
            IsEditing = editing,
            OpenOnRender = editing && _editingOpenOnRender,
            InitialText = editing ? _editingInitialText : null,
            Generation = _cellEditGeneration,
            CloseEditor = null!,
            EditorKeyDown = null!,
            FocusEditor = null!,
        };
        // The callbacks belong to THIS context: once it is no longer current they
        // do nothing (a late close from the previous row's editor cannot close the
        // next row's).
        context = new TreeGridCellEditContext
        {
            Item = context.Item, Field = context.Field, NodeId = context.NodeId,
            IsEditing = context.IsEditing, OpenOnRender = context.OpenOnRender,
            InitialText = context.InitialText, Generation = context.Generation,
            CloseEditor = () => EndCellEdit(context!),
            EditorKeyDown = e => HandleCellEditorKeyDownAsync(context!, e),
            FocusEditor = (element, selectText) => FocusCellEditorAsync(context!, element, selectText),
        };
        _cellEditContext = context;
        return context;
    }

    // Current = the context the last render handed out AND still of the tree's phase:
    // every phase change bumps the generation before the render that replaces the
    // cached context, and a key or close from the old editor in that gap is stale.
    /// <summary>The current cell's context for this phase — the one the render hands
    /// the editor (cached per generation), looked up rather than read from the cache
    /// so a phase change not yet rendered is never answered by the old context.</summary>
    private TreeGridCellEditContext? CurrentCellEditContext() =>
        CurrentEditableCell() is { } cell ? GetCellEditContext(cell.Node, cell.Column) : null;

    private bool IsCurrentContext(TreeGridCellEditContext context) =>
        ReferenceEquals(context, _cellEditContext) && context.Generation == _cellEditGeneration;

    /// <summary>Starts editing the current editable cell. False when the selected
    /// row has none. An edit already open on that cell is kept, unless the popup was
    /// asked for (a second click on the current cell while the editor swap was in
    /// flight): then the editor remounts with its list / calendar open.</summary>
    private bool BeginCellEdit(bool openPopup, string? initialText = null)
    {
        if (CurrentEditableCell() is not { } cell) return false;
        var sameCell = IsEditingCell && Equals(_editingNodeId, cell.Node.Id)
            && string.Equals(_editingField, cell.Column.Field, StringComparison.Ordinal);
        if (sameCell && !openPopup) return true;

        _editingNodeId = cell.Node.Id;
        _editingField = cell.Column.Field;
        _editingOpenOnRender = openPopup;
        _editingInitialText = initialText;
        _cellEditGeneration++;
        StateHasChanged();
        return true;
    }

    /// <summary>Ends the edit owned by <paramref name="context"/>: the cell returns
    /// to the cursor phase (a new generation, so the editor remounts closed) and
    /// the tree takes the keyboard back after the render — only when focus is still
    /// inside the tree or was lost with the editor, never when it moved elsewhere
    /// on the page. A cursor-phase context (an editor the mouse opened) only asks
    /// for that refocus. A context that is no longer current is ignored.</summary>
    private void EndCellEdit(TreeGridCellEditContext context)
    {
        if (!IsCurrentContext(context)) return;
        if (context.IsEditing && IsEditingCell)
        {
            _generationClosedByEditor = context.Generation;
            EndEditingCore();
        }
        _refocusAfterCellEditClose = true;
        StateHasChanged();
    }

    private void EndEditingCore()
    {
        _editingNodeId = null;
        _editingField = null;
        _editingOpenOnRender = false;
        _editingInitialText = null;
        _cellEditGeneration++;
    }

    /// <summary>Editing ends implicitly when the selection or the cursor column
    /// leaves the cell being edited (the old editor unmounts; its blur has already
    /// committed).</summary>
    private void SyncCellEditWithCursor()
    {
        if (!IsEditingCell) return;
        var cell = CurrentEditableCell();
        if (cell is { } c && Equals(_editingNodeId, c.Node.Id)
            && string.Equals(_editingField, c.Column.Field, StringComparison.Ordinal))
            return;
        _editingNodeId = null;
        _editingField = null;
        _editingOpenOnRender = false;
        _editingInitialText = null;
        _cellEditGeneration++;
        _refocusAfterCellEditClose = true;
    }

    /// <summary>Ends the in-cell edit, if one is open. In the cursor phase this is a
    /// no-op: the current cell keeps its closed editor.</summary>
    public void ClearActiveCellEdit()
    {
        if (!IsEditingCell) return;
        EndEditingCore();
        _refocusAfterCellEditClose = true;
        StateHasChanged();
    }

    /// <summary>A click on an editable cell's display. On another row (or another
    /// column in Cell mode) it is the first click: select, cursor phase, keyboard on
    /// the tree. On the current cell it is the second click: start editing with the
    /// popup open (the two-click contract — the display button stays mounted under
    /// the inert cursor-phase editor, so a fast second click always lands here).</summary>
    private async Task ActivateCellEditFromClickAsync(TreeNode<TValue> node, TreeGridColumn col, MouseEventArgs e)
    {
        // The display button is a MOUSE target. It keeps DOM focus for the round
        // trips until the editor takes it, so Enter / Space pressed then also fire
        // the button's activation click (Detail 0): that key already reached the
        // tree root, a second BeginCellEdit would restart the edit it just ended.
        if (e.Detail == 0) return;
        // Same rule as a row click: an invalid built-in edit on another row keeps the
        // selection, so the click does nothing rather than half-moving the cursor.
        if (_activeEdit is not null && !await FinishActiveEditorAsync()) return;

        var colIndex = VisibleColumns.IndexOf(col);
        var isSelected = _selectedItem != null && EqualityComparer<TValue>.Default.Equals(node.Data, _selectedItem);
        var isCurrentCell = isSelected && (SelectionMode != SelectionMode.Cell || colIndex == CurrentCellColumnIndex);

        if (colIndex >= 0) _activeCellColumnIndex = colIndex;
        if (!isSelected)
        {
            var visibleIndex = VisibleNodes.ToList().FindIndex(n => Equals(n.Id, node.Id));
            await SelectNodeAsync(node, visibleIndex);
        }
        else
            SyncCellEditWithCursor();

        if (isCurrentCell)
        {
            BeginCellEdit(openPopup: true);
            return;
        }
        // The clicked display button is about to be covered by the closed editor;
        // the keyboard must stay on the tree.
        _pendingTreeFocus = true;
        StateHasChanged();
    }

    /// <summary>The keys an in-cell editor did NOT consume (vsFlexGrid editing):
    /// Enter commits and the cursor stays on the row (an Enter that moved to the
    /// next row was a page-rolled bug); Escape leaves the edit; Up/Down end the
    /// edit and move the row; Tab / Shift+Tab end it and move to the next / previous
    /// row (a Cell-mode tree: Tab is the page's — the tree is one stop); Right / Left
    /// end it and act on the outline — VB6 gProperties_KeyDown: Right expands a
    /// collapsed folder or goes to the first child, Left collapses an expanded folder
    /// or goes to the parent (a Cell-mode tree: the cell cursor). Which of these an
    /// editor forwards is the editor's decision: a text editor keeps its arrows for
    /// the caret, an open popup keeps its own. A Tab from an open list whose pick
    /// closed the edit first (a page ValueChanged handler calling CloseEditor — the
    /// Inbox shape) still moves: that context is one generation old and was ended by
    /// the editor side, not by the tree.</summary>
    private async Task HandleCellEditorKeyDownAsync(TreeGridCellEditContext context, KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey) return;
        var navigation = e.Key is "Tab" or "ArrowDown" or "Down" or "ArrowUp" or "Up" or "ArrowLeft" or "Left" or "ArrowRight" or "Right";
        if (navigation && e.ShiftKey && e.Key != "Tab") return;
        var current = IsCurrentContext(context);
        var justEnded = !current && e.Key == "Tab" && !IsEditingCell
            && context.Generation == _cellEditGeneration - 1 && context.Generation == _generationClosedByEditor;
        if (!current && !justEnded) return;

        switch (e.Key)
        {
            case "Escape":
            case "Enter":
            case "NumpadEnter":
                if (current) EndCellEdit(context);
                break;
            case "Tab":
                if (current) EndCellEdit(context);
                if (SelectionMode == SelectionMode.Row)
                    await MoveSelectionForTabAsync(e.ShiftKey ? -1 : 1);
                break;
            case "ArrowDown":
            case "Down":
            case "ArrowUp":
            case "Up":
                if (current) EndCellEdit(context);
                await MoveSelectionAsync(e.Key is "ArrowDown" or "Down" ? 1 : -1);
                break;
            case "ArrowRight":
            case "Right":
                if (current) EndCellEdit(context);
                if (SelectionMode == SelectionMode.Cell)
                    MoveCellCursor(1);
                else
                    await ExpandOrMoveToChildAsync();
                break;
            case "ArrowLeft":
            case "Left":
                if (current) EndCellEdit(context);
                if (SelectionMode == SelectionMode.Cell)
                    MoveCellCursor(-1);
                else
                    await CollapseOrMoveToParentAsync();
                break;
        }

        // The key may have come from a JS-invoked commit (a buffered TextBox), not a
        // Blazor event on the tree: nothing re-renders the tree after this method on
        // its own, so the move is painted here.
        if (navigation) StateHasChanged();
    }

    /// <summary>Keys that reach the tree root while an editor is editing: they were
    /// pressed before the editor took focus (its mount is one or more round trips
    /// away) and are handed to it so nothing typed is lost.</summary>
    private static bool IsEditorRelayKey(KeyboardEventArgs e)
    {
        if (e.CtrlKey || e.MetaKey) return false;
        if (e.AltKey) return e.Key is "ArrowDown" or "Down";
        if (e.Key is { Length: 1 } && !char.IsControl(e.Key[0])) return true;
        return e.Key is "Enter" or "NumpadEnter" or "Escape" or "Tab" or "F4"
            or "ArrowUp" or "Up" or "ArrowDown" or "Down" or "ArrowLeft" or "Left" or "ArrowRight" or "Right";
    }

    private static bool IsTypedCharacter(KeyboardEventArgs e) =>
        !e.AltKey && !e.CtrlKey && !e.MetaKey
        && e.Key is { Length: 1 } && !char.IsControl(e.Key[0]) && !char.IsWhiteSpace(e.Key[0]);

    /// <summary>Focus left the tree while a cell was being edited — a page-level Tab,
    /// a click elsewhere: the edit ends (the editor's own blur has committed) and
    /// focus is left where it went. Ignored for an edit that already ended.</summary>
    [JSInvokable]
    public Task OnCellEditFocusLeftAsync(int generation)
    {
        if (IsEditingCell && generation == _cellEditGeneration)
        {
            EndEditingCore();
            _refocusAfterCellEditClose = true;
            StateHasChanged();
        }
        return Task.CompletedTask;
    }

    private async Task HandleRowDoubleClick(TreeNode<TValue> node, int visibleIndex)
    {
        if (EditSettingsRef?.AllowEditing == true && EditSettingsRef.AllowEditOnDblClick)
        { await BeginEditFromUiAsync(node.Id!, VisibleColumns.ElementAtOrDefault(CurrentCellColumnIndex)?.Field); return; }
        await SelectNodeAsync(node, visibleIndex);

        if (RowDoubleClicked.HasDelegate)
            await RowDoubleClicked.InvokeAsync(CreateRowEventArgs(node, visibleIndex));
    }

    // VB6 BeforeMouseDown parity: right-click first moves the selection to the row
    // under the cursor, then notifies the host — but ONLY when a host subscribed,
    // so unsubscribed consumers keep the exact previous right-click behavior.
    private async Task HandleRowContextMenu(TreeNode<TValue> node, int visibleIndex)
    {
        if (!RowRightClicked.HasDelegate)
            return;

        await SelectNodeAsync(node, visibleIndex);
        await RowRightClicked.InvokeAsync(CreateRowEventArgs(node, visibleIndex));
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (_activeEdit is not null) return;

        if (IsEditingCell)
        {
            // The editor owns the keyboard. A key that still reached the tree root
            // was pressed before the editor took focus: hand it over. A template that
            // mounted no host-aware editor (no relay) gets the tree's own editing keys,
            // so Escape / Enter always end the edit and Up / Down always move. Anything
            // else (Ctrl+S and friends) bubbles on to the page.
            if (IsEditorRelayKey(e) && CurrentCellEditContext() is { IsEditing: true } ctx)
                await (ctx.Relay is { } relay ? relay(e) : HandleCellEditorKeyDownAsync(ctx, e));
            return;
        }

        if (e.Key == "F2" || (e.Key == "Enter" && EditSettingsRef?.EditOnEnterKey == true))
        {
            if (EditSettingsRef?.AllowEditing != true)
            {
                // No built-in (Telerik-style) editing: F2 starts the current cell's
                // in-cell editor instead, mounted closed.
                if (e.Key == "F2") BeginCellEdit(openPopup: false);
                return;
            }
            if (_selectedItem is not null) await BeginEditFromUiAsync(RecordId(_selectedItem), VisibleColumns.ElementAtOrDefault(CurrentCellColumnIndex)?.Field);
            return;
        }
        if (e.Key is " " or "Spacebar" && ShowCheckboxes && _selectedItem is not null)
        { var id = RecordId(_selectedItem); await SetRowCheckedAsync(id, !_checkedKeys.Contains(id)); return; }
        if (e.CtrlKey && e.Key is "ArrowRight" or "ArrowLeft" && AllowRowDragAndDrop)
        { if (e.Key == "ArrowRight") await IndentSelectedAsync(); else await OutdentSelectedAsync(); return; }

        // The form's own keys (VB6 gData_KeyDown, e.g. Delete clears the value) —
        // only while no editor is open, as in VB6 (KeyDownEdit is the editor's).
        if (OnHostKeyDown.HasDelegate)
            await OnHostKeyDown.InvokeAsync(e);

        if (EditSettingsRef?.AllowEditing != true)
        {
            // vsFlexGrid Editable=flexEDKbdMouse: typing starts the edit with that
            // character (Space keeps its expand/collapse meaning); F4 / Alt+Down open
            // the current cell's list or calendar.
            if (IsTypedCharacter(e) && BeginCellEdit(openPopup: false, initialText: e.Key))
                return;
            if ((e.Key == "F4" || (e.AltKey && e.Key is "ArrowDown" or "Down")) && BeginCellEdit(openPopup: true))
                return;
        }

        if (IsPropertyGrid && e.Key == "Tab" && !e.AltKey && !e.CtrlKey && !e.MetaKey)
        {
            await MoveSelectionForTabAsync(e.ShiftKey ? -1 : 1);
            return;
        }

        switch (e.Key)
        {
            case "ArrowDown":
            case "Down":
                await MoveSelectionAsync(1);
                break;
            case "ArrowUp":
            case "Up":
                await MoveSelectionAsync(-1);
                break;
            case "Home":
                await SelectVisibleNodeAsync(0);
                break;
            case "End":
                await SelectVisibleNodeAsync(VisibleNodes.ToList().Count - 1);
                break;
            case "Enter":
            case "NumpadEnter":
                // Enter on the current editable cell starts its editor with the
                // list / calendar open (VB6 Enter on a combo cell drops the list).
                if (EditSettingsRef?.AllowEditing != true && BeginCellEdit(openPopup: true))
                    break;
                await ActivateSelectedNodeAsync();
                break;
            case " ":
            case "Spacebar":
                // VB6 VSFlexGrid parity: Space toggles the current node's
                // collapse/expand state (FInboxJobs gData_KeyDown vbKeySpace).
                await ToggleSelectedNodeAsync();
                break;
            case "ArrowRight":
            case "Right":
                if (SelectionMode == SelectionMode.Cell)
                    await CellCursorRightAsync();
                else
                    await ExpandOrMoveToChildAsync();
                break;
            case "ArrowLeft":
            case "Left":
                if (SelectionMode == SelectionMode.Cell)
                    await CellCursorLeftAsync();
                else
                    await CollapseOrMoveToParentAsync();
                break;
        }
    }

    // ── Cell-mode column cursor (vsFlexGrid free-cell navigation) ─────────────
    // Left/Right move the highlighted cell across visible columns (the cursor
    // survives row moves, like vsFlexGrid's Col). Explorer behavior is kept on
    // the TREE column: Right first expands a collapsed parent, Left collapses /
    // walks to the parent.
    private int _activeCellColumnIndex = -1;

    private int CurrentCellColumnIndex
    {
        get
        {
            var max = Math.Max(0, VisibleColumns.Count - 1);
            var idx = _activeCellColumnIndex < 0 ? ResolvedTreeColumnIndex : _activeCellColumnIndex;
            return Math.Clamp(idx, 0, max);
        }
    }

    private bool IsCursorColumn(int colIdx) =>
        SelectionMode == SelectionMode.Cell && colIdx == CurrentCellColumnIndex;

    private async Task CellCursorRightAsync()
    {
        if (CurrentCellColumnIndex == ResolvedTreeColumnIndex)
        {
            var visible = VisibleNodes.ToList();
            var i = GetSelectedVisibleIndex(visible);
            if (i >= 0 && visible[i].HasChildren && !visible[i].IsExpanded)
            {
                await ExpandOrMoveToChildAsync();
                return;
            }
        }
        MoveCellCursor(1);
    }

    private async Task CellCursorLeftAsync()
    {
        if (CurrentCellColumnIndex != ResolvedTreeColumnIndex && CurrentCellColumnIndex > 0)
        {
            MoveCellCursor(-1);
            return;
        }
        await CollapseOrMoveToParentAsync();
    }

    private void MoveCellCursor(int delta)
    {
        var max = Math.Max(0, VisibleColumns.Count - 1);
        _activeCellColumnIndex = Math.Clamp(CurrentCellColumnIndex + delta, 0, max);
        SyncCellEditWithCursor();
        StateHasChanged();
    }

    private async Task ToggleSelectedNodeAsync()
    {
        var visible = VisibleNodes.ToList();
        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
            return;

        var node = visible[selectedIndex];
        if (!node.HasChildren)
            return;

        _pendingTreeFocus = true;
        await ToggleNode(node);
    }

    private async Task MoveSelectionAsync(int delta)
    {
        var visible = VisibleNodes.ToList();
        if (visible.Count == 0)
            return;

        var selectedIndex = GetSelectedVisibleIndex(visible);
        var targetIndex = selectedIndex < 0
            ? (delta >= 0 ? 0 : visible.Count - 1)
            : Math.Clamp(selectedIndex + delta, 0, visible.Count - 1);

        await SelectVisibleNodeAsync(targetIndex, visible);
    }

    private async Task SelectVisibleNodeAsync(int index, List<TreeNode<TValue>>? visible = null)
    {
        visible ??= VisibleNodes.ToList();
        if (visible.Count == 0 || index < 0 || index >= visible.Count)
            return;

        var node = visible[index];
        _pendingKeyboardFocusNode = node;
        await SelectNodeAsync(node, index);
    }

    private async Task ActivateSelectedNodeAsync()
    {
        var visible = VisibleNodes.ToList();
        if (visible.Count == 0)
            return;

        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        _pendingTreeFocus = true;
        if (RowActivated.HasDelegate)
            await RowActivated.InvokeAsync(CreateRowEventArgs(node, selectedIndex));
        else if (RowDoubleClicked.HasDelegate)
            await RowDoubleClicked.InvokeAsync(CreateRowEventArgs(node, selectedIndex));
    }

    private async Task ExpandOrMoveToChildAsync()
    {
        var visible = VisibleNodes.ToList();
        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        if (!node.HasChildren)
            return;

        if (!node.IsExpanded)
        {
            _pendingTreeFocus = true;
            await SetNodeExpandedAsync(node, true);
            return;
        }

        var nextIndex = selectedIndex + 1;
        if (nextIndex < visible.Count && visible[nextIndex].Level == node.Level + 1)
            await SelectVisibleNodeAsync(nextIndex, visible);
    }

    private async Task CollapseOrMoveToParentAsync()
    {
        var visible = VisibleNodes.ToList();
        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        if (node.HasChildren && node.IsExpanded)
        {
            _pendingTreeFocus = true;
            await SetNodeExpandedAsync(node, false);
            return;
        }

        for (var i = selectedIndex - 1; i >= 0; i--)
        {
            if (visible[i].Level < node.Level)
            {
                await SelectVisibleNodeAsync(i, visible);
                return;
            }
        }
    }

    private int GetSelectedVisibleIndex(List<TreeNode<TValue>> visible)
    {
        if (_selectedItem == null)
            return -1;

        for (var i = 0; i < visible.Count; i++)
        {
            if (EqualityComparer<TValue>.Default.Equals(visible[i].Data, _selectedItem))
                return i;
        }

        return -1;
    }

    private async Task SelectNodeAsync(TreeNode<TValue> node, int visibleIndex)
    {
        if (!AllowSelection) return;
        if (_activeEdit is not null && !Equals(_activeEdit.Id, node.Id) && !await FinishActiveEditorAsync()) return;
        node = _flatNodes.FirstOrDefault(n => Equals(n.Id, node.Id)) ?? node;

        var prevSelected = _selectedItem;
        _selectedItem = node.Data;
        _selectedIndex = visibleIndex;
        // Selection moving off the row being edited (click, keyboard, or
        // programmatic) ends the in-cell edit.
        SyncCellEditWithCursor();
        if (AllowPaging && visibleIndex >= 0) _page = visibleIndex / PageSize + 1;

        if (prevSelected != null && RowDeselected.HasDelegate)
            await RowDeselected.InvokeAsync(new TreeRowSelectEventArgs<TValue> { Data = prevSelected });

        if (RowSelected.HasDelegate)
            await RowSelected.InvokeAsync(CreateRowEventArgs(node, visibleIndex));
    }

    private string GetRowCssClass(TreeNode<TValue> node, int visibleIndex)
    {
        if (RowCssClassSelector == null || node.Data == null)
            return string.Empty;

        try
        {
            return RowCssClassSelector.Invoke(node.Data, visibleIndex) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TreeRowSelectEventArgs<TValue> CreateRowEventArgs(TreeNode<TValue> node, int visibleIndex) =>
        new()
        {
            Data = node.Data,
            RowIndex = visibleIndex,
            Level = node.Level,
            HasChildren = node.HasChildren,
            IsExpanded = node.IsExpanded
        };

    public async Task SetExpandedAsync(TValue? item, bool expanded)
    {
        if (item == null)
            return;

        var node = _flatNodes.FirstOrDefault(n => EqualityComparer<TValue>.Default.Equals(n.Data, item));
        if (node != null)
            await SetNodeExpandedAsync(node, expanded);
    }

    // ── Public API ──────────────────────────────────────────────────────

    public async Task ExpandAllAsync()
    {
        if (!await FinishActiveEditorAsync()) return;
        foreach (var node in _flatNodes)
            if (node.HasChildren)
                node.IsExpanded = true;
        await InvokeAsync(StateHasChanged);
    }

    public async Task CollapseAllAsync()
    {
        if (!await FinishActiveEditorAsync()) return;
        foreach (var id in _loadingChildren.Keys) _expansionIntent[id] = false;
        foreach (var node in _flatNodes)
            if (node.HasChildren)
                node.IsExpanded = false;
        await InvokeAsync(StateHasChanged);
    }

    public bool HasExpandableNodes => _flatNodes.Any(node => node.HasChildren);

    public bool AreAllExpandableNodesExpanded =>
        HasExpandableNodes && _flatNodes.Where(node => node.HasChildren).All(node => node.IsExpanded);

    public async Task ExpandAtLevelAsync(int level)
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren && node.Level <= level)
                node.IsExpanded = true;
        await InvokeAsync(StateHasChanged);
    }

    public async Task CollapseAtLevelAsync(int level)
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren && node.Level >= level)
                node.IsExpanded = false;
        await InvokeAsync(StateHasChanged);
    }

    public TValue? GetSelectedRecord() => _selectedItem;

    public async Task FocusAsync(bool preventScroll = true)
    {
        try { await _treeGridElement.FocusAsync(preventScroll); } catch { }
    }

    public async Task SelectRecordAsync(TValue? item)
    {
        if (EqualityComparer<TValue>.Default.Equals(_selectedItem, item))
            return;

        var prevSelected = _selectedItem;
        _selectedItem = item;
        _selectedIndex = -1;
        SyncCellEditWithCursor();

        if (item != null)
        {
            var visible = VisibleNodes.ToList();
            for (var i = 0; i < visible.Count; i++)
            {
                if (EqualityComparer<TValue>.Default.Equals(visible[i].Data, item))
                {
                    _selectedIndex = i;
                    break;
                }
            }
        }

        if (prevSelected != null && RowDeselected.HasDelegate)
            await RowDeselected.InvokeAsync(new TreeRowSelectEventArgs<TValue> { Data = prevSelected });

        if (item != null && RowSelected.HasDelegate)
        {
            var node = _flatNodes.FirstOrDefault(n => EqualityComparer<TValue>.Default.Equals(n.Data, item));
            if (node != null)
            {
                await RowSelected.InvokeAsync(CreateRowEventArgs(node, _selectedIndex));
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearSelectionAsync()
    {
        if (_selectedItem == null && _selectedIndex < 0)
            return;

        var previous = _selectedItem;
        var previousIndex = _selectedIndex;
        _selectedItem = default;
        _selectedIndex = -1;
        SyncCellEditWithCursor();

        if (previous != null && RowDeselected.HasDelegate)
        {
            await RowDeselected.InvokeAsync(new TreeRowSelectEventArgs<TValue>
            {
                Data = previous,
                RowIndex = previousIndex
            });
        }

        await InvokeAsync(StateHasChanged);
    }

    // ── Column Sorting / Filtering / Options ────────────────────────────

    internal async Task HandleHeaderClickAsync(TreeGridColumn column, MouseEventArgs? e = null)
    {
        if (!AllowSorting || !column.AllowSorting || string.IsNullOrWhiteSpace(column.Field)) return;
        var state = GetColumnState(column);
        var next = state.SortDirection switch
        {
            null => SortDirection.Ascending,
            SortDirection.Ascending => SortDirection.Descending,
            _ => (SortDirection?)null
        };
        var sorts = AllowMultiSorting && e?.ShiftKey == true ? _sorts.ToList() : [];
        var index = sorts.FindIndex(s => s.Field == column.Field);
        if (index >= 0) sorts.RemoveAt(index);
        if (next.HasValue) sorts.Insert(index < 0 ? sorts.Count : index, new(column.Field, next.Value));
        await SetSortsAsync(sorts);
    }

    internal string GetHeaderCellCss(TreeGridColumn column)
    {
        var state = GetColumnState(column);
        var parts = new List<string> { "fx-treegrid-header-cell" };
        if (AllowSorting && column.AllowSorting && !string.IsNullOrWhiteSpace(column.Field))
            parts.Add("fx-treegrid-header-sortable");
        if (state.SortDirection.HasValue)
            parts.Add("fx-treegrid-sorted");
        if (HasFilter(state))
            parts.Add("fx-treegrid-filtered");
        if (AllowResizing && column.AllowResizing)
            parts.Add("fx-treegrid-resizable");
        return string.Join(" ", parts);
    }

    internal string GetSortGlyph(TreeGridColumn column) =>
        GetColumnState(column).SortDirection switch
        {
            SortDirection.Ascending => "▲",
            SortDirection.Descending => "▼",
            _ => ""
        };

    internal bool IsColumnFilterPopupOpen(TreeGridColumn column) =>
        !string.IsNullOrWhiteSpace(column.Field) &&
        string.Equals(_filterPopupField, column.Field, StringComparison.OrdinalIgnoreCase);

    internal void ToggleFilterPopup(TreeGridColumn column)
    {
        if (!AllowFiltering || !column.AllowFiltering || string.IsNullOrWhiteSpace(column.Field))
            return;

        if (IsColumnFilterPopupOpen(column))
        {
            _filterPopupField = null;
            return;
        }

        _filterPopupField = column.Field;
        var state = GetColumnState(column);
        _filterDraft = state.FilterValue ?? "";
        _filterOperatorDraft = state.FilterActive ? state.FilterOperator : column.Type == ColumnType.Text ? TextFilterOperator.Contains : TextFilterOperator.Equals;
        _secondFilterDraft = state.SecondFilterValue ?? "";
        _secondOperatorDraft = state.SecondFilterOperator;
        _logicalDraft = state.LogicalFilterOperator;
    }

    internal async Task ApplyFilterAsync(TreeGridColumn column)
    {
        await SetFilterAsync(new(column.Field, _filterOperatorDraft, _filterDraft,
            _secondOperatorDraft, _secondFilterDraft, _logicalDraft));
        _filterPopupField = null;
    }

    internal async Task ClearFilterAsync(TreeGridColumn column)
    {
        if (!await FinishActiveEditorAsync()) return;
        _columnStates.Remove(GetColumnKey(column));
        GetColumnState(column).SortDirection = _sorts.FirstOrDefault(s => s.Field == column.Field)?.Direction;
        _filterDraft = ""; _filterPopupField = null; _page = 1;
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearFiltersAsync()
    {
        if (!await FinishActiveEditorAsync()) return;
        foreach (var column in _columns) await ClearFilterAsync(column);
        await InvokeAsync(StateHasChanged);
    }

    internal async Task SetColumnVisibleAsync(TreeGridColumn column, bool visible)
    {
        if (!await FinishActiveEditorAsync()) return;
        if (string.IsNullOrWhiteSpace(column.Field))
            return;

        if (!visible && !CanHideColumn(column))
            return;

        _visibilityOverrides[column.Field] = visible;
        await InvokeAsync(StateHasChanged);
    }

    internal void StartColumnResize(TreeGridColumn column, MouseEventArgs e)
    {
        if (!AllowResizing || !column.AllowResizing)
            return;

        _isColumnResizing = true;
        _resizingColumn = column;
        _resizeStartX = e.ClientX;
        _resizeStartWidth = GetEffectiveColumnWidth(column);
    }

    internal async Task HandleColumnResizeMove(MouseEventArgs e)
    {
        if (!_isColumnResizing || _resizingColumn == null)
            return;

        var delta = e.ClientX - _resizeStartX;
        var minWidth = ParseCssPixels(_resizingColumn.MinWidth, 32);
        var width = Math.Max(minWidth, _resizeStartWidth + delta);
        _columnWidthOverrides[GetColumnKey(_resizingColumn)] = width;
        await InvokeAsync(StateHasChanged);
    }

    internal async Task EndColumnResize(MouseEventArgs e)
    {
        if (!_isColumnResizing)
            return;

        await HandleColumnResizeMove(e);
        _isColumnResizing = false;
        _resizingColumn = null;
        await InvokeAsync(StateHasChanged);
    }

    internal string GetHeaderStyle(TreeGridColumn column)
    {
        var parts = new List<string>();
        var width = GetEffectiveColumnWidth(column);
        if (width > 0)
            parts.Add($"width:{width:0.##}px");
        else if (!string.IsNullOrWhiteSpace(column.Width))
            parts.Add($"width:{column.Width}");

        if (!string.IsNullOrWhiteSpace(column.MinWidth))
            parts.Add($"min-width:{column.MinWidth}");
        parts.Add($"text-align:{column.ResolvedTextAlign.ToString().ToLowerInvariant()}");
        return string.Join(";", parts) + FrozenStyle(column);
    }

    internal string GetCellStyle(TreeGridColumn column)
    {
        var parts = new List<string>();
        var width = GetEffectiveColumnWidth(column);
        if (width > 0)
            parts.Add($"width:{width:0.##}px");
        else if (!string.IsNullOrWhiteSpace(column.Width))
            parts.Add($"width:{column.Width}");

        if (!string.IsNullOrWhiteSpace(column.MinWidth))
            parts.Add($"min-width:{column.MinWidth}");
        parts.Add($"text-align:{column.ResolvedTextAlign.ToString().ToLowerInvariant()}");
        return string.Join(";", parts) + FrozenStyle(column);
    }

    internal string? GetCellTitle(TValue? item, TreeGridColumn column)
    {
        if (item == null || string.IsNullOrWhiteSpace(column.Field))
            return null;

        var value = GetCellDisplayValue(item, column);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private bool HasActiveFilters => _columnStates.Values.Any(HasFilter);

    private void RebuildPreservingExpansion()
    {
        var expandStates = _flatNodes
            .Where(node => node.Id != null)
            .ToDictionary(node => node.Id!, node => node.IsExpanded);

        BuildTree();

        foreach (var node in _flatNodes)
        {
            if (node.Id != null && expandStates.TryGetValue(node.Id, out var expanded))
                node.IsExpanded = expanded;
        }
    }

    private void SortRows<TRow>(List<TRow> rows, Func<TRow, TValue> itemSelector)
    {
        if (_sorts.Count == 0) return;
        var ordered = GridLocalSortPipeline.Apply(rows, _sorts,
            (row, field) => GetPropertyValue(itemSelector(row), field), Comparer<object?>.Create(CompareValues)).ToList();
        rows.Clear(); rows.AddRange(ordered);
    }

    private static int CompareValues(object? left, object? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (left is string a && right is string b) return StringComparer.CurrentCultureIgnoreCase.Compare(a, b);
        if (left is IComparable comparable && left.GetType().IsInstanceOfType(right)) return comparable.CompareTo(right);
        return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
    }

    private HashSet<TreeNode<TValue>> BuildFilterInclusionSet()
    {
        var included = new HashSet<TreeNode<TValue>>();
        foreach (var node in _flatNodes)
        {
            if (!NodeMatchesFilters(node))
                continue;

            included.Add(node);
            if (FilterHierarchyMode is TreeGridFilterHierarchyMode.Parent or TreeGridFilterHierarchyMode.Both)
                IncludeNodeAndAncestors(node, included);
            if (FilterHierarchyMode is TreeGridFilterHierarchyMode.Child or TreeGridFilterHierarchyMode.Both)
                IncludeDescendants(node, included);
        }

        // Keep added drafts reachable even when the current query excludes their initial values.
        foreach (var added in _flatNodes.Where(n => Session(n)?.IsNew == true)) IncludeNodeAndAncestors(added, included);
        return included;
    }

    private bool NodeMatchesFilters(TreeNode<TValue> node) => GetFilters().All(filter =>
        TreeGridQuery.Matches(GetPropertyValue(node.Data, filter.Field),
            _columns.FirstOrDefault(c => c.Field == filter.Field)?.Type ?? ColumnType.Text, filter, FilterMatchCase));

    private void IncludeNodeAndAncestors(TreeNode<TValue> node, HashSet<TreeNode<TValue>> included)
    {
        included.Add(node);
        var parentId = node.ParentId;
        while (parentId != null)
        {
            var parent = _flatNodes.FirstOrDefault(n => Equals(n.Id, parentId));
            if (parent == null || !included.Add(parent))
                return;
            parentId = parent.ParentId;
        }
    }

    private void IncludeDescendants(TreeNode<TValue> node, HashSet<TreeNode<TValue>> included)
    {
        var children = _flatNodes.Where(n => Equals(n.ParentId, node.Id)).ToList();
        foreach (var child in children)
        {
            included.Add(child);
            IncludeDescendants(child, included);
        }
    }

    private double GetEffectiveColumnWidth(TreeGridColumn column)
    {
        var key = GetColumnKey(column);
        if (_columnWidthOverrides.TryGetValue(key, out var width))
            return width;

        return ParseCssPixels(column.Width, 0);
    }

    private static double ParseCssPixels(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];

        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels)
            ? pixels
            : fallback;
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private object? GetPropertyValue(TValue? item, string propertyName)
    {
        if (item == null || string.IsNullOrEmpty(propertyName)) return null;
        var prop = typeof(TValue).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(item);
    }

    private string GetCellDisplayValue(TValue item, TreeGridColumn col)
    {
        var val = GetPropertyValue(item, col.Field);
        if (val == null) return "";

        if (!string.IsNullOrEmpty(col.Format) && val is IFormattable formattable)
            return formattable.ToString(col.Format, System.Globalization.CultureInfo.CurrentCulture);

        return val.ToString() ?? "";
    }
}

// ── Supporting Types ────────────────────────────────────────────────────

public class TreeNode<TValue>
{
    public TValue Data { get; set; } = default!;
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; } = true;
    public object? Id { get; set; }
    public object? ParentId { get; set; }
    public bool IsLastSibling { get; set; }
    public IReadOnlyList<bool> AncestorLineContinuations { get; set; } = Array.Empty<bool>();
    public ElementReference RowElement { get; set; }
}

public class TreeRowSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; }
}

public class TreeNodeEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int Level { get; set; }
}
