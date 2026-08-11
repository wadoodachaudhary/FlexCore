using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Fx.ControlKit;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue> : FlexControlBase, IGridOwner, IAsyncDisposable
{
    void IGridOwner.RegisterColumnsContainer(GridColumnsBase container)
    {
        var changed = !ReferenceEquals(_columnsContainer, container);
        _columnsContainer = container;
        _autoWidthPending = true;
        if (changed)
            QueueColumnsChangedRender();
    }

    void IGridOwner.NotifyColumnsChanged()
    {
        _autoWidthPending = true;
        QueueColumnsChangedRender();
    }

    void IGridOwner.NotifyColumnsCompleted(int generation)
    {
        // Post-batch bookkeeping only (see IGridOwner) — a redraw here would
        // ship the columns in a second wire batch. Duplicate completions for
        // the same generation are dropped.
        if (generation == _completedColumnsGeneration)
            return;
        _completedColumnsGeneration = generation;
    }

    private int _completedColumnsGeneration;

    // Every GridColumn registration used to queue its OWN fire-and-forget
    // re-render, so mounting an N-column grid shipped a stack of render
    // batches — on a slow link the header visibly assembled column by
    // column (HHM-756). Coalesce: registrations arriving before the queued
    // render executes share one StateHasChanged.
    private bool _columnsChangedRenderQueued;

    private void QueueColumnsChangedRender()
    {
        if (_columnsChangedRenderQueued)
            return;
        _columnsChangedRenderQueued = true;
        _ = InvokeAsync(() =>
        {
            _columnsChangedRenderQueued = false;
            StateHasChanged();
        });
    }

    // ── Injectables ─────────────────────────────────────────────────────
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    // ── Parameters ───────────────────────────────────────────────────────

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public GridWidthMode WidthMode { get; set; } = GridWidthMode.FillAvailable;
    [Parameter] public bool EnableViewportSafeSizing { get; set; } = true;
    [Parameter] public string ViewportSafeMaxWidth { get; set; } = "";
    [Parameter] public string ViewportSafeMaxHeight { get; set; } = "";
    [Parameter] public EventCallback<MouseEventArgs> OnContextMenu { get; set; }
    [Parameter] public bool PreventDefaultContextMenu { get; set; }
    [Parameter] public bool StopContextMenuPropagation { get; set; }
    /// <summary>
    /// Adds a header-height scrollbar gutter so legacy picker grids look like
    /// the vertical scrollbar starts at the header instead of the first data row.
    /// </summary>
    [Parameter] public bool ExtendVerticalScrollbarIntoHeader { get; set; }
    [Parameter] public int RowHeight { get; set; }
    /// <summary>
    /// Allows end users to resize rows with a row-edge handle. Defaults off so
    /// existing application grids keep their current behavior unless they opt in.
    /// </summary>
    [Parameter] public bool AllowRowResizing { get; set; }
    /// <summary>
    /// Shows a fixed VB6/Excel-style row-header handle that lets users drag
    /// rows to a new position. Defaults off so existing grids keep their
    /// current layout and behavior unless they opt in.
    /// </summary>
    [Parameter] public bool AllowRowReorder { get; set; }
    [Parameter] public Func<TValue, bool>? RowReorderPredicate { get; set; }
    /// <summary>
    /// Shows a narrow VB6-style row-selector handle column. The handle is
    /// grid chrome, not a data column, and is off by default.
    /// </summary>
    [Parameter] public bool ShowRowSelectorHandle { get; set; }
    [Parameter] public GridRowSelectorHandleShape RowSelectorHandleShape { get; set; } = GridRowSelectorHandleShape.HalfButton;
    [Parameter] public int RowSelectorHandleWidth { get; set; } = 18;
    [Parameter] public Func<TValue, bool>? RowSelectorHandlePredicate { get; set; }
    [Parameter] public Func<TValue, bool>? RowSelectorHandleEmphasisPredicate { get; set; }
    [Parameter] public double MinRowHeight { get; set; } = 16;
    [Parameter] public Func<TValue, int, double?>? RowHeightSelector { get; set; }
    [Parameter] public Func<TValue, int, string?>? RowCssClassSelector { get; set; }

    // Feature flags
    [Parameter] public bool AllowSorting { get; set; }
    [Parameter] public bool AllowMultiSorting { get; set; }
    [Parameter] public bool AllowFiltering { get; set; }
    [Parameter] public int PageSize { get; set; } = 50;
    [Parameter] public int[] PageSizes { get; set; } = [25, 50, 100, 200];
    [Parameter] public int PageButtonCount { get; set; } = 5;
    [Parameter] public int AutoPageRowThreshold { get; set; } = 10000;
    /// <summary>
    /// Clears transient filters when the grid receives a different data source
    /// instance. This prevents one context's filter (for example one FAssembly
    /// community) from hiding rows after the host reloads for another context.
    /// </summary>
    [Parameter] public bool ClearFiltersOnDataSourceChange { get; set; } = true;
    /// <summary>
    /// When opted in, selects/focuses the first visible data row when a selectable
    /// grid first receives rows, and after sorting changes the visible row order.
    /// </summary>
    [Parameter] public bool AutoSelectFirstRow { get; set; }

    /// <summary>Opens the grid with its first cell as the current cell and the grid
    /// holding focus, so the first keystroke lands in the grid — the VSFlexGrid
    /// behaviour, where a grid always has a current cell. Re-applied for a few
    /// renders because rows usually arrive after the first one and other components
    /// on the page can take focus back; it stops as soon as a cell is current.</summary>
    [Parameter] public bool AutoFocusFirstCell { get; set; }
    /// <summary>
    /// Shows the clickable header filter icon that opens the filter popup.
    /// Disable this when the grid already renders explicit filter-bar inputs.
    /// </summary>
    [Parameter] public bool ShowHeaderFilterIcon { get; set; } = true;
    /// <summary>
    /// Optional glyph/text used for the clickable header filter icon. When
    /// blank, the grid renders a compact built-in filter glyph.
    /// </summary>
    [Parameter] public string HeaderFilterIcon { get; set; } = string.Empty;
    [Parameter] public bool AllowPaging { get; set; }
    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public bool HighlightSelectedRows { get; set; } = true;
    /// <summary>
    /// Enables spreadsheet-style row drag selection for multi-select row grids.
    /// Click selection is independent and always handled by <see cref="HandleRowClick"/>.
    /// </summary>
    [Parameter] public bool AllowRowDragSelection { get; set; } = true;
    /// <summary>Optional selected-row background color. When blank, the active theme decides.</summary>
    [Parameter] public string SelectedRowBackground { get; set; } = string.Empty;
    /// <summary>Optional selected-row foreground/text color. When blank, the active theme decides.</summary>
    [Parameter] public string SelectedRowForeground { get; set; } = string.Empty;
    /// <summary>Optional selected-row hover background. When blank, falls back to selected-row background.</summary>
    [Parameter] public string SelectedRowHoverBackground { get; set; } = string.Empty;
    /// <summary>Optional unselected-row hover background. When blank, the active theme decides.</summary>
    [Parameter] public string RowHoverBackground { get; set; } = string.Empty;
    /// <summary>
    /// When true, pressing Enter on the focused grid invokes
    /// <see cref="GridControlEvents{TValue}.OnRecordDoubleClick"/> for the
    /// current single selected row. This gives picker-style grids desktop
    /// behavior: click once to select, Enter to accept.
    /// </summary>
    [Parameter] public bool CommitSelectedRowOnEnter { get; set; }
    // Suppress the built-in "X rows selected … Clear Selection" banner
    // that appears above the grid header on multi-row selection. When
    // false, hosts can render their own selection toolbar above the
    // grid so its top edge stays at a fixed offset; otherwise the
    // banner pops in/out on first selection and the grid body shifts
    // vertically by ~32 px, which reads as visual "shaking" mid-drag.
    [Parameter] public bool ShowSelectionInfoBar { get; set; }
    /// <summary>
    /// Field that receives multi-row type-ahead when no row-selection gesture
    /// has captured an editable target column. Kept as the compatibility
    /// fallback for hosts that explicitly want the older fixed-target behavior.
    /// </summary>
    [Parameter] public string? TypeAheadTargetField { get; set; }
    /// <summary>
    /// Optional alias for the same fallback role as <see cref="TypeAheadTargetField"/>.
    /// <see cref="TypeAheadTargetField"/> wins when both are supplied.
    /// </summary>
    [Parameter] public string? TypeAheadFallbackField { get; set; }
    /// <summary>
    /// In row-selection grids, highlights the editable target column across the
    /// selected rows so bulk edits have an obvious destination. Defaults off so
    /// existing row-select grids keep their current visual behavior.
    /// </summary>
    [Parameter] public bool ShowRowSelectionEditColumnCue { get; set; }
    /// <summary>
    /// Controls how batch-mode editable cells behave. MultiRow keeps the
    /// legacy FlexKit behavior: single-click enters edit and can fan out to
    /// multiple selected rows. SingleCell gives desktop-grid behavior:
    /// click selects a cell, double-click edits, and typed keys commit to
    /// that one cell on Enter.
    /// </summary>
    [Parameter] public GridBatchEditBehavior BatchEditBehavior { get; set; } = GridBatchEditBehavior.MultiRow;
    /// <summary>
    /// Opens an editable batch cell on a plain mouse click. Defaults off so
    /// grids that use click-to-select/type-to-replace keep that behavior.
    /// VB6-style grids can opt in when mouse click should call EditCell.
    /// </summary>
    [Parameter] public bool EditOnSingleClick { get; set; }
    /// <summary>
    /// In <see cref="GridBatchEditBehavior.SingleCell"/> mode, allow Ctrl/Cmd
    /// and Shift selection across cells in one column, then commit typed
    /// type-ahead values to all selected cells in that column.
    /// </summary>
    [Parameter] public bool AllowSingleCellColumnMassEdit { get; set; }
    [Parameter] public bool AllowGrouping { get; set; }
    [Parameter] public bool AllowResizing { get; set; }

    /// <summary>Best fit: double-clicking a column's resize grip sizes it to its
    /// content, and the header menu offers "Best Fit" / "Best Fit All Columns".
    /// Requires <see cref="AllowResizing"/> — a column with no grip has nothing to
    /// double-click, though the menu items and the public API still work.</summary>
    [Parameter] public bool AllowColumnAutoFit { get; set; } = true;

    /// <summary>How many rows a best-fit measures, sampled around the scroll
    /// viewport rather than from the top, so one long value far off-screen cannot
    /// stretch the column.</summary>
    [Parameter] public int AutoFitSampleSize { get; set; } = 50;

    /// <summary>Floor for a best-fit width, in px. A column's own
    /// <see cref="GridColumn.MinWidth"/> wins when it sets one.</summary>
    [Parameter] public double AutoFitMinWidth { get; set; } = 40;

    /// <summary>Ceiling for a best-fit width, in px. A column's own
    /// <see cref="GridColumn.MaxWidth"/> wins when it sets one.</summary>
    [Parameter] public double AutoFitMaxWidth { get; set; }

    /// <summary>Measure real rendered text through the DOM instead of estimating from
    /// character counts. Exact, because clipped cells still report their full
    /// scrollWidth — but it needs the rows to be on screen. Falls back to the
    /// character estimate automatically whenever the measurement is unavailable.</summary>
    [Parameter] public bool AutoFitUseDomMeasurement { get; set; } = true;

    /// <summary>
    /// When true, text cell values that start with '=' are evaluated as
    /// row-scoped arithmetic formulas. Column formulas configured on
    /// <see cref="GridColumn.Formula"/> are always evaluated.
    /// </summary>
    [Parameter] public bool AllowCellFormulas { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;
    [Parameter] public bool EnableAltRow { get; set; } = true;
    [Parameter] public bool ShowSearchBar { get; set; }
    /// <summary>
    /// Enables VB6 VSFlexGrid-style read-only type search. When the focused
    /// grid receives printable keys, it searches the clicked/active column
    /// for the first row whose text starts with the typed buffer.
    /// </summary>
    [Parameter] public bool EnableTypeSearch { get; set; }
    /// <summary>
    /// Number of seconds before the type-search buffer resets. VB6 FPickList
    /// used AutoSearchDelay=3.
    /// </summary>
    [Parameter] public int TypeSearchDelaySeconds { get; set; } = 3;
    [Parameter] public GridLines GridLines { get; set; } = GridLines.Both;
    [Parameter] public List<string>? Toolbar { get; set; }

    /// <summary>
    /// Show the built-in grid toolbar for custom <see cref="Toolbar"/> items
    /// and the search bar. Defaults to <c>null</c> meaning auto-show whenever
    /// the caller supplied custom <see cref="Toolbar"/> items / <see cref="ShowSearchBar"/>.
    /// Set to <c>true</c> to force-show or <c>false</c> to force-hide.
    /// </summary>
    [Parameter] public bool? ShowGridToolbar { get; set; }

    /// <summary>
    /// Show the built-in Expand All icon button in the group drop area.
    /// Defaults to true when AllowGrouping is enabled.
    /// </summary>
    [Parameter] public bool ShowExpandAllButton { get; set; } = true;

    /// <summary>
    /// Show the built-in Collapse All icon button in the group drop area.
    /// Defaults to true when AllowGrouping is enabled.
    /// </summary>
    [Parameter] public bool ShowCollapseAllButton { get; set; } = true;

    /// <summary>
    /// Show a compact group-strip icon that toggles normal grid view versus
    /// the advanced view exposing filters, pivot, columns, and theme tools.
    /// Defaults off so grids only show the advanced surface when a host opts in.
    /// </summary>
    [Parameter] public bool ShowAdvancedViewToggleButton { get; set; }

    /// <summary>
    /// Initial advanced-view state when <see cref="ShowAdvancedViewToggleButton"/>
    /// is enabled. Defaults to false so opt-in grids still start in normal view
    /// unless the host explicitly asks for the advanced tools to be visible.
    /// </summary>
    [Parameter] public bool DefaultAdvancedView { get; set; }

    // Resolved visibility. The advanced toggle lives in the group drop area so
    // enabling it does not create a separate toolbar row above the grid.
    internal bool ShouldRenderToolbar =>
        ShowGridToolbar ?? ((Toolbar is { Count: > 0 }) || ShowSearchBar);

    /// <summary>Initial group columns (field names).</summary>
    [Parameter] public List<string>? GroupColumns { get; set; }

    /// <summary>
    /// Optional group header display resolver. The raw key remains unchanged for
    /// grouping/collapse state; this only changes the text painted in the group
    /// header row.
    /// </summary>
    [Parameter] public Func<string, string, IReadOnlyList<TValue>, string>? GroupHeaderTextSelector { get; set; }

    /// <summary>Aggregate row definitions for group footers and grid footer.</summary>
    [Parameter] public List<AggregateRow>? AggregateRows { get; set; }

    /// <summary>Show an Expand All / Collapse All toggle button in the group drop area.</summary>
    [Parameter] public bool ShowGroupExpandCollapse { get; set; } = true;

    /// <summary>Show the expression-filter search button in the group drop area.</summary>
    [Parameter] public bool ShowExpressionFilterButton { get; set; }

    /// <summary>Show the grid option rail for columns, pivot, and theme actions.</summary>
    [Parameter] public bool ShowGridOptionsRail { get; set; }

    /// <summary>Show the column-options button on the grid option rail.</summary>
    [Parameter] public bool ShowColumnOptionsButton { get; set; }

    /// <summary>
    /// Deprecated. Column filtering now lives in each column header popup, so
    /// the standalone filter-panel rail button is ignored.
    /// </summary>
    [Parameter] public bool ShowFilterPanelButton { get; set; }

    /// <summary>Allow this grid to switch into the embedded pivot view.</summary>
    [Parameter] public bool AllowPivoting { get; set; }

    /// <summary>Show the pivot-mode button on the grid option rail.</summary>
    [Parameter] public bool ShowPivotPanelButton { get; set; }

    /// <summary>Show the local dark/light theme toggle on the grid option rail.</summary>
    [Parameter] public bool ShowGridThemeToggle { get; set; }
    /// <summary>
    /// Initial grid color scheme. Users can change it at runtime from the Theme
    /// panel when <see cref="ShowGridThemeToggle"/> is enabled.
    /// </summary>
    [Parameter] public GridTheme Theme { get; set; } = GridTheme.Default;

    /// <summary>Show the back-to-grid button on the grid option rail.</summary>
    [Parameter] public bool ShowGridBackButton { get; set; }

    /// <summary>When true, columns currently used for grouping are hidden from the data grid.</summary>
    [Parameter] public bool HideGroupedColumns { get; set; } = true;

    /// <summary>When true, the user can drag a column header onto another to
    /// reorder columns. Default true. (Drag-to-group is always wired separately
    /// through the group drop area when <see cref="AllowGrouping"/> is on.)</summary>
    [Parameter] public bool AllowColumnReorder { get; set; } = true;

    /// <summary>
    /// Enables the built-in right-click column header menu. Hosts that need a
    /// legacy text-edit context menu can turn this off and handle the bubbled
    /// contextmenu event themselves.
    /// </summary>
    [Parameter] public bool EnableHeaderContextMenu { get; set; } = true;

    /// <summary>
    /// When true, the header context menu renders a compact checked column
    /// list instead of the standard group/hide/rename/print command set.
    /// </summary>
    [Parameter] public bool HeaderContextMenuShowsColumns { get; set; }

    /// <summary>
    /// Enables a lightweight Cut/Copy/Paste/Delete menu on data cells. Off by
    /// default so existing grids keep their current right-click behavior.
    /// </summary>
    [Parameter] public bool EnableCellContextMenu { get; set; }

    /// <summary>
    /// Color used for the vertical column-reorder insertion pipe. Defaults to a
    /// dark grey-black; callers can set any valid CSS color.
    /// </summary>
    [Parameter] public string ColumnReorderPipeColor { get; set; } = "#2b2b2b";

    /// <summary>
    /// When true, the header-menu Print command opens the small print-options
    /// dialog. When false, Print immediately exports a PDF with the configured
    /// default print options.
    /// </summary>
    [Parameter] public bool ShowPrintOptionsDialog { get; set; } = true;

    /// <summary>Default orientation for GridControl PDF print/export.</summary>
    [Parameter] public GridPdfOrientation DefaultPrintOrientation { get; set; } = GridPdfOrientation.Portrait;

    /// <summary>Default page size for GridControl PDF print/export.</summary>
    [Parameter] public GridPdfPageSize DefaultPrintPageSize { get; set; } = GridPdfPageSize.Letter;

    /// <summary>Default column layout for GridControl PDF print/export.</summary>
    [Parameter] public GridPdfColumnLayout DefaultPrintColumnLayout { get; set; } = GridPdfColumnLayout.WrapText;

    /// <summary>Default gridline visibility for GridControl PDF print/export.</summary>
    [Parameter] public bool DefaultPrintGridLines { get; set; } = true;

    /// <summary>Default zoom mode for GridControl PDF print/export.</summary>
    [Parameter] public GridPdfZoomMode DefaultPrintZoomMode { get; set; } = GridPdfZoomMode.FitToPage;

    /// <summary>Default zoom percentage used when <see cref="DefaultPrintZoomMode"/> is Percent.</summary>
    [Parameter] public int DefaultPrintZoomPercent { get; set; } = 100;

    /// <summary>
    /// Include grid footer aggregate rows in Print and Save As exports when
    /// the grid declares AggregateRows with ShowInFooter=true.
    /// </summary>
    [Parameter] public bool IncludeTotalsInExport { get; set; } = true;

    /// <summary>Optional schema for the Choose Columns dialog. Hosts that
    /// have a saved layout with columns the grid isn't currently rendering
    /// (e.g. legacy "Hidden=true" entries) pass them here so the dialog can
    /// list them with unchecked boxes. When null the dialog falls back to the
    /// rendered <c>&lt;GridColumn&gt;</c> children.</summary>
    [Parameter] public IEnumerable<ChooseColumnDescriptor>? AvailableColumns { get; set; }

    /// <summary>Optional "factory default" layout for the Choose Columns dialog's
    /// <em>Restore Default Layout</em> button. When supplied, clicking that button
    /// resets each listed column's checked state and order to match this schema
    /// (columns absent from it keep their current state). When null, Restore
    /// Default falls back to the in-memory snapshot taken when the dialog first
    /// opened. The grid is general-purpose: it neither knows nor cares where the
    /// host sourced these from (a service, a config, hardcoded) — it just applies
    /// them. Hosts populate this exactly like <see cref="AvailableColumns"/>.</summary>
    [Parameter] public IEnumerable<ChooseColumnDescriptor>? DefaultColumns { get; set; }

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
    /// Optional text color for the grouped value displayed on group-header
    /// rows. When empty, falls back to <see cref="GroupedColumnColor"/> for
    /// backward compatibility.
    /// </summary>
    [Parameter] public string GroupItemTextColor { get; set; } = "";

    /// <summary>
    /// Optional text color for aggregate totals rendered for a group: inline
    /// group-header totals, group-caption totals, and group-footer totals.
    /// Empty means the toolkit's default aggregate colors are used.
    /// </summary>
    [Parameter] public string GroupTotalTextColor { get; set; } = "";

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
            ? FxGridIconStyles.PlusMinus
            : null);   // Triangle → no inline style; CSS class drives it

    private string ResolvedGroupItemTextColor =>
        !string.IsNullOrWhiteSpace(GroupItemTextColor)
            ? GroupItemTextColor.Trim()
            : (string.IsNullOrWhiteSpace(GroupedColumnColor) ? "" : GroupedColumnColor.Trim());

    private string ResolvedGroupTotalTextColor =>
        string.IsNullOrWhiteSpace(GroupTotalTextColor) ? "" : GroupTotalTextColor.Trim();

    // A lighter tint of the resolved group item color used for the chip background.
    // Uses color-mix() so any caller-supplied colour produces a sensible soft
    // tint without needing colour-space math in C#.
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

            return IsPagingActive
                ? "overflow-x:auto; overflow-y:hidden; flex:1;"
                : "overflow:auto; flex:1;";
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

    // Inline style for the host div. Only emits the GroupedColumnColor CSS
    // variables when the caller actually supplied a color — empty-string
    // assignments (e.g. "--fx-grid-group-color: ;") suppress the var() fallback
    // in the stylesheet, leaving the chip background unset and invisible.
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

    private bool GridContextMenuPreventDefault => EnableCellContextMenu || PreventDefaultContextMenu || OnContextMenu.HasDelegate;

    private async Task HandleGridContextMenu(MouseEventArgs e)
    {
        if (OnContextMenu.HasDelegate)
            await OnContextMenu.InvokeAsync(e);
    }

    private string? ResolveViewportSafeSize(string? size, string maxSize)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var trimmed = size.Trim();
        if (!EnableViewportSafeSizing || string.IsNullOrWhiteSpace(maxSize))
            return trimmed;

        // Percentage-only sizes fill the parent. The CSS min-width/min-height
        // reset above lets the parent shrink instead of forcing page scroll.
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

    private bool IsCellSelectionRow(TValue item, int rowIndex)
    {
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell || _selectedCells.Count == 0)
            return false;

        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        return _selectedCells.Any(cell => cell.RowIndex == resolvedRowIndex);
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
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResizing { get; set; }
    [Parameter] public EventCallback<RowResizeEventArgs<TValue>> RowResized { get; set; }
    [Parameter] public EventCallback<RowReorderEventArgs<TValue>> RowReordering { get; set; }
    [Parameter] public EventCallback<RowReorderEventArgs<TValue>> RowReordered { get; set; }
    /// <summary>Factory used to create a new row item when adding rows.</summary>
    [Parameter] public Func<TValue>? NewItemFactory { get; set; }
    /// <summary>When enabled, keeps one real blank new row at the end of the grid.</summary>
    [Parameter] public bool EnsureTrailingNewRow { get; set; }
    /// <summary>Optional predicate that tells the grid whether its tracked trailing new row is still blank.</summary>
    [Parameter] public Func<TValue, bool>? IsTrailingNewRow { get; set; }
    /// <summary>When enabled, moving forward from the last cell appends a new row and focuses its first editable cell.</summary>
    [Parameter] public bool AddNewRowOnLastCellExit { get; set; }
    /// <summary>Optional guard for <see cref="AddNewRowOnLastCellExit"/>; return false to leave focus at the grid edge.</summary>
    [Parameter] public Func<TValue, bool>? CanAddNewRowOnLastCellExit { get; set; }
    /// <summary>Factory used to clone an existing row before editing.</summary>
    [Parameter] public Func<TValue, TValue>? CloneFactory { get; set; }

    // ── Internal State ───────────────────────────────────────────────────

    internal GridColumnsBase? _columnsContainer;
    private readonly Dictionary<string, ColumnState> _columnStates = new();
    private readonly PageState _pageState = new();
    private readonly HashSet<TValue> _selectedItems = new();
    private readonly HashSet<(int RowIndex, int CellIndex)> _selectedCells = new();
    private (int RowIndex, int CellIndex)? _activeCell;
    private (int RowIndex, int CellIndex)? _pointerFillCell;
    private bool _expandAllGroups;
    private bool _allGroupsCollapsed;
    private TValue? _trailingNewRowItem;
    private bool _hasTrailingNewRowItem;
    private bool _ensuringTrailingNewRow;
    private readonly HashSet<string> _collapsedGroupPaths = new(StringComparer.OrdinalIgnoreCase);
    private (int RowIndex, int CellIndex)? _lastSelectedCell;
    private int? _lastSelectedRowIndex;

    // Drag-select state. Mousedown on a row in SelectionType.Multiple
    // captures the anchor; mouseenter on a different row while the left
    // button is still held extends the selection range. Mouseup ends the
    // drag immediately and marks the trailing click event (browser fires
    // mousedown->mouseup->click) to be swallowed rather than collapsing
    // back to a single-row selection.
    private int? _dragAnchorRowIndex;
    // Drag/Shift-range selection works in VISIBLE row-index space, not raw
    // DataSource indices — see GetVisibleRowItems for why. We track the
    // anchor / last-selected items themselves so the index is recomputed
    // against the current visible list each time the user extends the
    // selection (the visible list shifts whenever a group collapses).
    private TValue? _dragAnchorItem;
    private TValue? _lastSelectedItem;
    private bool _isDragSelecting;
    private bool _suppressNextClickAfterDragSelect;
    private DateTime _suppressNextClickAfterDragSelectUntilUtc = DateTime.MinValue;
    private (int RowIndex, int CellIndex)? _cellDragAnchor;
    private bool _isCellDragSelecting;

    // Editing
    private bool _isEditing;
    private int _editingRowIndex = -1;
    private TValue? _editItem;

    // Batch editing (cell-level inline editing)
    private TValue? _batchEditItem;
    private int _batchEditRowIndex = -1;
    private string? _batchEditField;
    private string? _batchEditValue;
    // True only after the user actually mutated the input value via oninput.
    // Gates the multi-row fan-out so that auto-fired commits (Blazor blur
    // cascade, focus round-trips) at click time can't blast the cell's
    // pre-edit value across the rest of the selection.
    private bool _batchEditDirty;
    // Mouse single-click opens the editor without visibly selecting the text,
    // but the first typed character should still replace the selected cell's
    // full value. Double-click/editor-in-text starts leave this false.
    private bool _batchEditReplaceOnFirstInput;

    // Browser Tab can blur the input before Blazor Server processes the
    // keydown. Keep the just-committed cell so the Tab handler can still move
    // to the next displayed editable cell after blur has cleared live edit
    // state.
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

    // Set by StartBatchEdit. Consumed by OnAfterRenderAsync after the
    // batch input exists in the DOM so a single-click edit immediately
    // accepts typing instead of leaving focus on the grid host/cell.
    private bool _pendingBatchEditFocus;

    // Set by StartBatchEdit when the column has SelectAllOnEdit=true.
    // Consumed alongside _pendingBatchEditFocus to optionally select the
    // current value. Reset immediately so later renders do not re-select
    // while the user is typing.
    private bool _pendingBatchEditSelectAll;
    private double? _pendingBatchEditClientX;
    // True while OnAfterRenderAsync is applying focus/selection to a freshly
    // mounted batch editor (owner editor fix 2026-07-24) — blocks re-entry from
    // renders that occur during the await.
    private bool _batchEditFocusInFlight;
    private bool _batchDropdownOpenOnRender;
    private TValue? _mouseStartedBatchEditItem;
    private string? _mouseStartedBatchEditField;
    private DateTime _mouseStartedBatchEditUtc = DateTime.MinValue;
    private bool _suppressRetargetedBatchEditClick;
    private TValue? _mouseDownClosedDropdownItem;
    private string? _mouseDownClosedDropdownField;
    private DateTime _mouseDownClosedDropdownUtc = DateTime.MinValue;
    private bool _suppressMouseDownClosedDropdownOpenClick;
    private bool _suppressNextPointerSelectionAfterTypeAheadCommit;
    private bool _deferredTrailingNewRowEnsureRequested;
    private string _batchDropdownTypeSelectBuffer = "";
    private DateTime _batchDropdownTypeSelectLastInputUtc = DateTime.MinValue;
    private static readonly TimeSpan DropdownTypeSelectResetDelay = TimeSpan.FromSeconds(1);

    // Set by BeginEditCellAsync (programmatic edit, e.g. host "New row"): when
    // true the post-render focus is allowed to scroll the cell into view
    // (FocusAsync preventScroll:false) instead of the default preventScroll:true
    // used for mouse-click edits. This is how a newly-added off-screen row is
    // brought into view — pure Blazor focus, NO JavaScript scrollIntoView.
    private bool _pendingBatchEditScrollIntoView;
    private bool _pendingActiveCellScrollIntoView;

    // Element ref for the active batch-edit <input>. Captured in both
    // render paths (grouped + flat) so the post-render focus/select action
    // has something to target.
    private ElementReference _batchEditInputRef;

    // Lazy-imported ES module from wwwroot/grid-control.js. We only
    // pay the import round-trip the first time SelectAllOnEdit fires;
    // subsequent edits reuse the module reference.
    private IJSObjectReference? _gridJsModule;
    private ElementReference _gridHostElement;
    private PivotControl<TValue>? _pivotControlRef;
    private DotNetObjectReference<GridControl<TValue>>? _gridDotNetRef;
    private bool _headerDragPreviewRegistered;
    private bool _rowDragSelectionAutoScrollRegistered;
    private bool _gridKeyboardTrapRegistered;
    private bool _gridScrollSyncRegistered;
    private bool _scrollbarActivityRegistered;
    private bool _gridResizeCaptureRegistered;
    private bool _initialScrollResetOnFirstRenderPending = true;
    private bool _initialScrollResetOnFirstDataPending = true;
    private bool _pendingFirstRowSelection;
    private string? _focusedGroupPath;
    private int? _lastHostResolvedPageSize;
    private bool _renderPassActive;
    private List<GridColumn>? _renderVisibleColumns;
    private IList<TValue>? _renderRowIndexLookupSource;
    private Dictionary<object, int>? _renderRowIndexLookup;

    private enum GridScrollNavigationKey
    {
        PageUp,
        PageDown,
        Home,
        End
    }

    // Filtering popup
    private string? _filterPopupField;
    private double _filterPopupX;
    private double _filterPopupY;
    private string _filterTextDraft = "";
    private TextFilterOperator _filterOperatorDraft = TextFilterOperator.Contains;
    private bool _filterPopupAutoApply = true;
    private HashSet<string> _filterCheckedDraft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextFilterOperator> _filterOperatorDraftsByField = new(StringComparer.Ordinal);
    private IEnumerable<TValue>? _lastFilterDataSource;
    private DataSourceSelectionSignature _lastFilterDataSourceSignature;
    private bool _filterDataSourceCaptured;
    private ElementReference _filterConditionInputRef;
    private FilterPopupFocusTarget? _pendingFilterPopupFocusTarget;
    private IEnumerable<TValue>? _lastSelectionDataSource;
    private DataSourceSelectionSignature _lastSelectionDataSourceSignature;
    private bool _selectionDataSourceCaptured;
    private string FilterPopupStyle =>
        string.Create(CultureInfo.InvariantCulture,
            $"left:clamp(8px,{_filterPopupX + 8}px,calc(100vw - 360px));top:clamp(8px,{_filterPopupY + 10}px,calc(100vh - 440px));");

    private enum FilterPopupFocusTarget
    {
        ConditionInput
    }

    // Type-ahead buffer (multi-select numeric input)
    private string _typeAheadBuffer = "";
    private bool _rowSelectionTypeAheadTargetCaptured;
    private string? _rowSelectionTypeAheadTargetField;

    private readonly record struct DataSourceSelectionSignature(int Count, int Fingerprint);

    // Search
    private string? SearchText;
    private CancellationTokenSource? _searchCts;
    private string? _exportStatusMessage;
    private int _exportStatusGeneration;

    // ── Grouping State ───────────────────────────────────────────────────
    private readonly List<GroupDescriptor> _groupDescriptors = new();
    private string? _draggingColumnField;
    private string? _draggingGroupChipField;
    private bool _dragOverGroupArea;
    private string? _dragOverHeaderField;
    private bool _dragInsertAfterTarget;
    private string? _lastHeaderDragSourceField;
    private DateTime _lastHeaderDragStartedUtc;
    private int _headerDragGeneration;

    // ── Resize State ─────────────────────────────────────────────────────
    private GridColumn? _resizingCol;
    private double _resizeStartX;
    private double _resizeStartWidth;

    // Double-click on the grip is detected from two mousedowns rather than from the
    // DOM's dblclick event. A real hand jitters a pixel or two, which resizes the
    // column, which fires OnLayoutChanged, which makes hosts that re-key their
    // columns rebuild the header — and a browser only fires dblclick when both
    // clicks land on the SAME element, so the rebuilt grip never sees one. mousedown
    // always arrives before the rebuild, so this path survives it.
    private string? _lastGripDownField;
    private DateTime _lastGripDownAt;
    private const int GripDoubleClickMs = 500;

    /// <summary>Movement under this many px is hand tremor, not a resize.</summary>
    private const double GripJitterPx = 3;

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

    // ── Computed Properties ──────────────────────────────────────────────

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
            var cols = EffectiveColumns.Where(IsColumnVisible);
            if (HideGroupedColumns && _groupDescriptors.Count > 0)
            {
                var groupedFields = _groupDescriptors.Select(g => g.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
                cols = cols.Where(c => !groupedFields.Contains(c.Field));
            }
            return cols;
        }
    }

    private long _renderPassStartTimestamp;

    private void BeginGridRenderPass()
    {
        _renderPassStartTimestamp = GridRenderDiagnostics.BeginPass();
        EnsureAutoColumnWidthsBeforeRender();
        _renderPassActive = true;
        _renderVisibleColumns = null;
        _renderRowIndexLookupSource = null;
        _renderRowIndexLookup = null;
    }

    private void EndGridRenderPass()
    {
        GridRenderDiagnostics.EndPass(_renderPassStartTimestamp);
        _renderPassActive = false;
        _renderVisibleColumns = null;
        _renderRowIndexLookupSource = null;
        _renderRowIndexLookup = null;
    }

    private IReadOnlyList<GridColumn> GetRenderVisibleColumns()
    {
        if (!_renderPassActive)
            return VisibleColumns.ToList();

        return _renderVisibleColumns ??= VisibleColumns.ToList();
    }

    private IEnumerable<GridColumn> EffectiveColumns
    {
        get
        {
            var columns = _columnsContainer?.Columns;
            if (columns is { Count: > 0 })
                return columns;

            return AvailableColumns?
                .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                .Select(CreateTransientColumn)
                ?? Enumerable.Empty<GridColumn>();
        }
    }

    #pragma warning disable BL0005 // Transient non-rendered columns mirror host schema when data is empty.
    private static GridColumn CreateTransientColumn(ChooseColumnDescriptor column) => new()
    {
        Field = column.Field,
        HeaderText = string.IsNullOrWhiteSpace(column.Header) ? column.Field : column.Header,
        Visible = column.Visible
    };
    #pragma warning restore BL0005

    public IReadOnlyList<GridColumn> Columns =>
        _columnsContainer?.Columns ?? Array.Empty<GridColumn>();

    private bool AllRowsSelected =>
        PagedData.Any() && PagedData.All(item => _selectedItems.Contains(item));

    private bool ShouldHideGridContentForNoVisibleColumns =>
        !VisibleColumns.Any()
        && Columns.Count > 0;

    private EventCallback<bool> SelectAllCheckedChanged =>
        EventCallback.Factory.Create<bool>(this, (bool value) => ToggleSelectAll(value));

    private int GroupedPlaceholderCount =>
        (AllowGrouping && HideGroupedColumns) ? GroupedLayoutColumns.Count : 0;

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
                    var col = FindColumnByField(colField);
                    data = data.Where(item =>
                    {
                        var rawVal = GetFilterRawValue(item, colField)?.ToString() ?? "";
                        var displayText = GetFilterDisplayText(item, col, rawVal);
                        return PassesDisplayAwareTextFilter(rawVal, displayText, filterVal, state.FilterOperator);
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

            // Apply expression filter from the group-drop search button.
            if (_expressionFilterRoot != null)
            {
                data = data.Where(PassesExpressionFilter);
            }

            // Apply global search
            if (!string.IsNullOrEmpty(SearchText))
            {
                var searchLower = SearchText.ToLower();
                data = data.Where(item =>
                    VisibleColumns.Any(col =>
                    {
                        var val = GetColumnSearchText(item, col);
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
                var rows = data.ToList();
                var regularRows = rows.Where(item => !IsPinnedTrailingNewRow(item));
                var pinnedRows = rows.Where(IsPinnedTrailingNewRow);

                if (sortedCol.Value.SortDirection == SortDirection.Ascending)
                    data = regularRows.OrderBy(item => GetSortKeyValue(item, sortField), GridSortKeyComparer.Instance).Concat(pinnedRows);
                else
                    data = regularRows.OrderByDescending(item => GetSortKeyValue(item, sortField), GridSortKeyComparer.Instance).Concat(pinnedRows);
            }
            return data;
        }
    }

    private static object? GetSortKeyValue(object? item, string field)
        => NormalizeSortValue(GetPropertyValue(item, field));

    private GridColumn? FindColumnByField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        return Columns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    private static object? NormalizeSortValue(object? value)
    {
        if (value is null or DBNull)
            return null;

        if (value is string || value is IComparable)
            return value;

        if (TryGetDictionaryValue(value, "SortValue", out var dictionarySortValue)
            || TryGetDictionaryValue(value, "DisplayValue", out dictionarySortValue)
            || TryGetDictionaryValue(value, "DisplayText", out dictionarySortValue)
            || TryGetDictionaryValue(value, "Text", out dictionarySortValue)
            || TryGetDictionaryValue(value, "Name", out dictionarySortValue)
            || TryGetDictionaryValue(value, "VendorName", out dictionarySortValue)
            || TryGetDictionaryValue(value, "Description", out dictionarySortValue)
            || TryGetDictionaryValue(value, "Value", out dictionarySortValue))
        {
            return ReferenceEquals(dictionarySortValue, value)
                ? ConvertSortFallbackToString(value)
                : NormalizeSortValue(dictionarySortValue);
        }

        foreach (var memberName in SortDisplayMemberNames)
        {
            var accessor = GetPropertyAccessor(value.GetType(), memberName);
            if (accessor?.CanRead != true)
                continue;

            object? memberValue;
            try
            {
                memberValue = accessor.Getter!(value);
            }
            catch
            {
                continue;
            }

            return ReferenceEquals(memberValue, value)
                ? ConvertSortFallbackToString(value)
                : NormalizeSortValue(memberValue);
        }

        return ConvertSortFallbackToString(value);
    }

    private static readonly string[] SortDisplayMemberNames =
    [
        "SortValue",
        "DisplayValue",
        "DisplayText",
        "Text",
        "Name",
        "VendorName",
        "Description",
        "Value"
    ];

    private static string ConvertSortFallbackToString(object? value)
    {
        if (value is null or DBNull)
            return string.Empty;

        try
        {
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private sealed class GridSortKeyComparer : IComparer<object?>
    {
        public static readonly GridSortKeyComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            x = NormalizeSortValue(x);
            y = NormalizeSortValue(y);

            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return -1;

            if (y is null)
                return 1;

            if (x is string xText && y is string yText)
                return StringComparer.CurrentCultureIgnoreCase.Compare(xText, yText);

            if (x is not string && y is not string
                && x.GetType() != y.GetType()
                && TryConvertToDecimal(x, out var xNumber)
                && TryConvertToDecimal(y, out var yNumber))
            {
                return xNumber.CompareTo(yNumber);
            }

            if (x.GetType() == y.GetType() && x is IComparable sameTypeComparable)
            {
                try
                {
                    return sameTypeComparable.CompareTo(y);
                }
                catch
                {
                    // Fall through to the string comparer for unusual IComparable implementations.
                }
            }

            if (x is IComparable xComparable)
            {
                try
                {
                    return xComparable.CompareTo(y);
                }
                catch
                {
                    // Incompatible mixed types fall through to display string comparison.
                }
            }

            if (y is IComparable yComparable)
            {
                try
                {
                    return -yComparable.CompareTo(x);
                }
                catch
                {
                    // Incompatible mixed types fall through to display string comparison.
                }
            }

            return StringComparer.CurrentCultureIgnoreCase.Compare(
                ConvertSortFallbackToString(x),
                ConvertSortFallbackToString(y));
        }
    }

    private bool IsPinnedTrailingNewRow(TValue item)
    {
        return _hasTrailingNewRowItem
            && EqualityComparer<TValue>.Default.Equals(item, _trailingNewRowItem!)
            && IsTrackedTrailingNewRowStillBlank(item);
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

    // ── Custom row windowing (flat, bounded-height path) ─────────────────
    //
    // Replaces Blazor's <Virtualize>. Its IntersectionObserver did NOT track
    // scrolling inside this <table> grid (verified: scroll frozen), so we drive
    // the visible window ourselves from scrollTop/clientHeight: render only the
    // rows in (and near) the viewport, bracketed by two zero-content spacer rows
    // whose heights stand in for the un-rendered rows above and below. The only
    // browser dependency is a tiny rAF-throttled scroll-position reader in
    // grid-control.js (registerGridWindowScroll) — reading scrollTop/clientHeight
    // has no Blazor equivalent (the geometry exception to minimize-JS).

    /// <summary>Overscan rows kept above and below the visible slice so a fast
    /// flick never outruns the render.</summary>
    [Parameter] public int WindowOverscanRows { get; set; } = 260;

    /// <summary>Only refresh the row window after scrolling close to the
    /// buffered edge; this avoids a Blazor render for every row of scroll.</summary>
    private int WindowRefreshGuardRows => WindowOverscanRows / 3;

    /// <summary>First rendered ABSOLUTE row index into the paged/sorted list.</summary>
    private int _winStart;

    /// <summary>Rows to render (visible + overscan). Defaults large so the FIRST
    /// paint has a full buffered window before the initial scroll callback
    /// right-sizes it to the real viewport height.</summary>
    private int _winCount = 520;   // resized from WindowOverscanRows after the first scroll sync

    /// <summary>The vertical scroll container (<c>.fx-grid-content</c>,
    /// <c>overflow:auto</c>, bounded by <see cref="Height"/>). The scroll reader
    /// attaches here.</summary>
    private ElementReference _scrollElement;

    /// <summary>Callback ref passed to the JS scroll reader.</summary>
    private DotNetObjectReference<GridControl<TValue>>? _windowSelfRef;

    /// <summary>True once the scroll listener has been attached for this grid.</summary>
    private bool _windowScrollRegistered;

    /// <summary>Fingerprint of the last rendered list. When it changes (sort,
    /// filter, or DataSource swap) the window offset resets to the top.</summary>
    private int _lastWindowListSignature;

    private bool _preserveRowWindowOnNextListChange;
    private TValue? _preserveRowWindowExpectedLastItem;
    private bool _hasPreserveRowWindowExpectedLastItem;

    /// <summary>Set when the offset was reset to the top; consumed after render to
    /// scroll the container back to the top so the first paint isn't blank.</summary>
    private bool _pendingWindowScrollReset;

    /// <summary>
    /// Resolved data-row height (px) used to size the spacer rows and translate
    /// scrollTop into a row index. Honors <see cref="RowHeight"/> when the consumer
    /// sets it; otherwise 16px — the measured compact data-row height (11px font,
    /// 1.1 line-height). Variable heights (<see cref="RowHeightSelector"/>, user
    /// row-resize, the inline edit row) can drift from this fixed size; the
    /// overscan absorbs minor drift.
    /// </summary>
    private double _rowHeightPx =>
        RowHeight > 0 ? Math.Max(MinRowHeight, RowHeight) : 16;

    /// <summary>
    /// Window the flat row path only where it helps and has a real scroll
    /// viewport: a bounded <see cref="Height"/> (the grid's own
    /// <c>overflow:auto</c> container), no active paging, and no grouping. The
    /// grouped / aggregate / inline-new-row / empty-state paths are never windowed.
    /// Pivot and chart render entirely different bodies, so they are excluded too
    /// (keeps the render branch and the JS registration in lock-step, and avoids a
    /// stale <c>_scrollElement</c>).
    /// </summary>
    private bool UseRowWindowing =>
        !IsPagingActive
        && !string.IsNullOrWhiteSpace(Height)
        && !(AllowGrouping && _groupDescriptors.Count > 0)
        && !_pivotMode
        && !(ShowAsChart && ChartValueFields is { Count: > 0 });

    /// <summary>Inline style for a spacer row's single cell — a pure height stand-in
    /// (invariant-formatted so locales with a comma decimal don't emit bad CSS).</summary>
    private static string WindowSpacerStyle(double heightPx)
    {
        var h = heightPx < 0 ? 0 : heightPx;
        return string.Create(CultureInfo.InvariantCulture,
            $"height:{h}px;padding:0;border:0");
    }

    /// <summary>
    /// Per-render offset maintenance for the windowed flat path:
    /// <list type="bullet">
    /// <item>Resets <see cref="_winStart"/> to 0 when the rendered list identity
    /// changes (sort / filter / DataSource swap) so a scrolled-down offset is not
    /// carried onto a different — possibly shorter — list.</item>
    /// <item>Clamps <see cref="_winStart"/> so the window never begins past the end
    /// of a shrunken list (belt-and-suspenders with the reset above).</item>
    /// </list>
    /// Runs during render; only mutates view-window fields (never calls
    /// StateHasChanged), matching the existing in-render bookkeeping in
    /// <see cref="PagedData"/>.
    /// </summary>
    private void PrepareRowWindow(IList<TValue> pagedList)
    {
        var signature = ComputeWindowListSignature(pagedList);
        if (signature != _lastWindowListSignature)
        {
            var preserveWindow = ShouldPreserveRowWindowForListChange(pagedList);
            ClearPreserveRowWindowOnNextListChange();

            _lastWindowListSignature = signature;
            if (!preserveWindow && _winStart != 0)
            {
                _winStart = 0;
                // The DOM scroll container keeps its old scrollTop across a Blazor
                // re-render; without pulling it back to the top the first paint of
                // the reset window would land on the bottom spacer (blank). Cleared
                // in OnAfterRenderAsync.
                _pendingWindowScrollReset = true;
            }
        }
        else
        {
            ClearPreserveRowWindowOnNextListChange();
        }

        if (_winCount < 1)
            _winCount = 1;

        var maxStart = Math.Max(0, pagedList.Count - _winCount);
        if (_winStart > maxStart)
            _winStart = maxStart;
        if (_winStart < 0)
            _winStart = 0;
    }

    private void PreserveRowWindowOnNextListChange(TValue? expectedLastItem)
    {
        if (!UseRowWindowing || _winStart == 0)
            return;

        _preserveRowWindowOnNextListChange = true;
        _preserveRowWindowExpectedLastItem = expectedLastItem;
        _hasPreserveRowWindowExpectedLastItem = expectedLastItem is not null;
    }

    private bool ShouldPreserveRowWindowForListChange(IList<TValue> pagedList)
    {
        if (!_preserveRowWindowOnNextListChange)
            return false;

        if (!_hasPreserveRowWindowExpectedLastItem)
            return true;

        return pagedList.Count > 0
            && EqualityComparer<TValue>.Default.Equals(pagedList[^1], _preserveRowWindowExpectedLastItem!);
    }

    private void ClearPreserveRowWindowOnNextListChange()
    {
        _preserveRowWindowOnNextListChange = false;
        _preserveRowWindowExpectedLastItem = default;
        _hasPreserveRowWindowExpectedLastItem = false;
    }

    /// <summary>
    /// Cheap O(1) fingerprint of the rendered list: row count, the active sort
    /// (field + direction), and the first/last item identities. Sorting changes the
    /// endpoints; filtering changes the count and/or endpoints; a DataSource swap
    /// changes all of them — so any of those flips the signature and resets the
    /// window to the top, while a pure scroll (or an in-place cell edit that keeps
    /// count/endpoints) does not.
    /// </summary>
    private int ComputeWindowListSignature(IList<TValue> list)
    {
        var hash = new HashCode();
        hash.Add(list.Count);

        foreach (var kvp in _columnStates)
        {
            if (kvp.Value?.SortDirection is { } dir)
            {
                hash.Add(kvp.Key);
                hash.Add(dir);
            }
        }

        if (list.Count > 0)
        {
            var cmp = EqualityComparer<TValue>.Default;
            var first = list[0];
            var last = list[^1];
            hash.Add(first is null ? 0 : cmp.GetHashCode(first));
            hash.Add(last is null ? 0 : cmp.GetHashCode(last));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// JS → C# callback from the rAF-throttled scroll reader in grid-control.js.
    /// Translates scrollTop / clientHeight into a new window (first row + count)
    /// and re-renders only when the window actually moved.
    /// </summary>
    [JSInvokable]
    public async Task OnGridWindowScrollAsync(double scrollTop, double clientHeight)
    {
        if (!UseRowWindowing)
            return;

        var rowH = _rowHeightPx <= 0 ? 16 : _rowHeightPx;
        var visible = (int)Math.Ceiling(clientHeight / rowH);
        if (visible < 1)
            visible = 1;

        var firstVisible = Math.Max(0, (int)Math.Floor(scrollTop / rowH));
        var lastVisibleExclusive = firstVisible + visible;
        var newCount = visible + WindowOverscanRows * 2;
        var currentEnd = _winStart + _winCount;
        var nearWindowStart = firstVisible < _winStart + WindowRefreshGuardRows;
        var nearWindowEnd = lastVisibleExclusive > currentEnd - WindowRefreshGuardRows;

        if (_winCount == newCount && !nearWindowStart && !nearWindowEnd)
            return;

        var newStart = Math.Max(0, firstVisible - WindowOverscanRows);
        if (newStart != _winStart || newCount != _winCount)
        {
            _winStart = newStart;
            _winCount = newCount;
            await InvokeAsync(StateHasChanged);
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

    /// <summary>A numeric column with many distinct values makes one group per value —
    /// unusable at scale. Past 100 distinct values the groups become RANGES instead,
    /// with "nice" 1/2/5-magnitude bounds derived from the column's min and max.
    /// Returns null (per-value grouping) for non-numeric columns or small value sets.</summary>
    private Func<TValue, string>? BuildNumericBucketSelector(IEnumerable<TValue> data, string field)
    {
        var values = new List<double>();
        var distinct = new HashSet<double>();
        foreach (var item in data)
        {
            var raw = GetPropertyValue(item, field);
            if (raw == null || raw is DBNull)
                continue;
            if (raw is string || raw is bool || raw is DateTime)
                return null;
            double d;
            try { d = Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
            catch { return null; }
            values.Add(d);
            distinct.Add(d);
        }
        if (values.Count == 0 || distinct.Count <= 100)
            return null;

        var min = values.Min();
        var max = values.Max();
        var span = max - min;
        if (span <= 0)
            return null;

        const int targetBuckets = 12;
        var rawStep = span / targetBuckets;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var normalized = rawStep / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var step = nice * magnitude;
        var start = Math.Floor(min / step) * step;

        string Label(int idx)
        {
            var lo = start + idx * step;
            var hi = lo + step;
            return $"{lo.ToString("0.###", CultureInfo.CurrentCulture)} - {hi.ToString("0.###", CultureInfo.CurrentCulture)}";
        }

        return item =>
        {
            var raw = GetPropertyValue(item, field);
            if (raw == null || raw is DBNull)
                return "(empty)";
            var d = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            var idx = (int)Math.Floor((d - start) / step);
            if (idx < 0) idx = 0;
            var maxIdx = (int)Math.Floor((max - start) / step);
            if (idx > maxIdx) idx = maxIdx;
            return Label(idx);
        };
    }

    private IEnumerable<GroupResult<TValue>> BuildGroups(IEnumerable<TValue> data, int level, string parentPath)
    {
        if (level >= _groupDescriptors.Count)
            return Enumerable.Empty<GroupResult<TValue>>();

        var gd = _groupDescriptors[level];
        // The group KEY stays a string (it names the header and the expand-state path),
        // but ordering below uses the raw typed value so numeric fields sort 39 < 1000
        // instead of alphabetically.
        var groupSortValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var bucketSelector = BuildNumericBucketSelector(data, gd.Field);
        var groups = data
            .GroupBy(item => bucketSelector != null
                ? bucketSelector(item)
                : GetPropertyValue(item, gd.Field)?.ToString() ?? "(empty)")
            .Select(g =>
            {
                var allItems = g.ToList();
                var groupPath = string.IsNullOrEmpty(parentPath)
                    ? $"{gd.Field}:{g.Key}"
                    : $"{parentPath}/{gd.Field}:{g.Key}";
                groupSortValues[groupPath] = GetPropertyValue(allItems[0], gd.Field);
                var group = new GroupResult<TValue>
                {
                    Field = gd.Field,
                    HeaderText = gd.HeaderText,
                    Key = g.Key,
                    DisplayText = ResolveGroupHeaderText(gd.Field, g.Key, allItems),
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

        groups = GetGroupSortDirection(gd.Field) == SortDirection.Descending
            ? groups.OrderByDescending(group => groupSortValues.GetValueOrDefault(group.GroupPath), GridSortKeyComparer.Instance).ToList()
            : groups.OrderBy(group => groupSortValues.GetValueOrDefault(group.GroupPath), GridSortKeyComparer.Instance).ToList();

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

    private SortDirection GetGroupSortDirection(string field)
    {
        return _columnStates.TryGetValue(field, out var state) && state.SortDirection == SortDirection.Descending
            ? SortDirection.Descending
            : SortDirection.Ascending;
    }

    private string ResolveGroupHeaderText(string field, string key, IReadOnlyList<TValue> items)
    {
        if (GroupHeaderTextSelector == null)
            return key;

        var displayText = GroupHeaderTextSelector(field, key, items);
        return string.IsNullOrWhiteSpace(displayText) ? key : displayText;
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
        _pageState.PageSize = ResolvedPageSize;
        _lastHostResolvedPageSize = ResolvedPageSize;

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
        if (AutoFocusFirstCell && _autoFocusFirstCellAttempts < 3
            && PagedData.Any() && VisibleColumns.Any())
        {
            _autoFocusFirstCellAttempts++;
            if (!_activeCell.HasValue)
                await SelectCellAsync((0, 0));
            await FocusGridHostAsync();
        }

        // Right-click menus open with the first item highlighted, as the Win32
        // popup menus they replace did.
        if (_focusMenuPending && (_showHeaderContextMenu || _showCellContextMenu))
        {
            _focusMenuPending = false;
            await FocusMenuItemAsync(
                _showHeaderContextMenu ? _headerMenuElement : _cellMenuElement, "first");
        }

        // Re-apply initial group columns now that columns are loaded. Redraw
        // only when a group descriptor was actually added — the unconditional
        // first-render StateHasChanged shipped a no-op batch for every grid.
        if (firstRender && GroupColumns is { Count: > 0 } && _groupDescriptors.Count == 0)
        {
            var groupsApplied = false;
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
                    groupsApplied = true;
                }
            }
            if (groupsApplied)
                StateHasChanged();
        }

        // Hook once per component instance; JS side is idempotent as well.
        await EnsureGridKeyboardTrapRegisteredAsync();
        await EnsureInstantSelectionFeedbackRegisteredAsync();
        await EnsureGridScrollSyncRegisteredAsync();
        await EnsureHeaderDragPreviewRegisteredAsync();
        await EnsureRowDragSelectionAutoScrollRegisteredAsync();
        await EnsureScrollbarActivityRegisteredAsync();
        await EnsureFilterPopupDragRegisteredAsync();
        await EnsureGridWindowScrollRegisteredAsync();

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

        // Drag-selection preview handoff: the client-painted preview is
        // removed only AFTER the authoritative selection render has been
        // applied, so there is never an unselected flash between them.
        if (_dragPreviewClearPending)
        {
            _dragPreviewClearPending = false;
            await ClearDragPreviewAsync();
        }

        await EnsureTrailingNewRowIfNeededAsync();

        // Batch-edit focus handling — fires once per batch-edit start after
        // the input has actually been laid into the DOM. Without this,
        // single-click editing can show an input while focus remains on the
        // grid/cell, so typed characters are not rendered in the editor.
        await ApplyPendingBatchEditFocusAsync();

        if (_pendingActiveCellScrollIntoView)
        {
            _pendingActiveCellScrollIntoView = false;
            _pendingWindowScrollReset = false;
            await EnsureActiveCellVisibleAsync();
        }

        await ResetInitialGridScrollIfNeededAsync(firstRender);
        await ApplyPendingWindowScrollResetAsync();
        await EnsurePendingFirstRowSelectionAsync();
        await RestoreFilterPopupFocusAsync();
    }

    /// <summary>
    /// Attach (or detach) the rAF-throttled scroll reader that drives custom row
    /// windowing. Registered lazily the first time <see cref="UseRowWindowing"/> is
    /// active and the scroll container has been laid into the DOM; unregistered if
    /// the grid later leaves the windowed configuration (e.g. paging turned on or
    /// grouping applied). The JS export is idempotent, so a repeat call is safe.
    /// </summary>
    private async Task EnsureGridWindowScrollRegisteredAsync()
    {
        try
        {
            if (UseRowWindowing)
            {
                if (_windowScrollRegistered)
                    return;

                _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                    "import", GridJsModulePath);
                _windowSelfRef ??= DotNetObjectReference.Create(this);
                await _gridJsModule.InvokeVoidAsync(
                    "registerGridWindowScroll", _scrollElement, _windowSelfRef);
                _windowScrollRegistered = true;
            }
            else if (_windowScrollRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridWindowScroll", _scrollElement);
                _windowScrollRegistered = false;
            }
        }
        catch (Exception)
        {
            // Best-effort. Without the reader the window keeps its initial 60-row
            // slice (still correct, just non-scroll-tracking) instead of throwing.
        }
    }

    /// <summary>
    /// After the window offset was reset to the top on a list-identity change,
    /// pull the scroll container back to the top so the first paint of the reset
    /// window lands on real rows rather than the bottom spacer. Reuses the existing
    /// scroll-reset helper (no new JS) — that helper also fires a scroll event,
    /// which re-syncs the reader to scrollTop 0.
    /// </summary>
    private async Task ApplyPendingWindowScrollResetAsync()
    {
        if (!_pendingWindowScrollReset)
            return;

        _pendingWindowScrollReset = false;

        if (!_windowScrollRegistered || _gridJsModule == null)
            return;

        try
        {
            await _gridJsModule.InvokeVoidAsync("resetInitialGridScroll", _gridHostElement);
        }
        catch (Exception)
        {
            // Best-effort viewport correction.
        }
    }

    private async Task EnsureActiveCellVisibleAsync()
    {
        // With row windowing, a jump target (type-search hit, Home/End,
        // PageDown) is usually NOT in the rendered slice, so the DOM-element
        // based scroll below would find nothing to scroll to. Move the window to
        // the target row first, then let the element-based pass fine-tune.
        await EnsureWindowedActiveRowVisibleAsync();

        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("ensureActiveGridCellVisible", _gridHostElement);
        }
        catch (Exception)
        {
            // Best-effort viewport correction. Keyboard selection still moves
            // even if static assets are unavailable during circuit teardown.
        }
    }

    /// <summary>
    /// When row windowing is active, bring the active row into the rendered
    /// window if a jump landed outside it. The active cell's row index is a
    /// DataSource index, so we resolve the ITEM and find its position in the
    /// windowed display list (handles sort/filter reordering). We set
    /// <see cref="_winStart"/> directly (so it doesn't depend on the async
    /// rAF scroll reader) and sync the scrollbar to match.
    /// </summary>
    private async Task EnsureWindowedActiveRowVisibleAsync()
    {
        if (!UseRowWindowing || !_activeCell.HasValue)
            return;

        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (item is null)
            return;

        var pagedList = PagedData as IList<TValue> ?? PagedData.ToList();
        var displayIndex = pagedList.IndexOf(item);
        if (displayIndex < 0)
            return;

        // Already inside the rendered slice — the element-based pass handles it.
        if (displayIndex >= _winStart && displayIndex < _winStart + _winCount)
            return;

        var maxStart = Math.Max(0, pagedList.Count - _winCount);
        _winStart = Math.Clamp(displayIndex - WindowOverscanRows, 0, maxStart);
        await InvokeAsync(StateHasChanged);

        // Keep the scrollbar in sync with the new window (a couple of rows of
        // lead-in so the target isn't jammed against the top edge). Setting
        // scrollTop also re-fires the scroll reader, which re-confirms the window.
        var targetScrollTop = Math.Max(0, (displayIndex - 2) * _rowHeightPx);
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("setGridScrollTop", _scrollElement, targetScrollTop);
        }
        catch (Exception)
        {
            // Best-effort; the window is already correct even if the scrollbar
            // position lags.
        }
    }

    private async Task ResetInitialGridScrollIfNeededAsync(bool firstRender)
    {
        var hasData = HasAnyData;
        var shouldReset = (_initialScrollResetOnFirstRenderPending && firstRender)
            || (_initialScrollResetOnFirstDataPending && hasData);

        if (!shouldReset)
            return;

        if (!await ResetInitialGridScrollAsync())
            return;

        if (hasData)
        {
            _initialScrollResetOnFirstDataPending = false;
            _initialScrollResetOnFirstRenderPending = false;
        }
        else if (firstRender)
        {
            _initialScrollResetOnFirstRenderPending = false;
        }
    }

    private async Task EnsurePendingFirstRowSelectionAsync()
    {
        if (!_pendingFirstRowSelection)
            return;

        if (!ShouldAutoSelectFirstRow())
        {
            _pendingFirstRowSelection = false;
            return;
        }

        var rows = GetVisibleRowItems();
        if (rows.Count == 0)
            return;

        _pendingFirstRowSelection = false;
        if (_selectedItems.Count > 0)
            return;

        await SelectFirstVisibleRowAsync(force: false);
        await InvokeAsync(StateHasChanged);
    }

    private bool ShouldAutoSelectFirstRow()
    {
        if (!AutoSelectFirstRow || !AllowSelection)
            return false;
        if (SelectionSettingsRef?.CheckboxOnly == true)
            return false;
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
            return false;
        if (ShouldHideGridContentForNoVisibleColumns)
            return false;

        return true;
    }

    private async Task SelectFirstVisibleRowAsync(bool force)
    {
        if (!ShouldAutoSelectFirstRow())
            return;

        var item = GetVisibleRowItems().FirstOrDefault();
        if (item == null)
            return;

        if (!force && _selectedItems.Count > 0)
            return;

        var rowIndex = ResolveRowIndex(item, 0);
        if (rowIndex < 0)
            rowIndex = 0;

        _focusedGroupPath = null;
        _selectedItems.Clear();
        _selectedItems.Add(item);
        _selectedCells.Clear();
        _lastSelectedItem = item;
        _lastSelectedRowIndex = rowIndex;

        _activeCell = VisibleColumns.Any() ? (rowIndex, 0) : null;
        if (_activeCell.HasValue)
            _lastSelectedCell = _activeCell.Value;

        if (EventsRef?.RowSelected.HasDelegate == true)
        {
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue>
            {
                Data = item,
                RowIndex = rowIndex
            });
        }

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
        if (_filterPopupField == null)
            await FocusGridHostAsync();
    }

    private void QueueFilterPopupFocus(FilterPopupFocusTarget target)
    {
        if (_filterPopupField != null)
            _pendingFilterPopupFocusTarget = target;
    }

    private async Task RestoreFilterPopupFocusAsync()
    {
        if (_filterPopupField == null || !_pendingFilterPopupFocusTarget.HasValue)
            return;

        _pendingFilterPopupFocusTarget = null;
        var element = _filterConditionInputRef;

        try
        {
            var module = await GetGridJsModuleAsync();
            if (module != null)
            {
                await module.InvokeVoidAsync("focusInputAtEnd", element);
                return;
            }
        }
        catch (Exception)
        {
            // Fall back to Blazor's native focus helper below.
        }

        try
        {
            await element.FocusAsync(preventScroll: true);
        }
        catch
        {
            // The popup may have closed or the field may have rerendered away.
        }
    }

    private async Task<bool> ResetInitialGridScrollAsync()
    {
        try
        {
            var module = await GetGridJsModuleAsync();
            if (module == null)
                return false;

            await module.InvokeVoidAsync("resetInitialGridScroll", _gridHostElement);
            return true;
        }
        catch (Exception)
        {
            // Best-effort initial viewport correction. If JS interop is not
            // available yet, the first-data pass will retry on a later render.
            return false;
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

    /// <summary>
    /// Static-asset path to <c>grid-control.js</c>. Resolved from the
    /// executing assembly's simple name so the same source line works for
    /// both packages: each consumer reaches the static web asset under
    /// that package's assembly name. Keep the path computed
    /// (not a string literal) so the FlexCore ↔ FlexKit byte-identical-
    /// source rule holds.
    /// </summary>
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

        var settings = BuildChooseColumnsSnapshotSettings(snapshot);
        _lastAppliedSettings = settings;
        // Don't capture column signature here — the host's column rebuild is
        // async, so the live signature will diverge in a moment and trigger
        // ReapplyAfterRebuildIfNeeded as intended.
        try { await GridSettingsStore.SaveAsync(PersistenceKey, settings); }
        catch (Exception) { /* persistence shouldn't surface errors */ }
    }

    private GridSettings BuildChooseColumnsSnapshotSettings(IReadOnlyList<ChooseColumnDescriptor> snapshot)
    {
        var order = new List<string>();
        var visibility = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var column in snapshot)
        {
            if (string.IsNullOrWhiteSpace(column.Field))
                continue;

            order.Add(column.Field);
            visibility[column.Field] = column.Visible;
        }

        return new GridSettings
        {
            ColumnOrder     = order,
            Visibility      = visibility,
            // Carry forward any existing user state for fields the dialog
            // didn't touch — widths, header overrides, group columns.
            Widths          = Columns.Where(c => c.RuntimeWidth.HasValue && !string.IsNullOrEmpty(c.Field))
                                     .ToDictionary(c => c.Field, c => c.RuntimeWidth!.Value),
            HeaderOverrides = _headerOverrides.Count > 0 ? new Dictionary<string, string>(_headerOverrides) : null,
            GroupColumns    = _groupDescriptors.Select(g => g.Field).ToList()
        };
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
    private async Task FireLayoutChangedAsync(GridSettings? snapshot = null)
    {
        if (!OnLayoutChanged.HasDelegate) return;
        await OnLayoutChanged.InvokeAsync(snapshot ?? BuildCurrentSnapshot());
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
        SyncPrintDefaults();
        ResetFiltersIfDataSourceChanged();
        ClearSelectionIfDataSourceChanged();

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

        // A consumer that turns pivoting off (e.g. AllowPivoting bound to a
        // toggle) while the grid is still in pivot mode would otherwise strand
        // it: the pivot body stops rendering but _pivotMode stays true, leaving a
        // blank grid. Exit pivot cleanly so it falls back to the normal row body.
        if (!AllowPivoting && _pivotMode)
        {
            _pivotMode = false;
            _activeOptionsPanel = GridOptionsPanel.None;
        }
    }

    private void SyncPrintDefaults()
    {
        var defaultsChanged = !_printDefaultsInitialized
            || _lastDefaultPrintOrientation != DefaultPrintOrientation
            || _lastDefaultPrintPageSize != DefaultPrintPageSize
            || _lastDefaultPrintColumnLayout != DefaultPrintColumnLayout
            || _lastDefaultPrintGridLines != DefaultPrintGridLines
            || _lastDefaultPrintZoomMode != DefaultPrintZoomMode
            || _lastDefaultPrintZoomPercent != ClampPrintZoomPercent(DefaultPrintZoomPercent);

        if (!defaultsChanged)
            return;

        _lastDefaultPrintOrientation = DefaultPrintOrientation;
        _lastDefaultPrintPageSize = DefaultPrintPageSize;
        _lastDefaultPrintColumnLayout = DefaultPrintColumnLayout;
        _lastDefaultPrintGridLines = DefaultPrintGridLines;
        _lastDefaultPrintZoomMode = DefaultPrintZoomMode;
        _lastDefaultPrintZoomPercent = ClampPrintZoomPercent(DefaultPrintZoomPercent);
        _printDefaultsInitialized = true;

        if (_showPrintOptionsDialog)
            return;

        _printOrientation = DefaultPrintOrientation;
        _printPageSize = DefaultPrintPageSize;
        _printColumnLayout = DefaultPrintColumnLayout;
        _printShowGridLines = DefaultPrintGridLines;
        _printZoomMode = DefaultPrintZoomMode;
        _printZoomPercent = ClampPrintZoomPercent(DefaultPrintZoomPercent);
    }

    private void ResetFiltersIfDataSourceChanged()
    {
        var signature = ComputeSelectionDataSourceSignature();
        if (!_filterDataSourceCaptured)
        {
            _lastFilterDataSource = DataSource;
            _lastFilterDataSourceSignature = signature;
            _filterDataSourceCaptured = true;
            return;
        }

        if (signature.Equals(_lastFilterDataSourceSignature))
        {
            _lastFilterDataSource = DataSource;
            return;
        }

        _lastFilterDataSource = DataSource;
        _lastFilterDataSourceSignature = signature;
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
            _pendingFirstRowSelection = AutoSelectFirstRow;
            return;
        }

        if (signature.Equals(_lastSelectionDataSourceSignature))
        {
            _lastSelectionDataSource = DataSource;
            return;
        }

        _lastSelectionDataSource = DataSource;
        _lastSelectionDataSourceSignature = signature;
        _runtimeRowHeights.Clear();
        ClearTransientSelectionState(clearRows: true);
        _pendingFirstRowSelection = AutoSelectFirstRow;
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
        _pointerFillCell = null;
        _lastSelectedCell = null;
        _lastSelectedRowIndex = null;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        _lastSelectedItem = default;
        _isDragSelecting = false;
        _suppressNextClickAfterDragSelect = false;
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
        ClearMouseDownClosedDropdownOpenSuppression();
        ClearBatchDropdownTypeSelectBuffer();
        _pendingBatchEditFocus = false;
        _pendingBatchEditSelectAll = false;
        _pendingBatchEditClientX = null;
        _pendingBatchEditScrollIntoView = false;
        ClearKeyboardNavigationSource();
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

        _pendingFirstRowSelection = false;
        await SelectFirstVisibleRowAsync(force: true);
    }

    // ── Filtering ────────────────────────────────────────────────────────

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
        state.FilterOperator = filterOperator ?? state.FilterOperator;
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
        _numericFilterMinText.Remove(field);
        _numericFilterMaxText.Remove(field);
        if (string.Equals(_filterPopupField, field, StringComparison.Ordinal))
        {
            var state = GetColumnState(field);
            _filterTextDraft = "";
            _filterOperatorDraft = state.FilterOperator;
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

        _numericFilterMinText.Clear();
        _numericFilterMaxText.Clear();
        ClearExpressionFilterState();
        _filterPopupField = null;
        _filterTextDraft = "";
        _filterOperatorDraft = TextFilterOperator.Contains;
        _filterOperatorDraftsByField.Clear();
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

    // ── Selection ────────────────────────────────────────────────────────

    private async Task HandleRowClick(TValue item, int rowIndex, MouseEventArgs? mouseArgs = null)
    {
        if (await CommitPendingTypeAheadFromPointerAsync())
            return;

        // Commit any in-progress batch cell edit when clicking away
        await CommitBatchEdit();
        ClearTypeSearchBuffer();
        ClearKeyboardNavigationSource();
        if (mouseArgs?.ShiftKey != true)
            ClearKeyboardRangeSelectionAnchor();

        // If the mouseup that just preceded this click came at the end of
        // a drag-select, the selection was already established by
        // HandleRowMouseEnter. Swallow the click so the normal SelectRow
        // path doesn't collapse the range back to a single row.
        if (ConsumeDragSelectClickSuppression())
            return;
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

    // ── Drag-select ──────────────────────────────────────────────────────
    //
    // Spreadsheet-style click-and-drag selection across rows. Mousedown on
    // a row in SelectionType.Multiple sets the anchor; subsequent
    // mouseenters with the left button still held extend the range from
    // anchor to current row, replacing any previous selection. The
    // trailing click event (browsers fire click after every mouseup) is
    // swallowed by HandleRowClick when _isDragSelecting is true so the
    // drag's selection survives.

    // Rec 1 — mousedown/mouseup are ARMING events: they carry no visual of
    // their own (the instant row preview is client-side JS, editors render
    // explicitly inside TryStartBatchEdit), so their EventCallbacks are
    // created on a non-IHandleEvent receiver and skip the implicit re-render.
    // A physical click then costs ONE authoritative render — the click's.
    private EventCallback<FocusEventArgs> NonRenderingGridFocusOut =>
        EventCallback.Factory.Create<FocusEventArgs>(NonRenderingEventReceiver.Instance,
            HandleGridFocusOut);

    private EventCallback<MouseEventArgs> NonRenderingGridMouseUp =>
        EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance,
            (Action<MouseEventArgs>)HandleGridMouseUp);

    private EventCallback<MouseEventArgs> NonRenderingRowMouseDown(TValue item, int rowIndex) =>
        EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance,
            (Action<MouseEventArgs>)(e => HandleRowMouseDown(item, rowIndex, e)));

    private void HandleRowMouseDown(TValue item, int rowIndex, MouseEventArgs args)
    {
        if (!AllowRowDragSelection) return;

        // Only the primary button (Button == 0) starts a drag. Right-click
        // and middle-click should not select.
        if (args.Button != 0) return;

        // Modifier keys are reserved for SelectRow's range/toggle gestures —
        // letting them seed a drag here would clobber Ctrl+click toggling
        // and confuse Shift+click range extension. Plain clicks only.
        if (args.CtrlKey || args.MetaKey || args.ShiftKey) return;

        if (SelectionSettingsRef?.Mode == SelectionMode.Cell && SelectionSettingsRef?.CheckboxOnly != true)
            return;

        var selType = SelectionSettingsRef?.Type ?? SelectionType.Single;
        if (selType != SelectionType.Multiple) return;
        if (!AllowSelection) return;

        _dragAnchorItem = item;
        _dragAnchorRowIndex = ResolveRowIndex(item, rowIndex);
        _isDragSelecting = false;   // promoted when the browser reports movement
        _suppressNextClickAfterDragSelect = false;

        var anchorVisibleIndex = GetVisibleRowItems().IndexOf(item);
        if (anchorVisibleIndex >= 0)
            _ = RegisterDragSelectionCaptureAsync("row", anchorVisibleIndex, null);

        // Wipe any document-level text selection left over from earlier
        // interactions (or from a browser that paints a transient
        // selection on the mousedown itself, before user-select: none
        // takes effect). Fire-and-forget — we don't await it because
        // mousedown handlers must stay synchronous to keep click ordering
        // predictable, and the helper is idempotent / cheap.
        _ = ClearGridTextSelectionAsync();
    }

    private async Task HandleCellMouseDown(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var mouseDownColumn = VisibleColumns.ElementAtOrDefault(cellIndex);

        if (args.Button == 0)
        {
            var previousActiveCell = _activeCell;
            var activeCellChanged = previousActiveCell?.RowIndex != resolvedRowIndex
                || previousActiveCell?.CellIndex != cellIndex;

            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
                && activeCellChanged
                && _typeAheadBuffer.Length > 0)
            {
                var preserveSelection = HasSingleCellBulkEditSelection();
                if (await CommitPendingSingleCellTypeAheadAsync() && preserveSelection)
                {
                    _suppressNextPointerSelectionAfterTypeAheadCommit = true;
                    return;
                }
            }

            if (activeCellChanged && IsBatchEditingDifferentCell(item, mouseDownColumn))
            {
                await CommitBatchEdit();
                StateHasChanged();   // commit teardown must not wait for the click render
            }

            SetActiveCell(resolvedRowIndex, cellIndex);
            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
                _selectedItems.Clear();

            ClearKeyboardNavigationSource();
            if (!args.ShiftKey)
                ClearKeyboardRangeSelectionAnchor();
            CaptureRowSelectionTypeAheadTarget(cellIndex);
            if (CanStartSingleCellColumnDrag(cellIndex))
            {
                _cellDragAnchor = (resolvedRowIndex, cellIndex);
                _isCellDragSelecting = false;
                var cellAnchorVisibleIndex = GetVisibleRowItems().IndexOf(item);
                var cellAnchorField = VisibleColumns.ElementAtOrDefault(cellIndex)?.Field;
                if (cellAnchorVisibleIndex >= 0 && !string.IsNullOrEmpty(cellAnchorField))
                    _ = RegisterDragSelectionCaptureAsync("cell", cellAnchorVisibleIndex, cellAnchorField);
            }

            var isPlainMouseDown = !args.CtrlKey && !args.MetaKey && !args.ShiftKey;
            if (isPlainMouseDown && CanStartSingleCellClosedDropdownEdit(mouseDownColumn))
            {
                var wasBatchEditingCell = !string.IsNullOrWhiteSpace(mouseDownColumn?.Field)
                    && IsBatchEditing(item, mouseDownColumn.Field);
                var started = await TryStartBatchEdit(item, resolvedRowIndex, mouseDownColumn!, args.ClientX, openDropdownOnRender: false, selectAllOnStart: true);
                if (started && !wasBatchEditingCell)
                {
                    ArmMouseDownClosedDropdownOpenSuppression(item, mouseDownColumn!.Field);
                    await FocusGridHostAsync();
                }
            }
            else if (CanStartMouseDownBatchDropdownEdit(mouseDownColumn))
            {
                var started = await TryStartBatchEdit(item, resolvedRowIndex, mouseDownColumn!, args.ClientX, openDropdownOnRender: true, selectAllOnStart: true);
                if (started)
                    ArmRetargetedBatchEditClickSuppression(item, mouseDownColumn!.Field);
            }
        }

        // Data cells stop mousedown propagation so popup/template buttons do
        // not also trigger the row handler. Start the same row drag protocol
        // here, now with the cell known, so the anchor cell can reveal its
        // flat "..." button while the user is selecting a range.
        HandleRowMouseDown(item, rowIndex, args);
    }

    private bool CanStartSingleCellClosedDropdownEdit(GridColumn? col)
    {
        return SingleCellColumnMassEditEnabled
            && col != null
            && EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && col.AllowEditing
            && !col.IsPrimaryKey
            && !string.IsNullOrEmpty(col.Field)
            && col.Type != ColumnType.CheckBox
            && col.EditOptions?.Any() == true;
    }

    private bool CanStartMouseDownBatchDropdownEdit(GridColumn? col)
    {
        if (SingleCellColumnMassEditEnabled)
            return false;

        return col != null
            && EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && col.AllowEditing
            && !col.IsPrimaryKey
            && !string.IsNullOrEmpty(col.Field)
            && col.Type != ColumnType.CheckBox
            && col.EditOptions?.Any() == true
            && col.OpenEditOptionsOnEdit;
    }

    private void ArmRetargetedBatchEditClickSuppression(TValue item, string? field)
    {
        _mouseStartedBatchEditItem = item;
        _mouseStartedBatchEditField = field;
        _mouseStartedBatchEditUtc = DateTime.UtcNow;
        _suppressRetargetedBatchEditClick = true;
    }

    private bool ShouldSuppressRetargetedBatchEditClick(TValue item, GridColumn? col)
    {
        if (!_suppressRetargetedBatchEditClick)
            return false;

        if ((DateTime.UtcNow - _mouseStartedBatchEditUtc).TotalMilliseconds > 1000)
        {
            ClearRetargetedBatchEditClickSuppression();
            return false;
        }

        var isOriginalCell = col != null
            && !string.IsNullOrWhiteSpace(col.Field)
            && EqualityComparer<TValue>.Default.Equals(item, _mouseStartedBatchEditItem!)
            && string.Equals(col.Field, _mouseStartedBatchEditField, StringComparison.OrdinalIgnoreCase);

        ClearRetargetedBatchEditClickSuppression();
        return !isOriginalCell;
    }

    private void ClearRetargetedBatchEditClickSuppression()
    {
        _suppressRetargetedBatchEditClick = false;
        _mouseStartedBatchEditItem = default;
        _mouseStartedBatchEditField = null;
        _mouseStartedBatchEditUtc = DateTime.MinValue;
    }

    private void ArmMouseDownClosedDropdownOpenSuppression(TValue item, string? field)
    {
        _mouseDownClosedDropdownItem = item;
        _mouseDownClosedDropdownField = field;
        _mouseDownClosedDropdownUtc = DateTime.UtcNow;
        _suppressMouseDownClosedDropdownOpenClick = true;
    }

    private bool ShouldSuppressMouseDownClosedDropdownOpen(TValue item, GridColumn? col)
    {
        if (!_suppressMouseDownClosedDropdownOpenClick)
            return false;

        if ((DateTime.UtcNow - _mouseDownClosedDropdownUtc).TotalMilliseconds > 1000)
        {
            ClearMouseDownClosedDropdownOpenSuppression();
            return false;
        }

        var isOriginalCell = col != null
            && !string.IsNullOrWhiteSpace(col.Field)
            && EqualityComparer<TValue>.Default.Equals(item, _mouseDownClosedDropdownItem!)
            && string.Equals(col.Field, _mouseDownClosedDropdownField, StringComparison.OrdinalIgnoreCase);

        ClearMouseDownClosedDropdownOpenSuppression();
        return isOriginalCell;
    }

    private void ClearMouseDownClosedDropdownOpenSuppression()
    {
        _suppressMouseDownClosedDropdownOpenClick = false;
        _mouseDownClosedDropdownItem = default;
        _mouseDownClosedDropdownField = null;
        _mouseDownClosedDropdownUtc = DateTime.MinValue;
    }

    private void ClearBatchDropdownTypeSelectBuffer()
    {
        _batchDropdownTypeSelectBuffer = "";
        _batchDropdownTypeSelectLastInputUtc = DateTime.MinValue;
    }

    private void HandleCellContextMenu(TValue item, int rowIndex, int cellIndex, MouseEventArgs args)
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        SetActiveCell(resolvedRowIndex, cellIndex);
        _lastSelectedCell = (resolvedRowIndex, cellIndex);
        _lastSelectedItem = item;
        _lastSelectedRowIndex = resolvedRowIndex;
        ClearKeyboardNavigationSource();
        CaptureRowSelectionTypeAheadTarget(cellIndex);

        if (!EnableCellContextMenu)
            return;

        var visibleColumns = VisibleColumns.ToList();
        if (cellIndex < 0 || cellIndex >= visibleColumns.Count)
            return;

        _cellContextMenuItem = item;
        _cellContextMenuColumn = visibleColumns[cellIndex];
        _cellContextMenuX = Math.Max(0, args.ClientX);
        _cellContextMenuY = Math.Max(0, args.ClientY);
        _showCellContextMenu = true;
        OpenMenuKeyboardNav();
        _showHeaderContextMenu = false;
        _showInsertColumnSubmenu = false;
    }

    private void CloseCellContextMenu()
    {
        _showCellContextMenu = false;
        _cellContextMenuItem = default;
        _cellContextMenuColumn = null;
    }

    private bool CanReadCellContext => _cellContextMenuItem is not null && _cellContextMenuColumn is not null;

    private bool CanApplyCellContextEdit => CanEditCellContextColumn(_cellContextMenuColumn);

    private bool CanEditCellContextColumn(GridColumn? col)
    {
        return EnableCellContextMenu
            && col is not null
            && col.AllowEditing
            && !col.IsPrimaryKey
            && !string.IsNullOrWhiteSpace(col.Field)
            && EditSettingsRef?.AllowEditing == true;
    }

    private async Task CellContextCutAsync()
    {
        if (!CanApplyCellContextEdit)
            return;

        var text = GetCellContextText();
        await WriteCellContextClipboardAsync(text);
        await CommitCellContextValueAsync(GetCellContextDeleteValue(_cellContextMenuColumn!));
        CloseCellContextMenu();
    }

    private async Task CellContextCopyAsync()
    {
        if (!CanReadCellContext)
            return;

        await WriteCellContextClipboardAsync(GetCellContextText());
        CloseCellContextMenu();
    }

    private async Task CellContextPasteAsync()
    {
        if (!CanApplyCellContextEdit || _cellContextMenuColumn is null)
            return;

        var text = await ReadCellContextClipboardAsync();
        if (text is null)
        {
            CloseCellContextMenu();
            return;
        }

        await CommitCellContextValueAsync(CoerceCellContextText(_cellContextMenuColumn, text));
        CloseCellContextMenu();
    }

    private async Task CellContextDeleteAsync()
    {
        if (!CanApplyCellContextEdit || _cellContextMenuColumn is null)
            return;

        await CommitCellContextValueAsync(GetCellContextDeleteValue(_cellContextMenuColumn));
        CloseCellContextMenu();
    }

    private string GetCellContextText()
    {
        return _cellContextMenuItem is null || _cellContextMenuColumn is null
            ? string.Empty
            : GetCellDisplayValue(_cellContextMenuItem, _cellContextMenuColumn);
    }

    private async Task WriteCellContextClipboardAsync(string text)
    {
        _cellContextClipboardText = text;
        _hasCellContextClipboard = true;

        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch
        {
            // Clipboard access can be denied on non-HTTPS pages; keep the
            // internal fallback so Paste still works inside the grid.
        }
    }

    private async Task<string?> ReadCellContextClipboardAsync()
    {
        try
        {
            var text = await JsRuntime.InvokeAsync<string>("navigator.clipboard.readText");
            if (text is not null)
                return text;
        }
        catch
        {
            // Fall back to the last value copied/cut from this grid.
        }

        return _hasCellContextClipboard ? _cellContextClipboardText : null;
    }

    private static object? CoerceCellContextText(GridColumn col, string text)
    {
        if (col.Type is ColumnType.CheckBox or ColumnType.Boolean)
        {
            if (bool.TryParse(text, out var boolValue))
                return boolValue;
            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var intValue))
                return intValue != 0;

            return false;
        }

        return text;
    }

    private static object? GetCellContextDeleteValue(GridColumn col)
    {
        return col.Type is ColumnType.CheckBox or ColumnType.Boolean ? false : "";
    }

    private async Task<bool> CommitCellContextValueAsync(object? newValue)
    {
        if (_cellContextMenuItem is null || _cellContextMenuColumn is null || !CanApplyCellContextEdit)
            return false;

        var item = _cellContextMenuItem;
        var col = _cellContextMenuColumn;
        await CommitBatchEdit();

        if (EventsRef?.OnCellEdit.HasDelegate == true)
        {
            var editArgs = new CellEditArgs<TValue> { Data = item, ColumnName = col.Field };
            await EventsRef.OnCellEdit.InvokeAsync(editArgs);
            if (editArgs.Cancel)
            {
                await InvokeAsync(StateHasChanged);
                return false;
            }
        }

        if (!SetPropertyObjectValue(item, col.Field, newValue))
        {
            await InvokeAsync(StateHasChanged);
            return false;
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
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private async Task<bool> TryHandleCellContextShortcutAsync(KeyboardEventArgs e)
    {
        if (!EnableCellContextMenu || _batchEditItem != null || _isEditing)
            return false;

        var key = e.Key?.ToLowerInvariant() ?? "";
        var modifier = e.CtrlKey || e.MetaKey;

        if (modifier && key == "x")
        {
            if (!TryPrepareActiveCellContext())
                return false;

            await CellContextCutAsync();
            return true;
        }

        if (modifier && key == "c")
        {
            if (!TryPrepareActiveCellContext())
                return false;

            await CellContextCopyAsync();
            return true;
        }

        if (modifier && key == "v")
        {
            if (!TryPrepareActiveCellContext())
                return false;

            await CellContextPasteAsync();
            return true;
        }

        if (!modifier && !e.AltKey && key == "delete")
        {
            if (!TryPrepareActiveCellContext())
                return false;

            await CellContextDeleteAsync();
            return true;
        }

        return false;
    }

    private bool TryPrepareActiveCellContext()
    {
        if (!_activeCell.HasValue)
            return false;

        var col = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (col is null || item is null)
            return false;

        _cellContextMenuItem = item;
        _cellContextMenuColumn = col;
        return true;
    }

    private void HandleGridMouseUp(MouseEventArgs args)
    {
        // End the drag on mouseup so the cursor/visual state cannot stay
        // latched if the browser swallows the trailing click. The next click,
        // when it exists, is still swallowed so the selected range survives.
        if (_isDragSelecting || _isCellDragSelecting)
        {
            _suppressNextClickAfterDragSelect = true;
            _suppressNextClickAfterDragSelectUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            _isDragSelecting = false;
            _dragAnchorRowIndex = null;
            _dragAnchorItem = default;
            ClearCellDragState();
            return;
        }

        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        _cellDragAnchor = null;
    }

    // Drag tracking runs in the browser (grid-control.js): the preview is
    // painted client-side from data-ari attributes and the server hears ONE
    // endpoint call on release — the old per-row/per-cell mouseenter handlers
    // cost two renders of the whole window buffer per crossed row.
    [JSInvokable]
    public async Task EndDragSelectionFromBrowserAsync(string mode, int finalVisibleIndex, bool moved)
    {
        var isRow = string.Equals(mode, "row", StringComparison.Ordinal);

        if (!moved)
        {
            // Plain click: the click event does the selecting; just disarm.
            // The anchor preview stays painted until the CLICK's authoritative
            // render lands (OnAfterRenderAsync clears it) — clearing here would
            // flash the row back to unselected for a round trip on slow links.
            if (isRow)
            {
                _dragAnchorItem = default;
                _dragAnchorRowIndex = null;
                _isDragSelecting = false;
            }
            else
            {
                ClearCellDragState();
            }
            _dragPreviewClearPending = true;
            return;
        }

        var visible = GetVisibleRowItems();
        if (visible.Count == 0)
        {
            await ClearDragPreviewAsync();
            return;
        }
        finalVisibleIndex = Math.Clamp(finalVisibleIndex, 0, visible.Count - 1);

        // Arm the preview-clear BEFORE the selection render below, so that
        // render's OnAfterRender performs the handoff — arming afterwards
        // could leave the preview stuck if no further render follows.
        _dragPreviewClearPending = true;

        if (isRow && _dragAnchorItem != null)
        {
            var item = visible[finalVisibleIndex];
            // One range computation, one render, one SelectionChanged.
            await ContinueRowDragSelectionAsync(item, ResolveRowIndex(item, finalVisibleIndex));
        }
        else if (!isRow && _cellDragAnchor.HasValue)
        {
            var anchor = _cellDragAnchor.Value;
            var resolved = ResolveRowIndex(visible[finalVisibleIndex], finalVisibleIndex);
            _isCellDragSelecting = true;
            SelectSingleCellColumnDragRange(anchor.RowIndex, resolved, anchor.CellIndex);
            _lastSelectedCell = anchor;
            await InvokeAsync(StateHasChanged);
            await NotifySelectionChangedAsync(GridSelectionChangeSource.MouseDrag);
        }

        _suppressNextClickAfterDragSelect = true;
        _suppressNextClickAfterDragSelectUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        _isDragSelecting = false;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        ClearCellDragState();
    }

    private bool _dragPreviewClearPending;
    private DotNetObjectReference<GridControl<TValue>>? _dragDotNetRef;

    private bool _instantFeedbackRegistered;

    // Standing client-side binding: the pressed row highlights in the SAME
    // frame as the pointerdown, before any server round trip — so the row
    // paints first and the active-cell cue (server render) follows. Rows in
    // Multiple selection mode only; JS itself skips modifier presses and
    // in-cell editors.
    private async Task EnsureInstantSelectionFeedbackRegisteredAsync()
    {
        if (_instantFeedbackRegistered)
            return;
        if (!AllowSelection || SelectionSettingsRef?.Mode == SelectionMode.Cell)
            return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerGridInstantSelectionFeedback", _gridHostElement);
            _instantFeedbackRegistered = true;
        }
        catch
        {
        }
    }

    private async Task RegisterDragSelectionCaptureAsync(string mode, int anchorVisibleIndex, string? anchorField)
    {
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            _dragDotNetRef ??= DotNetObjectReference.Create(this);
            await _gridJsModule.InvokeVoidAsync("registerGridDragSelection",
                _gridHostElement, _dragDotNetRef, mode, anchorVisibleIndex, anchorField ?? "");
        }
        catch
        {
        }
    }

    private async Task ClearDragPreviewAsync()
    {
        try
        {
            if (_gridJsModule != null)
                await _gridJsModule.InvokeVoidAsync("clearGridDragPreview", _gridHostElement);
        }
        catch
        {
        }
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

    private bool ConsumeDragSelectClickSuppression()
    {
        if (!_suppressNextClickAfterDragSelect && !_isDragSelecting && !_isCellDragSelecting)
            return false;

        if (_suppressNextClickAfterDragSelect
            && !_isDragSelecting
            && !_isCellDragSelecting
            && DateTime.UtcNow > _suppressNextClickAfterDragSelectUntilUtc)
        {
            _suppressNextClickAfterDragSelect = false;
            _suppressNextClickAfterDragSelectUntilUtc = DateTime.MinValue;
            return false;
        }

        _suppressNextClickAfterDragSelect = false;
        _suppressNextClickAfterDragSelectUntilUtc = DateTime.MinValue;
        _isDragSelecting = false;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        ClearCellDragState();
        ClearMouseDownClosedDropdownOpenSuppression();
        return true;
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
        var field = CanReceiveTypeAhead(col)
            || (ShowRowSelectionEditColumnCue && CanUseAsRowSelectionEditCueTarget(col))
                ? col!.Field
                : null;

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

    private bool CanUseAsRowSelectionEditCueTarget(GridColumn? col)
    {
        return col != null
            && !string.IsNullOrWhiteSpace(col.Field)
            && !col.IsPrimaryKey
            && (col.AllowEditing || col.AllowCellDragSelection)
            && EditSettingsRef?.AllowEditing != false;
    }

    private string? ResolveRowSelectionEditCueTargetField()
    {
        if (_rowSelectionTypeAheadTargetCaptured)
            return _rowSelectionTypeAheadTargetField;

        if (_activeCell.HasValue)
        {
            var activeCol = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
            if (CanUseAsRowSelectionEditCueTarget(activeCol))
                return activeCol!.Field;
        }

        if (_lastSelectedCell.HasValue)
        {
            var lastCol = VisibleColumns.ElementAtOrDefault(_lastSelectedCell.Value.CellIndex);
            if (CanUseAsRowSelectionEditCueTarget(lastCol))
                return lastCol!.Field;
        }

        return null;
    }

    private bool ShouldShowRowSelectionEditColumnCue(TValue item, GridColumn column)
    {
        if (!ShowRowSelectionEditColumnCue
            || SelectionSettingsRef?.Mode == SelectionMode.Cell
            || _selectedItems.Count <= 1
            || !_selectedItems.Contains(item)
            || !CanUseAsRowSelectionEditCueTarget(column))
        {
            return false;
        }

        var targetField = ResolveRowSelectionEditCueTargetField();
        return !string.IsNullOrWhiteSpace(targetField)
            && string.Equals(column.Field, targetField, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearTypeAheadBuffer()
    {
        if (_typeAheadBuffer.Length == 0)
            return;

        _typeAheadBuffer = "";
        _ = NotifyTypeAheadChangedAsync();
    }

    /// <summary>
    /// Lazy-imports grid-control.js (if not already loaded) and invokes
    /// <c>clearTextSelection</c> to drop any document-level Selection
    /// ranges. The CSS rule <c>user-select: none</c> on the grid body
    /// stops most accidental selections from forming in the first place;
    /// this is the cleanup path for the ones that slip through.
    /// </summary>
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
            // Best-effort — if interop fails the worst case is the user
            // sees a stray selection until their next click; not worth
            // surfacing.
        }
    }

    /// <summary>
    /// Prevents browser-native Tab from racing ahead of Blazor's grid
    /// navigation while focus is inside a data cell/editor. Text entry stays
    /// native; only Tab is trapped client-side.
    /// </summary>
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
            // Best-effort — without this, Blazor navigation still runs but the
            // browser may also move focus to the next tabbable element.
        }
    }

    private async Task EnsureGridScrollSyncRegisteredAsync()
    {
        if (_gridScrollSyncRegistered) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerGridScrollSync", _gridHostElement);
            _gridScrollSyncRegistered = true;
        }
        catch (Exception)
        {
            // Best-effort. Native body scrolling still works if the helper is
            // unavailable; only header/body horizontal sync is affected.
        }
    }

    /// <summary>
    /// Installs a custom, compact drag image for header reordering so the
    /// browser's full-cell ghost doesn't occlude drop indicators.
    /// </summary>
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
            // Best-effort — if this fails, drag still works with default
            // browser ghost image; we'll retry on the next render.
        }
    }

    /// <summary>
    /// Installs browser-side edge auto-scroll for spreadsheet-style row
    /// drag selection. Blazor row selection still owns the selected state;
    /// JS only scrolls the viewport and nudges the row mouseenter path when
    /// scrolling moves new rows under a stationary pointer.
    /// </summary>
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
            // Best-effort — normal drag-select still works without edge
            // auto-scroll; we'll retry on the next render.
        }
    }

    /// <summary>
    /// Adds a short-lived CSS class while the grid's vertical scroller is active.
    /// Used by legacy scrollbar chrome so arrow buttons are not permanently visible.
    /// </summary>
    private async Task EnsureScrollbarActivityRegisteredAsync()
    {
        if (_scrollbarActivityRegistered) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerScrollbarActivity", _gridHostElement);
            _scrollbarActivityRegistered = true;
        }
        catch (Exception)
        {
            // Best-effort visual polish; scrolling itself remains native.
        }
    }

    private async Task EnsureFilterPopupDragRegisteredAsync()
    {
        if (_filterPopupField == null) return;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("registerFilterPopupDrag", _gridHostElement);
        }
        catch (Exception)
        {
            // Best-effort; the filter remains usable even if it cannot be dragged.
        }
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

        // Range is computed against the VISIBLE row order, not raw
        // DataSource indices. With grouping/sorting enabled the gap
        // between anchor.DataSourceIndex and current.DataSourceIndex can
        // span hundreds of rows that are hidden in collapsed groups or
        // belong to other groups — selecting them all reads as a wildly
        // inflated count for what looks like a small drag.
        var visible = GetVisibleRowItems();
        var anchorIdx = visible.IndexOf(anchorItem);
        var currentIdx = visible.IndexOf(item);
        if (anchorIdx < 0 || currentIdx < 0) return;

        if (anchorIdx == currentIdx && !_isDragSelecting)
        {
            // Still on the anchor row — not yet a drag.
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

        // Don't fire RowSelected for every row crossed — that would flood
        // user code with one callback per mousemove. The trailing click
        // doesn't fire RowSelected either (we early-return). Hosts that
        // need to react to drag-select progress (e.g. live count labels)
        // subscribe to SelectionChanged, which fires once per drag-extend
        // with the current count — already the natural per-mousemove
        // cadence so it's safe.

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
                && col.OpenEditButtonOnDoubleClick
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

        if (IsBatchDoubleClickEditCell(col))
        {
            // Owner editor spec 2026-07-24:
            //   • FIRST double-click on a cell → caret at the clicked character.
            //     The gesture's first click already opened the editor (select-all,
            //     via the single-click path); within the opening-gesture window
            //     convert that selection into a caret at the clicked X.
            //   • A LATER double-click on the already-open editor → select the
            //     ENTIRE cell text (not the browser's default word-select).
            if (!string.IsNullOrWhiteSpace(col.Field) && IsBatchEditing(item, col.Field))
            {
                var withinOpeningGesture =
                    (DateTime.UtcNow - _batchEditStartedUtc).TotalMilliseconds <= BatchEditOpeningGestureMs;

                _pendingBatchEditFocus = true;
                _pendingBatchEditSelectAll = !withinOpeningGesture;                 // 2nd dbl-click = select all
                _pendingBatchEditClientX = withinOpeningGesture ? args.ClientX : null; // 1st dbl-click = caret at click
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Cell not editing yet (e.g. single-click editing disabled for this
            // grid): a direct double-click opens the editor caret-at-click.
            await StartBatchEdit(item, resolvedRowIndex, col, args.ClientX);
            return;
        }

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
            && col.OpenEditButtonOnDoubleClick
            && EventsRef?.OnEditButtonClick.HasDelegate == true
            && !string.IsNullOrEmpty(col.Field))
        {
            await HandleEditButtonClick(item, col);
            return;
        }
    }

    private async Task SelectRow(TValue item, int rowIndex, MouseEventArgs? mouseArgs = null)
    {
        _focusedGroupPath = null;
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
            _selectedItems.Clear();
            _selectedItems.Add(item);
        }
        else if (selType == SelectionType.Multiple || isCtrl || isShift)
        {
            if (!isCtrl && !isShift && selType == SelectionType.Multiple)
            {
                // Plain click (no modifiers) in Multiple mode. Match the
                // desktop convention: a bare click always collapses to
                // exactly the clicked row. A second click event for the same
                // physical click, or the second click in a double-click, must
                // remain idempotent instead of clearing the selection.
                _selectedItems.Clear();
                _selectedItems.Add(item);
            }
            else if (isShift && _lastSelectedItem != null)
            {
                // Range selection — anchor and current resolved against the
                // VISIBLE row order (PagedData when ungrouped, GroupedData
                // walked with collapsed groups skipped). Using DataSource
                // indices here was wrong for the same reason it was wrong
                // for drag-select: hidden / grouped-but-not-shown rows
                // between anchor and current would be added to the set,
                // making "Shift-click two adjacent visible rows" select
                // hundreds of underlying records.
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
                // Toggle single item
                if (!_selectedItems.Remove(item))
                    _selectedItems.Add(item);
            }
            else
            {
                _selectedItems.Clear();
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
        _suppressNextClickAfterDragSelect = false;
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
        _suppressNextClickAfterDragSelect = false;
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
        if (_suppressNextPointerSelectionAfterTypeAheadCommit)
        {
            _suppressNextPointerSelectionAfterTypeAheadCommit = false;
            await FocusGridHostAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (await CommitPendingTypeAheadFromPointerAsync())
            return;

        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var clickedCol = VisibleColumns.ElementAtOrDefault(cellIndex);
        ClearTypeSearchBuffer();

        if (ConsumeDragSelectClickSuppression())
        {
            await FocusGridHostAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (ShouldSuppressRetargetedBatchEditClick(item, clickedCol))
            return;

        var previousActiveCell = _activeCell;
        var activeCellChanged = previousActiveCell?.RowIndex != resolvedRowIndex
            || previousActiveCell?.CellIndex != cellIndex;

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell
            && activeCellChanged
            && _typeAheadBuffer.Length > 0)
        {
            var preserveSelection = HasSingleCellBulkEditSelection();
            if (await CommitPendingSingleCellTypeAheadAsync() && preserveSelection)
            {
                await FocusGridHostAsync();
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        SetActiveCell(resolvedRowIndex, cellIndex, preservePointerFill: true);
        MarkPointerFillCell(resolvedRowIndex, cellIndex);
        _cellDragAnchor = null;
        _dragAnchorRowIndex = null;
        _dragAnchorItem = default;
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
            _selectedItems.Clear();

        if (!AllowSelection)
            return;

        var isCtrl = args.CtrlKey || args.MetaKey;
        var isShift = args.ShiftKey;
        var isPlainCellClick = !isCtrl && !isShift;
        var clickedCellWasSelected = SelectionSettingsRef?.Mode == SelectionMode.Cell
            && _selectedCells.Contains((resolvedRowIndex, cellIndex));
        var suppressMouseDownClosedDropdownOpen = ShouldSuppressMouseDownClosedDropdownOpen(item, clickedCol);

        // Determine if the clicked cell is editable (batch mode)
        var isEditableCell = EditSettingsRef?.AllowEditing == true
            && EditSettingsRef.Mode == EditMode.Batch
            && clickedCol != null
            && clickedCol.AllowEditing
            && !clickedCol.IsPrimaryKey
            && !string.IsNullOrEmpty(clickedCol.Field);
        var isOptionListCell = isEditableCell && clickedCol?.EditOptions?.Any() == true;
        var shouldOpenEditOptionsOnEdit = isOptionListCell
            && clickedCol?.OpenEditOptionsOnEdit == true
            && !SingleCellColumnMassEditEnabled;
        var shouldOpenSelectedCellEditOptionsOnClick = SingleCellColumnMassEditEnabled
            && isOptionListCell
            && clickedCol?.OpenEditOptionsOnEdit == true
            && isPlainCellClick
            && clickedCellWasSelected
            && !suppressMouseDownClosedDropdownOpen;
        var shouldEnterClosedEditOptionsOnClick = SingleCellColumnMassEditEnabled
            && isOptionListCell
            && isPlainCellClick;
        // Two-click dropdown for non-SingleCell (row / plain) grids. A dropdown
        // column that opts OUT of open-on-edit (OpenEditOptionsOnEdit=false)
        // enters the CLOSED editor (arrow + frame) on the first click and opens
        // the list on a second click of the same cell — the desktop-grid feel,
        // instead of the default one-click-opens. Opt-in: columns that keep the
        // default (OpenEditOptionsOnEdit=true) are unchanged, so no existing
        // consumer shifts behavior.
        var isTwoClickDropdownCell = isOptionListCell
            && !SingleCellColumnMassEditEnabled
            && clickedCol?.OpenEditOptionsOnEdit == false
            && isPlainCellClick;
        var reopeningTwoClickDropdown = isTwoClickDropdownCell
            && !string.IsNullOrEmpty(clickedCol!.Field)
            && IsBatchEditing(item, clickedCol.Field);
        var shouldOpenDropdownOnEdit = shouldOpenEditOptionsOnEdit
            || shouldOpenSelectedCellEditOptionsOnClick
            || reopeningTwoClickDropdown;
        var shouldStartEditOnClick = EditOnSingleClick
            || shouldOpenDropdownOnEdit
            || shouldEnterClosedEditOptionsOnClick
            || isTwoClickDropdownCell;
        var useSingleCellBatchBehavior = isEditableCell
            && BatchEditBehavior == GridBatchEditBehavior.SingleCell;
        var clickWillStartBatchEdit = isEditableCell
            && clickedCol?.Type != ColumnType.CheckBox
            && shouldStartEditOnClick;

        // Commit any in-progress batch cell edit when clicking a DIFFERENT cell.
        // (StartBatchEdit also calls CommitBatchEdit internally, but this covers
        // clicks on non-editable cells that wouldn't reach StartBatchEdit.)
        // Guard: re-clicking the cell that is CURRENTLY being batch-edited must
        // NOT commit — committing nulls _batchEditField, which unmounts the
        // in-cell editor (TextBox/DropDownList) subtree on the next render,
        // stealing focus and making the dropdown "open then disappear". Note we
        // can't use activeCellChanged here: HandleCellMouseDown already moved
        // _activeCell to this cell before the click fires, so only the batch-
        // edit target (_batchEditItem/_batchEditField) reliably identifies the
        // cell being edited.
        var clickingActiveEditCell = clickedCol != null
            && !string.IsNullOrEmpty(clickedCol.Field)
            && IsBatchEditing(item, clickedCol.Field);
        if (!clickingActiveEditCell && !clickWillStartBatchEdit)
            await CommitBatchEdit();
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
        {
            // Row-selection grids use _activeCell for the dotted edit cue.
            // Leaving a previous programmatic cell selection behind makes
            // popup/template cells look "stuck" after focus moves elsewhere.
            _selectedCells.Clear();
        }

        // Handle row selection for Row-mode grids.
        // Cell clicks use stopPropagation so the <tr> onclick (HandleRowClick)
        // does NOT fire — we must handle selection here.
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell &&
            SelectionSettingsRef?.CheckboxOnly != true)
        {
            var isPlainClick = !args.CtrlKey && !args.MetaKey && !args.ShiftKey;
            // FullMultiSelect preserves a multi-row selection when the user
            // plain-clicks an editable/action cell inside it so bulk-edit
            // stays armed. VBMultiSelect keeps Ctrl/Shift/drag selection but
            // lets a plain click collapse the selection to the clicked row.
            //
            // Modifier-key clicks (Ctrl/Shift) intentionally fall through
            // to SelectRow so range/toggle gestures keep working.
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

        // Text/numeric cells do not enter edit mode on single click; the click
        // only selects the cell. A typed printable key starts editing and
        // replaces the full value, while double-click opens an in-cell editor at
        // the clicked character. Checkbox cells keep their click-to-toggle path.
        if (isEditableCell && !useSingleCellBatchBehavior && clickedCol?.Type == ColumnType.CheckBox)
        {
            await StartBatchEdit(item, resolvedRowIndex, clickedCol!);
            return;
        }

        if (SelectionSettingsRef?.Mode != SelectionMode.Cell)
        {
            if (isEditableCell && shouldStartEditOnClick && clickedCol?.Type != ColumnType.CheckBox)
            {
                await StartBatchEdit(item, resolvedRowIndex, clickedCol!, args.ClientX, openDropdownOnRender: shouldOpenDropdownOnEdit, selectAllOnStart: true);
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

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Pointer);

        if (isEditableCell && shouldStartEditOnClick && clickedCol?.Type != ColumnType.CheckBox)
        {
            await StartBatchEdit(item, resolvedRowIndex, clickedCol!, args.ClientX, openDropdownOnRender: shouldOpenDropdownOnEdit, selectAllOnStart: true);
            return;
        }

        await FocusGridHostAsync();
    }

    private void SelectSingleCellColumnMassEditCells(int rowIndex, int cellIndex, bool isCtrl, bool isShift)
    {
        var cell = (rowIndex, cellIndex);

        if (!isCtrl && !isShift)
        {
            _selectedCells.Clear();
            _selectedCells.Add(cell);
            return;
        }

        // Mass-edit selections are intentionally one-column only. If the user
        // starts a modifier selection in another column, make that column the
        // new mass-edit column instead of allowing ambiguous multi-column edits.
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
            // Focus is best-effort; the grid may have been removed by the host callback.
        }
        catch (JSException)
        {
            // Static assets/circuit teardown should not break row selection.
        }
    }

    /// <summary>
    /// Restores keyboard focus to the grid host without changing the current
    /// row/cell selection. Consumers should call this after an external popup,
    /// dialog, or picker closes so Tab/Shift+Tab continues grid navigation
    /// instead of falling back to the browser's page-level tab order.
    /// </summary>
    public Task FocusGridAsync() => FocusGridHostAsync();

    /// <summary>
    /// Alias for <see cref="FocusGridAsync"/> that reads naturally at popup
    /// close sites.
    /// </summary>
    public Task ResumeKeyboardNavigationAsync() => FocusGridHostAsync();

    private void SetActiveCell(int rowIndex, int cellIndex, bool preservePointerFill = false)
    {
        var col = VisibleColumns.ElementAtOrDefault(cellIndex);
        _activeCell = col != null ? (rowIndex, cellIndex) : null;
        if (!preservePointerFill)
            _pointerFillCell = null;
    }

    private void MarkPointerFillCell(int rowIndex, int cellIndex)
    {
        _pointerFillCell = (rowIndex, cellIndex);
    }

    private bool IsPointerFillCell(int rowIndex, int cellIndex)
    {
        return _pointerFillCell.HasValue
            && _pointerFillCell.Value.RowIndex == rowIndex
            && _pointerFillCell.Value.CellIndex == cellIndex;
    }

    private async Task ActivateCheckboxCellAsync(TValue item, int rowIndex, int cellIndex, bool focusGridHost)
    {
        await CommitBatchEdit();

        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        SetActiveCell(resolvedRowIndex, cellIndex);
        RememberKeyboardNavigationSource(item, resolvedRowIndex, cellIndex);
        _lastSelectedCell = (resolvedRowIndex, cellIndex);

        var cell = (resolvedRowIndex, cellIndex);
        var preserveCellSelection = SingleCellColumnMassEditEnabled
            && SelectionSettingsRef?.Mode == SelectionMode.Cell
            && _selectedCells.Count > 1
            && _selectedCells.Contains(cell);
        if (!preserveCellSelection)
        {
            _selectedCells.Clear();
            if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
                _selectedCells.Add(cell);
        }

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

    private Task HandleCheckboxMouseDown(TValue item, int rowIndex, int cellIndex, MouseEventArgs args) =>
        HandleCellMouseDown(item, rowIndex, cellIndex, args);

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
            if (_renderPassActive && item is not null)
            {
                var lookup = GetRenderRowIndexLookup(list);
                if (lookup.TryGetValue(item, out var cachedIndex))
                    return cachedIndex;
            }

            var idx = list.IndexOf(item);
            if (idx >= 0)
                return idx;
        }

        return fallbackIndex;
    }

    private Dictionary<object, int> GetRenderRowIndexLookup(IList<TValue> list)
    {
        if (ReferenceEquals(_renderRowIndexLookupSource, list) && _renderRowIndexLookup != null)
            return _renderRowIndexLookup;

        var lookup = new Dictionary<object, int>(Math.Max(0, list.Count));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not { } item)
                continue;

            lookup.TryAdd(item, i);
        }

        _renderRowIndexLookupSource = list;
        _renderRowIndexLookup = lookup;
        return lookup;
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

    /// <summary>
    /// Flat list of currently-visible data items in display order.
    /// When grouping is off this is just <see cref="PagedData"/>; when
    /// grouping is on it walks the group tree and skips items inside
    /// collapsed groups. Drives drag-select and Shift-range so the
    /// "selected count" matches what the user actually visually selected,
    /// rather than the underlying <c>DataSource</c> index span (which can
    /// include hundreds of rows hidden in collapsed groups or in other
    /// groups whose items happen to fall between the anchor and current
    /// rows in <c>DataSource</c> order).
    /// </summary>
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
        _focusedGroupPath = null;

        if (EventsRef?.RowSelecting.HasDelegate == true)
        {
            var args = new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex };
            await EventsRef.RowSelecting.InvokeAsync(args);
            if (args.Cancel) return;
        }

        if (!_selectedItems.Remove(item))
        {
            if ((SelectionSettingsRef?.Type ?? SelectionType.Single) == SelectionType.Single)
                _selectedItems.Clear();
            _selectedItems.Add(item);
        }

        _lastSelectedItem = item;
        _lastSelectedRowIndex = ResolveRowIndex(item, rowIndex);

        if (EventsRef?.RowSelected.HasDelegate == true)
            await EventsRef.RowSelected.InvokeAsync(new RowSelectEventArgs<TValue> { Data = item, RowIndex = rowIndex });

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Pointer);
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

    private async Task StartBatchEdit(TValue item, int rowIndex, GridColumn col, double? clientX = null, bool replaceOnFirstInput = false, bool openDropdownOnRender = false, bool selectAllOnStart = false)
    {
        await TryStartBatchEdit(item, rowIndex, col, clientX, replaceOnFirstInput, openDropdownOnRender, selectAllOnStart);
    }

    private async Task<bool> TryStartBatchEdit(TValue item, int rowIndex, GridColumn col, double? clientX = null, bool replaceOnFirstInput = false, bool openDropdownOnRender = false, bool selectAllOnStart = false)
    {
        if (!col.AllowEditing || string.IsNullOrEmpty(col.Field) || col.IsPrimaryKey) return false;
        if (EditSettingsRef?.AllowEditing != true || EditSettingsRef.Mode != EditMode.Batch) return false;

        // Re-entrancy guard: if this exact cell is ALREADY being batch-edited,
        // keep the live editor instead of commit-and-restart. Dropdown cells
        // can still be upgraded from "closed editor visible" to "open list"
        // when a later click asks for OpenOnRender.
        if (IsBatchEditing(item, col.Field))
        {
            if (openDropdownOnRender && col.EditOptions?.Any() == true && !_batchDropdownOpenOnRender)
            {
                _batchDropdownOpenOnRender = true;
                await InvokeAsync(StateHasChanged);
            }

            return true;
        }

        // Save previous edit if any. When another batch editor is about to
        // open, defer trailing-row maintenance until the new target is anchored;
        // otherwise an inline-new-row promotion can insert the blank template
        // row under the user's pointer before the click finishes.
        await CommitBatchEdit(deferTrailingNewRowEnsure: true);

        if (col.Type == ColumnType.CheckBox)
        {
            await FlushDeferredTrailingNewRowEnsureAsync();
            await HandleCheckboxToggle(item, col);
            return false;
        }

        if (EventsRef?.OnCellEdit.HasDelegate == true)
        {
            var args = new CellEditArgs<TValue> { Data = item, ColumnName = col.Field };
            await EventsRef.OnCellEdit.InvokeAsync(args);
            // Per-row / per-column edit veto (VB6 gData_BeforeEdit parity).
            if (args.Cancel)
            {
                await FlushDeferredTrailingNewRowEnsureAsync();
                return false;
            }
        }

        ClearBatchDropdownTypeSelectBuffer();
        _batchEditItem = item;
        _batchEditRowIndex = ResolveRowIndex(item, rowIndex);
        _batchEditField = col.Field;
        _batchEditValue = GetPropertyValue(item, col.Field)?.ToString() ?? "";
        _batchEditDirty = false;  // Reset on every new edit start.
        _batchEditReplaceOnFirstInput = replaceOnFirstInput;
        _batchDropdownOpenOnRender = openDropdownOnRender && col.EditOptions?.Any() == true;
        _batchEditInputRef = default;
        // Focus every batch input after render so single-click editing is
        // immediately typeable.
        // Owner editor spec 2026-07-24:
        //   • SINGLE-click edit start (selectAllOnStart=true from the mouse
        //     click sites) opens with the WHOLE text selected, so typing
        //     replaces it — same feel as multi-select mass edit.
        //   • DOUBLE-click start (HandleCellDblClick passes clientX only)
        //     places the caret at the clicked character instead.
        //   • A later double-click inside the open editor is native browser
        //     behavior (selects the text) and is not touched here.
        // New edit session → new @key on the uncontrolled editor so it re-captures
        // this cell's starting value (the value attribute is otherwise frozen for
        // the life of the component instance).
        _batchEditGeneration++;
        _batchEditStartedUtc = DateTime.UtcNow;
        _pendingBatchEditFocus = true;
        _pendingBatchEditSelectAll = selectAllOnStart || (col.SelectAllOnEdit && clientX == null);
        _pendingBatchEditClientX = _pendingBatchEditSelectAll ? null : clientX;
        return true;
    }

    // When the batch edit started (UTC). Used by HandleCellDblClick to tell the
    // tail of the OPENING double-click gesture (re-place the caret at the click)
    // apart from a later double-click on a long-open editor (leave the native
    // select-the-text behavior alone). Owner editor spec 2026-07-24.
    private DateTime _batchEditStartedUtc;
    private const int BatchEditOpeningGestureMs = 600;

    // Bumped once per edit session; used as the @key on the uncontrolled batch
    // editor so each new edit gets a fresh component that re-captures its value.
    private int _batchEditGeneration;

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

        var newValue = e?.Value is bool changedValue
            ? changedValue
            : !GetBoolValue(item, col.Field);

        var targets = ResolveCheckboxToggleTargets(item, col);
        var changedAny = false;
        foreach (var target in targets)
        {
            if (EventsRef?.OnCellEdit.HasDelegate == true)
            {
                var editArgs = new CellEditArgs<TValue> { Data = target, ColumnName = col.Field };
                await EventsRef.OnCellEdit.InvokeAsync(editArgs);
                if (editArgs.Cancel)
                    continue;
            }

            if (!SetPropertyObjectValue(target, col.Field, newValue))
                continue;

            changedAny = true;

            if (EventsRef?.OnCellSave.HasDelegate == true)
            {
                await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
                {
                    Data = target,
                    ColumnName = col.Field,
                    Value = newValue
                });
            }
        }

        if (!changedAny)
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        await EnsureTrailingNewRowIfNeededAsync();

        var rowIndex = ResolveRowIndex(item, -1);
        var cellIndex = ResolveVisibleColumnIndex(col.Field);
        if (rowIndex >= 0 && cellIndex >= 0)
            await TryAppendTrailingNewRowFromLastCellAsync(item, rowIndex, cellIndex, beginEdit: false);

        await InvokeAsync(StateHasChanged);
    }

    private List<TValue> ResolveCheckboxToggleTargets(TValue primary, GridColumn col)
    {
        // SingleCell column mass-edit (cell-selection) fan-out.
        if (SingleCellColumnMassEditEnabled)
        {
            var cellTargets = ResolveSingleCellColumnMassEditTargets(primary, col);
            return cellTargets.Count > 1 && cellTargets.Contains(primary)
                ? cellTargets
                : new List<TValue> { primary };
        }

        // Multi-ROW selection fan-out — mirrors the type-ahead / batch-edit bulk
        // edit (see CommitBatchEdit): when several rows are selected and the
        // toggled row is one of them, apply the new checkbox value to every
        // selected row so a CheckBox column participates in multi-edit like the
        // text / number / option columns already do. Reached only from the
        // keyboard toggle (Space/Enter) and FullMultiSelect click-toggles, where
        // the multi-selection survives the gesture; a plain VBMultiSelect click
        // has already collapsed the selection, so this naturally no-ops there.
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell
            && _selectedItems.Count > 1
            && _selectedItems.Contains(primary))
        {
            var rowTargets = new List<TValue> { primary };
            if (DataSource != null)
            {
                foreach (var d in DataSource)
                {
                    if (!EqualityComparer<TValue>.Default.Equals(d, primary)
                        && _selectedItems.Contains(d))
                    {
                        rowTargets.Add(d);
                    }
                }
            }

            if (rowTargets.Count > 1)
                return rowTargets;
        }

        return new List<TValue> { primary };
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

    private async Task<bool> TryInvokeActiveEditButtonAsync(KeyboardEventArgs e)
    {
        if (e.Key is not ("Enter" or "NumpadEnter"))
            return false;
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;
        if (_batchEditItem != null || !_activeCell.HasValue)
            return false;

        var column = VisibleColumns.ElementAtOrDefault(_activeCell.Value.CellIndex);
        if (column == null
            || !column.ShowEditButton
            || string.IsNullOrWhiteSpace(column.Field)
            || EventsRef?.OnEditButtonClick.HasDelegate != true)
        {
            return false;
        }

        var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
        if (item == null || !ShouldShowEditButtonForItem(column, item))
            return false;

        RememberKeyboardNavigationSource(item, _activeCell.Value.RowIndex, _activeCell.Value.CellIndex);
        await HandleEditButtonClick(item, column);
        return true;
    }

    private bool ShouldShowAlwaysEditButton(GridColumn col, TValue item)
    {
        if (!col.ShowEditButton || !col.AlwaysShowEditButton || string.IsNullOrWhiteSpace(col.Field))
            return false;

        return ShouldShowEditButtonForItem(col, item);
    }

    private bool ShouldShowActiveDisplayEditButton(GridColumn col, TValue item, bool showActiveEditButton)
    {
        if (!showActiveEditButton
            || !col.ShowEditButton
            || col.AlwaysShowEditButton
            || string.IsNullOrWhiteSpace(col.Field)
            || EventsRef?.OnEditButtonClick.HasDelegate != true)
        {
            return false;
        }

        return ShouldShowEditButtonForItem(col, item);
    }

    private bool ShouldShowEditButtonForItem(GridColumn col, TValue item)
    {
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

    private void RenderDisplayCellContent(RenderTreeBuilder builder, int sequence, TValue item, GridColumn col, bool showActiveEditButton = false)
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

        if (!ShouldShowAlwaysEditButton(col, item) && !ShouldShowActiveDisplayEditButton(col, item, showActiveEditButton))
        {
            builder.AddContent(sequence, text);
            return;
        }

        var buttonItem = item;
        var buttonCol = col;
        var editButtonText = string.IsNullOrWhiteSpace(col.EditButtonText) ? "..." : col.EditButtonText;
        if (editButtonText == "...")
        {
            builder.OpenElement(sequence, "span");
            builder.AddAttribute(sequence + 1, "class", "fx-cell-action-content");

            builder.OpenElement(sequence + 2, "span");
            builder.AddAttribute(sequence + 3, "class", "fx-cell-action-text");
            builder.AddContent(sequence + 4, text);
            builder.CloseElement();

            builder.OpenElement(sequence + 5, "button");
            builder.AddAttribute(sequence + 6, "type", "button");
            builder.AddAttribute(sequence + 7, "class", "fx-cell-action-btn fx-cell-action-ellipsis-btn");
            builder.AddAttribute(sequence + 8, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => HandleEditButtonClick(buttonItem, buttonCol)));
            builder.AddEventStopPropagationAttribute(sequence + 9, "onclick", true);
            builder.AddAttribute(sequence + 10, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, _ => { }));
            builder.AddEventStopPropagationAttribute(sequence + 11, "onmousedown", true);
            builder.AddEventPreventDefaultAttribute(sequence + 12, "onmousedown", true);
            builder.AddContent(sequence + 13, "...");
            builder.CloseElement();

            builder.CloseElement();
            return;
        }

        builder.OpenElement(sequence, "button");
        builder.AddAttribute(sequence + 1, "type", "button");
        builder.AddAttribute(sequence + 2, "class", "fx-cell-action-btn fx-cell-action-command");
        builder.AddAttribute(sequence + 3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, _ => HandleEditButtonClick(buttonItem, buttonCol)));
        builder.AddEventStopPropagationAttribute(sequence + 4, "onclick", true);
        builder.AddAttribute(sequence + 5, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, _ => { }));
        builder.AddEventStopPropagationAttribute(sequence + 6, "onmousedown", true);
        builder.AddEventPreventDefaultAttribute(sequence + 7, "onmousedown", true);
        builder.AddContent(sequence + 8, editButtonText);
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

    private sealed record GridEditOption(string Value, string Text);

    private IEnumerable<GridEditOption> GetEditOptions(GridColumn col)
    {
        if (col.EditOptions == null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in col.EditOptions)
        {
            var item = ParseEditOption(option);
            if (seen.Add(item.Value))
                yield return item;
        }
    }

    private static GridEditOption ParseEditOption(string? option)
    {
        var raw = option ?? string.Empty;
        return TryParseMappedEditOption(raw, out var value, out var text)
            ? new GridEditOption(value, text)
            : new GridEditOption(raw, raw);
    }

    private static bool TryParseMappedEditOption(string option, out string value, out string text)
    {
        value = option;
        text = option;

        if (string.IsNullOrEmpty(option) || option[0] != '#')
            return false;

        var separator = option.IndexOf(';');
        if (separator <= 1)
            return false;

        value = option[1..separator];
        text = option[(separator + 1)..];
        return true;
    }

    private static bool TryGetEditOptionDisplayValue(GridColumn col, object? value, out string text)
    {
        text = string.Empty;
        if (col.EditOptions == null)
            return false;

        var raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        foreach (var option in col.EditOptions)
        {
            var item = ParseEditOption(option);
            if (string.Equals(item.Value, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Text, raw, StringComparison.OrdinalIgnoreCase))
            {
                text = item.Text;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryApplyKeyToActiveBatchDropdownAsync(KeyboardEventArgs e)
    {
        if (_batchEditItem == null || string.IsNullOrWhiteSpace(_batchEditField))
            return false;
        if (!IsDropdownTypeSelectKey(e))
            return false;

        var col = ResolveBatchEditColumn(_batchEditField);
        if (col?.EditOptions?.Any() != true)
            return false;

        var options = GetEditOptions(col).ToList();
        if (options.Count == 0)
            return true;

        var now = DateTime.UtcNow;
        if (_batchDropdownTypeSelectBuffer.Length > 0
            && now - _batchDropdownTypeSelectLastInputUtc > DropdownTypeSelectResetDelay)
        {
            _batchDropdownTypeSelectBuffer = "";
        }

        _batchDropdownTypeSelectLastInputUtc = now;
        var requestedBuffer = _batchDropdownTypeSelectBuffer + e.Key;
        var repeatedCharacter = requestedBuffer.Length > 1
            && requestedBuffer.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(requestedBuffer[0]));

        var matchIndex = FindEditOptionTypeSelectMatchIndex(options, requestedBuffer, cycleFromCurrent: repeatedCharacter);
        if (matchIndex < 0 && requestedBuffer.Length > 1)
        {
            requestedBuffer = e.Key;
            matchIndex = FindEditOptionTypeSelectMatchIndex(options, requestedBuffer, cycleFromCurrent: true);
        }

        _batchDropdownTypeSelectBuffer = requestedBuffer;
        if (matchIndex < 0)
            return true;

        UpdateBatchEditValue(_batchEditItem, _batchEditField, options[matchIndex].Value);
        await CommitBatchEdit();
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private static bool IsDropdownTypeSelectKey(KeyboardEventArgs e)
    {
        return !e.AltKey
            && !e.CtrlKey
            && !e.MetaKey
            && e.Key.Length == 1
            && !char.IsControl(e.Key[0])
            && !char.IsWhiteSpace(e.Key[0]);
    }

    private int FindEditOptionTypeSelectMatchIndex(IReadOnlyList<GridEditOption> options, string prefix, bool cycleFromCurrent)
    {
        if (options.Count == 0 || string.IsNullOrEmpty(prefix))
            return -1;

        var start = 0;
        if (cycleFromCurrent)
        {
            var currentIndex = options.ToList().FindIndex(option =>
                string.Equals(option.Value, _batchEditValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Text, _batchEditValue, StringComparison.OrdinalIgnoreCase));
            start = currentIndex < 0 ? 0 : (currentIndex + 1) % options.Count;
        }

        for (var offset = 0; offset < options.Count; offset++)
        {
            var index = (start + offset) % options.Count;
            var option = options[index];
            if (option.Text.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)
                || (!string.Equals(option.Value, option.Text, StringComparison.Ordinal)
                    && option.Value.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)))
            {
                return index;
            }
        }

        return -1;
    }

    private void RenderBatchEditor(RenderTreeBuilder builder, int sequence, TValue item, int rowIndex, GridColumn col)
    {
        var editItem = item;
        var editField = col.Field;
        var options = col.EditOptions?.ToList();
        if (options is { Count: > 0 })
        {
            _pendingBatchEditFocus = false;

            builder.OpenComponent<DropDownListControl<string, GridEditOption>>(sequence);
            builder.SetKey((editItem, editField, rowIndex));
            builder.AddAttribute(sequence + 1, "DataSource", GetEditOptions(col).ToList());
            builder.AddAttribute(sequence + 2, "Value", _batchEditValue ?? string.Empty);
            builder.AddAttribute(sequence + 3, "ValueChanged", EventCallback.Factory.Create<string>(this, async value =>
            {
                UpdateBatchEditValue(editItem, editField, value ?? string.Empty);
                await CommitBatchEdit(editItem, editField);
            }));
            // +2px so the closed control covers the cell's right grid-line and
            // the arrow sits flush with the cell border (no ~2px gap). The
            // editing cell is overflow:visible and only one cell edits at a
            // time, so the small overhang is invisible. Paired with the
            // max-width:calc(100% + 2px) rule in GridControl.razor.css.
            builder.AddAttribute(sequence + 4, "Width", "calc(100% + 2px)");
            builder.AddAttribute(sequence + 5, "CssClass", "fx-batch-dropdown");
            builder.AddAttribute(sequence + 6, "OpenOnRender", _batchDropdownOpenOnRender);
            builder.AddAttribute(sequence + 7, "OpenOnArrowClickOnly", true);
            builder.AddAttribute(sequence + 8, "Closed", EventCallback.Factory.Create(this, () => CommitBatchEdit(editItem, editField)));
            builder.AddAttribute(sequence + 9, "OnKeyDown", EventCallback.Factory.Create<KeyboardEventArgs>(this, e => HandleBatchEditKeyDown(editItem, editField, e)));
            builder.AddAttribute(sequence + 10, "TextFieldName", nameof(GridEditOption.Text));
            builder.AddAttribute(sequence + 11, "ValueFieldName", nameof(GridEditOption.Value));
            builder.CloseComponent();
        }
        else
        {
            var inputType = GetEditorInputType(col);
            builder.SetKey(_batchEditGeneration);
            builder.OpenComponent<TextBoxControl>(sequence);
            builder.AddAttribute(sequence + 1, "InputType", inputType);
            builder.AddAttribute(sequence + 2, "CssClass", "fx-batch-input");
            builder.AddAttribute(sequence + 3, "Value", _batchEditValue);
            // Uncontrolled: the DOM owns the text while typing so parent
            // re-renders can't revert chars or reset the caret (no JS).
            builder.AddAttribute(sequence + 15, "Uncontrolled", true);
            builder.AddAttribute(sequence + 4, "style", GetEditorInputStyle(col));
            if (col.Type == ColumnType.Number && !col.ShowNumericSpinner)
                builder.AddAttribute(sequence + 5, "inputmode", "decimal");
            if (col.MaxLength.HasValue && col.MaxLength.Value > 0)
                builder.AddAttribute(sequence + 6, "MaxLength", col.MaxLength.Value);
            builder.AddAttribute(sequence + 13, "InputValueChanged", (Action<string?>)(value => UpdateBatchEditValue(editItem, editField, value ?? string.Empty)));
            builder.AddAttribute(sequence + 14, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => UpdateBatchEditValue(editItem, editField, value ?? string.Empty)));
            builder.AddAttribute(sequence + 7, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, e => HandleBatchEditKeyDown(editItem, editField, e)));
            builder.AddAttribute(sequence + 8, "onblur", EventCallback.Factory.Create(this, () => CommitBatchEdit(editItem, editField)));
            builder.AddAttribute(sequence + 12, "ElementReferenceCaptured", (Action<ElementReference>)CaptureBatchEditInputRef);
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
            builder.AddContent(sequence + 49, string.IsNullOrWhiteSpace(col.EditButtonText) ? "..." : col.EditButtonText);
            builder.CloseElement();
        }
    }

    private async Task CommitBatchEdit(bool deferTrailingNewRowEnsure = false)
    {
        if (_batchEditItem == null || string.IsNullOrEmpty(_batchEditField)) return;

        var primary = _batchEditItem;
        var field = _batchEditField;
        var selectedItemsBeforeCommit = _selectedItems.ToList();
        var selectedCellsBeforeCommit = _selectedCells.ToList();
        var batchEditColumn = ResolveBatchEditColumn(field);
        var currentValue = _batchEditValue ?? "";
        var newValue = ApplyColumnMaxLength(currentValue, batchEditColumn);
        if (!string.Equals(newValue, currentValue, StringComparison.Ordinal))
            _batchEditValue = newValue;

        // ── Build the fan-out target list ─────────────────────────────────
        // Only fan out across the multi-selection when the user actually
        // touched the input (_batchEditDirty). Without this guard, any
        // CommitBatchEdit that fires between StartBatchEdit and the user's
        // first keystroke (Blazor blur cascade on render, focus-shift
        // round-trip when the <input> first paints, etc.) would propagate
        // the cell's PRE-EDIT value across every selected row — exactly
        // the regression where clicking an editable cell visibly rewrote
        // sibling rows to the clicked row's existing value.
        //
        // Two-stage protection against stale references when we do fan out:
        // (1) Collect "live" selected items — those that are BOTH in the
        //     current DataSource AND in _selectedItems. Drop stale refs.
        // (2) Only fan out when the edited row itself is selected. A single
        //     selected row that differs from the editor row is a navigation
        //     race/stale selection, not a mass-edit request.
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
            if (batchEditColumn != null)
                cellMassEditTargets = ResolveSingleCellColumnMassEditTargets(primary, batchEditColumn);
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

        var shouldEnsureTrailingNewRow = false;
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
                // Compare via raw object value, not stringified — formatted display
                // ("5.00") would fail string-equality against the user's typed "5"
                // even though they're the same number.
                var oldValueObj = GetPropertyValue(item, field);
                var oldValueStr = oldValueObj?.ToString() ?? "";
                var changed = !string.Equals(oldValueStr, newValue, StringComparison.Ordinal);
                var shouldRaiseCellSave = changed || _batchEditDirty;

                if (changed)
                    SetPropertyValue(item, field, newValue);
                if (shouldRaiseCellSave)
                    committedAnyCell = true;

                // Fire OnCellSave for a real value change, or for an editor the
                // user actually touched. Do not fire a no-dirty/no-change blur:
                // hosts such as FDBGrid write the supplied Value into dynamic
                // dictionary rows, so a stale blank editor value can otherwise
                // overwrite a cell while focus is merely moving.
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

            shouldEnsureTrailingNewRow = committedAnyCell;
        }

        RestoreMultiSelectionAfterBatchCommit(selectedItemsBeforeCommit, selectedCellsBeforeCommit);

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
        ClearMouseDownClosedDropdownOpenSuppression();
        ClearBatchDropdownTypeSelectBuffer();
        _pendingBatchEditFocus = false;
        _pendingBatchEditSelectAll = false;
        _pendingBatchEditScrollIntoView = false;

        if (shouldEnsureTrailingNewRow)
            await EnsureTrailingNewRowAfterBatchCommitAsync(deferTrailingNewRowEnsure);
        else if (!deferTrailingNewRowEnsure)
            await FlushDeferredTrailingNewRowEnsureAsync();
    }

    private void RestoreMultiSelectionAfterBatchCommit(
        IReadOnlyList<TValue> selectedItemsBeforeCommit,
        IReadOnlyList<(int RowIndex, int CellIndex)> selectedCellsBeforeCommit)
    {
        if (SelectionSettingsRef?.Mode == SelectionMode.Cell)
        {
            if (selectedCellsBeforeCommit.Count <= 1)
                return;

            var maxRowIndex = DataSource?.Count() - 1 ?? -1;
            _selectedCells.Clear();
            foreach (var cell in selectedCellsBeforeCommit)
            {
                if (cell.RowIndex >= 0 && (maxRowIndex < 0 || cell.RowIndex <= maxRowIndex))
                    _selectedCells.Add(cell);
            }
            _selectedItems.Clear();
            return;
        }

        if (selectedItemsBeforeCommit.Count <= 1)
            return;

        var liveItems = DataSource != null
            ? new HashSet<TValue>(DataSource)
            : null;

        _selectedItems.Clear();
        foreach (var item in selectedItemsBeforeCommit)
        {
            if (liveItems == null || liveItems.Contains(item))
                _selectedItems.Add(item);
        }
    }

    private async Task EnsureTrailingNewRowAfterBatchCommitAsync(bool deferTrailingNewRowEnsure)
    {
        if (deferTrailingNewRowEnsure)
        {
            _deferredTrailingNewRowEnsureRequested = true;
            return;
        }

        _deferredTrailingNewRowEnsureRequested = false;
        await EnsureTrailingNewRowIfNeededAsync();
    }

    private async Task FlushDeferredTrailingNewRowEnsureAsync()
    {
        if (!_deferredTrailingNewRowEnsureRequested)
            return;

        _deferredTrailingNewRowEnsureRequested = false;
        await EnsureTrailingNewRowIfNeededAsync();
    }

    private bool IsActiveBatchEditSource(TValue item, string? field)
    {
        return _batchEditItem != null
            && !string.IsNullOrWhiteSpace(_batchEditField)
            && !string.IsNullOrWhiteSpace(field)
            && EqualityComparer<TValue>.Default.Equals(_batchEditItem, item)
            && string.Equals(_batchEditField, field, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsBatchEditingDifferentCell(TValue item, GridColumn? col)
    {
        return _batchEditItem != null
            && !string.IsNullOrWhiteSpace(_batchEditField)
            && !IsActiveBatchEditSource(item, col?.Field);
    }

    private Task CommitBatchEdit(TValue item, string? field)
    {
        return IsActiveBatchEditSource(item, field)
            ? CommitBatchEdit()
            : Task.CompletedTask;
    }

    private GridColumn? ResolveBatchEditColumn(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        return VisibleColumns.FirstOrDefault(c =>
            string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    private static string ApplyColumnMaxLength(string value, GridColumn? col)
    {
        var maxLength = col?.MaxLength;
        return maxLength is > 0 && value.Length > maxLength.Value
            ? value[..maxLength.Value]
            : value;
    }

    // ── Batch-editor focus application (owner editor fix 2026-07-24) ──────
    // The editor's ElementReference is captured in TextBoxControl's OWN
    // OnAfterRender, which can run AFTER the grid's OnAfterRender consumed the
    // pending-focus flag — the JS then received a null element, silently
    // no-oped, and the editor NEVER got focus (all further typing hit the grid
    // host and was dropped: "type 4000, only 4 lands"). Focus is now applied by
    // whichever happens LAST — the grid's render pass or the ref capture
    // (CaptureBatchEditInputRef) — and the pending flags survive until a REAL
    // attempt ran against a captured element. The in-flight latch stops
    // re-entry from renders during the await; HandleBatchEditKeyDown clears
    // _pendingBatchEditFocus the moment a key demonstrably arrives via the
    // focused editor, so input-originated keys are never double-applied.
    private void CaptureBatchEditInputRef(ElementReference er)
    {
        _batchEditInputRef = er;
        if (_pendingBatchEditFocus && !_batchEditFocusInFlight)
            _ = InvokeAsync(ApplyPendingBatchEditFocusAsync);
    }

    private async Task ApplyPendingBatchEditFocusAsync()
    {
        if (!_pendingBatchEditFocus || string.IsNullOrEmpty(_batchEditField) || _batchEditFocusInFlight)
            return;

        // Editor not mounted/captured yet — keep the flags armed; the capture
        // callback re-invokes this method as soon as the reference exists.
        if (string.IsNullOrEmpty(_batchEditInputRef.Id))
            return;

        _batchEditFocusInFlight = true;
        var selectAll = _pendingBatchEditSelectAll;
        var clientX = _pendingBatchEditClientX;
        var scrollIntoView = _pendingBatchEditScrollIntoView;
        _pendingBatchEditClientX = null;
        _pendingBatchEditScrollIntoView = false;

        try
        {
            // Programmatic edit (host "New row" / BeginEditCellAsync): focus the
            // input and LET the browser scroll it into view (pure Blazor, no JS
            // scrollIntoView; the sticky header stays visible through the scroll).
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
                catch { /* best-effort */ }
                // Best-effort: if the module can't be imported the user still
                // gets a normal caret and can click into the input.
            }
        }
        finally
        {
            // A real attempt ran against a captured element: from here on keys
            // reach the input itself — stop the pending-editor key routing.
            _pendingBatchEditFocus = false;
            _pendingBatchEditSelectAll = false;
            _batchEditFocusInFlight = false;
        }
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

        incomingValue = ApplyColumnMaxLength(incomingValue, ResolveBatchEditColumn(field));
        _batchEditValue = incomingValue;
        // Only typed input flips the dirty flag — auto-fired commits that
        // happen before the user actually changes anything (Blazor blur
        // cascade, focus-shift round-trips, etc.) must NOT propagate the
        // pre-edit value across the multi-selection. See CommitBatchEdit.
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
        // A key arriving via the editor's OWN handler proves the input has DOM
        // focus — stop the host-level pending-editor key routing immediately so
        // the same keystroke (which also bubbles to the grid host's onkeydown)
        // is never applied twice (owner editor fix 2026-07-24).
        _pendingBatchEditFocus = false;

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
            // Cancel edit without saving
            _batchEditItem = default;
            _batchEditRowIndex = -1;
            _batchEditField = null;
            _batchEditValue = null;
            _batchEditReplaceOnFirstInput = false;
            _batchDropdownOpenOnRender = false;
            ClearBatchDropdownTypeSelectBuffer();
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

        // A candidate can be vetoed by OnCellEdit. If so, keep walking in
        // displayed order instead of dropping focus out of the grid.
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
        if (!beginEdit)
            PreserveRowWindowOnNextListChange(row);
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

        // A keyboard move must save the current editor against its original
        // row before selecting the destination row. If selection changes first,
        // CommitBatchEdit can mistake the destination row for a multi-edit
        // target and copy the old cell value into it.
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
            // Without caret-position support, keep native text-editor behavior.
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
                await ExportToXlsxAsync();
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
        if (await TryHandleCellContextShortcutAsync(e))
            return;

        if (await TryApplyKeyToPendingBatchEditorAsync(e))
            return;

        if (await TryApplyKeyToActiveBatchDropdownAsync(e))
            return;

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
            await CancelPendingTypeAheadAndClearSelectionAsync();
            return;
        }

        if ((e.Key == "Enter" || e.Key == "NumpadEnter") && await TryFillSelectedCellsFromActiveCellAsync(e))
            return;

        if ((e.Key is " " or "Spacebar" or "Enter" or "NumpadEnter")
            && await TryToggleActiveCheckboxAsync())
        {
            return;
        }

        if (await TryInvokeActiveEditButtonAsync(e))
            return;

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

        if (_batchEditItem == null
            && _typeAheadBuffer.Length > 0
            && HasRowSelectionTypeAheadSelection()
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

        // Type-ahead: when multiple rows are selected and no batch edit is active,
        // let user type a value and press Enter to apply it to the captured column.
        if (HasRowSelectionTypeAheadSelection() && _batchEditItem == null)
        {
            var targetCol = ResolveTypeAheadTargetColumn();
            if (e.Key.Length == 1)
            {
                if (targetCol == null || !IsEditableTypeAheadKey(e, targetCol))
                    return;

                // Prevent multiple decimal points
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
                await CancelPendingTypeAheadAndClearSelectionAsync();
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

        if (_batchEditItem == null && !_isEditing && await HandleTypeSearchKeyAsync(e))
            return;

        if (EnableTypeSearch && _typeSearchBuffer.Length > 0)
            ClearTypeSearchBuffer();
    }

    private async Task<bool> TryApplyKeyToPendingBatchEditorAsync(KeyboardEventArgs e)
    {
        if (_batchEditItem == null || string.IsNullOrWhiteSpace(_batchEditField))
            return false;
        if (!_pendingBatchEditFocus)
            return false;
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return false;

        var col = ResolveBatchEditColumn(_batchEditField);
        if (col == null || col.EditOptions?.Any() == true || col.Type == ColumnType.CheckBox)
            return false;

        if (e.Key == "Backspace")
        {
            var current = _batchEditValue ?? string.Empty;
            if (current.Length == 0)
                return true;

            UpdateBatchEditValue(_batchEditItem, _batchEditField, current[..^1]);
            await InvokeAsync(StateHasChanged);
            return true;
        }

        if (!IsEditableTypeAheadKey(e, col))
            return false;

        var nextValue = _pendingBatchEditSelectAll && !_batchEditDirty
            ? e.Key
            : (_batchEditValue ?? string.Empty) + e.Key;
        _pendingBatchEditSelectAll = false;
        UpdateBatchEditValue(_batchEditItem, _batchEditField, nextValue);
        await InvokeAsync(StateHasChanged);
        return true;
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
        if (_batchEditItem != null || !HasRowSelectionTypeAheadSelection())
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

    private async Task<bool> CommitPendingRowSelectionTypeAheadAsync(bool collapseSelection = true)
    {
        if (_typeAheadBuffer.Length == 0 || !HasRowSelectionTypeAheadSelection())
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

        var selectionChanged = RestoreTypeAheadAnchor(anchor, collapseSelection);
        _typeAheadBuffer = "";
        await NotifyTypeAheadChangedAsync();

        await FocusGridHostAsync();
        if (selectionChanged)
            await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private async Task<bool> CommitPendingTypeAheadFromPointerAsync()
    {
        if (_batchEditItem != null || _typeAheadBuffer.Length == 0)
            return false;

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell)
        {
            return await CommitPendingSingleCellTypeAheadAsync();
        }

        return await CommitPendingRowSelectionTypeAheadAsync(collapseSelection: false);
    }

    private async Task HandleGridFocusOut(FocusEventArgs _)
    {
        if (_batchEditItem != null || _typeAheadBuffer.Length == 0)
            return;

        if (BatchEditBehavior == GridBatchEditBehavior.SingleCell)
        {
            await CommitPendingSingleCellTypeAheadAsync();
        }
        else
        {
            await CommitPendingRowSelectionTypeAheadAsync(collapseSelection: false);
        }

        _suppressNextPointerSelectionAfterTypeAheadCommit = false;
    }

    private async Task CancelPendingTypeAheadAndClearSelectionAsync()
    {
        var selectionChanged = _selectedItems.Count > 0 || _selectedCells.Count > 0;

        _typeAheadBuffer = "";
        _selectedItems.Clear();
        _selectedCells.Clear();
        _pointerFillCell = null;
        _suppressNextPointerSelectionAfterTypeAheadCommit = false;
        ClearCellDragState();
        ResetRowSelectionTypeAheadTarget();

        await NotifyTypeAheadChangedAsync();
        if (selectionChanged)
            await NotifySelectionChangedAsync(GridSelectionChangeSource.Keyboard);
        await InvokeAsync(StateHasChanged);
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
            _suppressNextClickAfterDragSelect = false;
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

        _batchEditValue = ApplyColumnMaxLength(e.Key, col);
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

        if (!HasSingleCellBulkEditSelection())
            return await TryStartActiveBatchEditFromTypedKeyAsync(e);

        if (!TryGetActiveEditableCell(out var item, out var col))
            return false;

        if ((e.Key == "Enter" || e.Key == "NumpadEnter") && _typeAheadBuffer.Length > 0)
        {
            await CommitSingleCellTypeAheadAsync(item, col);
            return true;
        }

        if (e.Key == "Escape" && _typeAheadBuffer.Length > 0)
        {
            await CancelPendingTypeAheadAndClearSelectionAsync();
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
        if (SingleCellColumnMassEditEnabled && SelectionSettingsRef?.Mode == SelectionMode.Cell)
            return _selectedCells.Count > 1;

        return _selectedItems.Count > 1;
    }

    private bool HasRowSelectionTypeAheadSelection()
    {
        return SelectionSettingsRef?.Mode != SelectionMode.Cell
            && _selectedItems.Count > 1;
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

    private async Task<bool> TryFillSelectedCellsFromActiveCellAsync(KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey || e.ShiftKey)
            return false;
        if (_isEditing || _batchEditItem != null)
            return false;
        if (!SingleCellColumnMassEditEnabled || SelectionSettingsRef?.Mode != SelectionMode.Cell)
            return false;
        if (_selectedCells.Count <= 1 || !_activeCell.HasValue)
            return false;

        var activeCell = _activeCell.Value;
        var col = VisibleColumns.ElementAtOrDefault(activeCell.CellIndex);
        if (col == null
            || string.IsNullOrWhiteSpace(col.Field)
            || col.IsPrimaryKey
            || (!col.AllowEditing && !col.AllowCellDragSelection))
        {
            return false;
        }

        if (_selectedCells.Any(c => c.CellIndex != activeCell.CellIndex))
            return false;

        var source = GetItemAtResolvedRowIndex(activeCell.RowIndex);
        if (source == null)
            return false;

        var targets = ResolveSingleCellColumnMassEditTargets(source, col);
        if (targets.Count <= 1)
            return false;

        var sourceValue = GetPropertyValue(source, col.Field);
        if (EventsRef?.OnTypeAheadCommit.HasDelegate == true)
        {
            await EventsRef.OnTypeAheadCommit.InvokeAsync(new TypeAheadCommitArgs<TValue>
            {
                SelectedItems = targets,
                ColumnName = col.Field,
                Value = sourceValue?.ToString() ?? string.Empty
            });
        }
        else
        {
            var committedAnyCell = false;
            foreach (var target in targets)
            {
                if (EqualityComparer<TValue>.Default.Equals(target, source))
                    continue;

                var oldValue = GetPropertyValue(target, col.Field);
                if (object.Equals(oldValue, sourceValue))
                    continue;

                if (!SetPropertyObjectValue(target, col.Field, sourceValue))
                    continue;

                committedAnyCell = true;
                if (EventsRef?.OnCellSave.HasDelegate == true)
                {
                    await EventsRef.OnCellSave.InvokeAsync(new CellSaveArgs<TValue>
                    {
                        Data = target,
                        ColumnName = col.Field,
                        Value = sourceValue
                    });
                }
            }

            if (committedAnyCell)
                await EnsureTrailingNewRowIfNeededAsync();
        }

        await FocusGridHostAsync();
        await InvokeAsync(StateHasChanged);
        return true;
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

    /// <summary>
    /// Fires <see cref="GridControlEvents{TValue}.TypeAheadChanged"/>
    /// with the current buffer text. Centralises the notification so
    /// every mutation path in <c>HandleKeyDown</c> emits a consistent
    /// signal. No-op when no consumer subscribes.
    /// </summary>
    private async Task NotifyTypeAheadChangedAsync()
    {
        if (EventsRef?.TypeAheadChanged.HasDelegate == true)
            await EventsRef.TypeAheadChanged.InvokeAsync(_typeAheadBuffer);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ── DRAG & DROP GROUPING ─────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════

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
        // Browser drag event ordering may raise dragend before drop in some
        // cases. Give drop a brief window to process before clearing source.
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

    /// <summary>Handles a drop on a header half-zone and reorders the dragged
    /// column relative to target (left half = before, right half = after).</summary>
    private async Task HandleHeaderDropZoneDrop(string targetField, bool insertAfter, DragEventArgs _)
    {
        try
        {
            if (!AllowColumnReorder) return;

            var fromField = _draggingColumnField;
            if (string.IsNullOrEmpty(fromField))
            {
                // Fallback if dragend arrived before drop on this browser path.
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

    // ══════════════════════════════════════════════════════════════════════
    // ── HEADER CONTEXT MENU ──────────────────────────────────────────────
    // Right-click on a column header opens a small menu offering grouping,
    // expand/collapse, hide-this-column, insert-a-column (from hidden ones),
    // rename-this-column, plus print and save-as. Generic across all grids.
    // ══════════════════════════════════════════════════════════════════════

    private bool _showHeaderContextMenu;
    private string _headerContextMenuField = "";
    private string _renameColumnField = "";
    private double _headerContextMenuX;
    private double _headerContextMenuY;
    private bool _showCellContextMenu;
    private double _cellContextMenuX;
    private double _cellContextMenuY;
    private TValue? _cellContextMenuItem;
    private GridColumn? _cellContextMenuColumn;
    private string _cellContextClipboardText = "";
    private bool _hasCellContextClipboard;
    // Reserved for the upcoming "insert column" submenu (currently the parent
    // header-menu item triggers HeaderMenuInsertColumn directly without a
    // submenu; this flag will gate the submenu UI when that lands).
#pragma warning disable CS0414
    private bool _showInsertColumnSubmenu;
#pragma warning restore CS0414
    private bool _showRenameColumn;
    private string _renameColumnDraft = "";
    private bool _showPrintOptionsDialog;
    private bool _printDefaultsInitialized;
    private GridPdfOrientation _lastDefaultPrintOrientation;
    private GridPdfPageSize _lastDefaultPrintPageSize;
    private GridPdfColumnLayout _lastDefaultPrintColumnLayout;
    private bool _lastDefaultPrintGridLines = true;
    private GridPdfZoomMode _lastDefaultPrintZoomMode = GridPdfZoomMode.FitToPage;
    private int _lastDefaultPrintZoomPercent = 100;
    private GridPdfOrientation _printOrientation = GridPdfOrientation.Portrait;
    private GridPdfPageSize _printPageSize = GridPdfPageSize.Letter;
    private GridPdfColumnLayout _printColumnLayout = GridPdfColumnLayout.WrapText;
    private bool _printShowGridLines = true;
    private GridPdfZoomMode _printZoomMode = GridPdfZoomMode.FitToPage;
    private int _printZoomPercent = 100;

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
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the column should render — checks the runtime
    /// override first, falls back to the column's declared <c>Visible</c>.</summary>
    internal bool IsColumnVisible(GridColumn col)
        => _visibilityOverrides.TryGetValue(col.Field, out var ov) ? ov : col.Visible;

    /// <summary>Right-click on a column header opens the context menu.</summary>
    private void OpenHeaderContextMenu(MouseEventArgs e, string field)
    {
        if (!EnableHeaderContextMenu)
            return;

        CloseCellContextMenu();
        _headerContextMenuField = field;
        _headerContextMenuX = e.ClientX;
        _headerContextMenuY = e.ClientY;
        _showHeaderContextMenu = true;
        OpenMenuKeyboardNav();
        _showInsertColumnSubmenu = false;
        _showRenameColumn = false;
    }

    private void CloseHeaderContextMenu()
    {
        _showHeaderContextMenu = false;
        _showInsertColumnSubmenu = false;
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
            if (CurrentHeaderColumn?.AllowHiding == false)
                return false;
            // Can't hide the last visible column — grids need at least one.
            var visibleCount = Columns.Count(IsColumnVisible);
            return visibleCount > 1;
        }
    }

    private IReadOnlyList<GridColumn> HiddenColumns =>
        Columns.Where(c => !IsColumnVisible(c) && !string.IsNullOrEmpty(c.Field)).ToList();

    private IReadOnlyList<GridColumn> HeaderContextMenuColumns =>
        Columns.Where(c => !string.IsNullOrWhiteSpace(c.Field)).ToList();

    private bool CanHideColumn(GridColumn col) =>
        col.AllowHiding && IsColumnVisible(col) && Columns.Count(IsColumnVisible) > 1;

    private bool CanToggleHeaderContextColumn(GridColumn col) =>
        IsColumnVisible(col) ? CanHideColumn(col) : true;

    private async Task ToggleHeaderContextColumnAsync(GridColumn col)
    {
        if (!CanToggleHeaderContextColumn(col))
            return;

        await SetColumnPanelVisibleAsync(col, !IsColumnVisible(col));
    }

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
        await SetColumnPanelVisibleAsync(col, false);
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
        await SetColumnPanelVisibleAsync(col, true);
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
        public bool CanHide { get; init; } = true;
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
                    Visible = ResolveChooseColumnVisible(d),
                    CanHide = true
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
                    Visible = IsColumnVisible(c),
                    CanHide = c.AllowHiding
                })
                .ToList();
        }
        _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
        _showChooseColumnsDialog = true;
        StateHasChanged();
    }

    private void ChooseColumnsSelect(string field) => _chooseColumnsSelectedField = field;

    private bool ResolveChooseColumnVisible(ChooseColumnDescriptor descriptor) =>
        _visibilityOverrides.TryGetValue(descriptor.Field, out var visible)
            ? visible
            : descriptor.Visible;

    private static void ChooseColumnsSetVisible(ChooseColumnRow row, ChangeEventArgs e)
    {
        if (!row.CanHide)
        {
            row.Visible = true;
            return;
        }

        row.Visible = e.Value switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var value) => value,
            _ => row.Visible
        };
    }

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
        if (row is { CanHide: true }) row.Visible = false;
    }

    private void ChooseColumnsShowAll()
    {
        foreach (var r in _chooseColumnsRows) r.Visible = true;
    }

    private void ChooseColumnsHideAll()
    {
        foreach (var r in _chooseColumnsRows)
            if (r.CanHide)
                r.Visible = false;
    }

    private void ChooseColumnsRestoreDefault()
    {
        // Preferred path: host supplied a factory-default schema (e.g. from a
        // layout service). Reset each listed column's checked state + order to
        // match it; columns the default doesn't mention are left as-is. The grid
        // stays general-purpose — it just applies whatever the host handed in.
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
            {
                if (defVisByField.TryGetValue(row.Field, out var vis))
                    row.Visible = vis;
                if (!row.CanHide)
                    row.Visible = true;
            }

            _chooseColumnsRows = _chooseColumnsRows
                .Select((r, idx) => (r, idx))
                .OrderBy(t => defOrderByField.TryGetValue(t.r.Field, out var o) ? o : int.MaxValue)
                .ThenBy(t => t.idx)
                .Select(t => t.r)
                .ToList();

            _chooseColumnsSelectedField = _chooseColumnsRows.FirstOrDefault()?.Field ?? "";
            return;
        }

        // Host supplied the full AvailableColumns universe (visible + hidden).
        // The grid only renders the VISIBLE columns as <GridColumn> children, so
        // its live `Columns` collection — and the _originalColumnOrder snapshot
        // taken from it — omit the hidden ones. Restoring from `Columns` would
        // therefore DROP every hidden column out of the dialog (the truncated
        // list bug). Rebuild from AvailableColumns so the full set survives a
        // restore. (The DefaultColumns path above handles true factory-default
        // verdicts when a host wires it; this is the no-DefaultColumns safety net.)
        if (AvailableColumns != null)
        {
            _chooseColumnsRows = AvailableColumns
                .Where(d => !string.IsNullOrEmpty(d.Field))
                .Select(d => new ChooseColumnRow
                {
                    Field = d.Field,
                    Header = string.IsNullOrEmpty(d.Header) ? d.Field : d.Header,
                    Visible = d.Visible,
                    CanHide = true
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
                Visible = _originalVisibility.TryGetValue(f, out var v) ? v : byField[f].Visible,
                CanHide = byField[f].AllowHiding
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
                Visible = r.CanHide ? r.Visible : true
            })
            .ToList();

        var layoutSnapshot = BuildChooseColumnsSnapshotSettings(snapshot);

        foreach (var item in snapshot)
            _visibilityOverrides[item.Field] = item.Visible;

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
            _columnsContainer?.ReorderColumns(snapshot.Select(r => r.Field));
            await SaveSnapshotSettingsAsync(snapshot);
            await FireLayoutChangedAsync(layoutSnapshot);
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
        _renameColumnField = col.Field;
        _renameColumnDraft = "";
        _showHeaderContextMenu = false;
        _showRenameColumn = true;
        _showInsertColumnSubmenu = false;
    }

    private async Task HeaderMenuCommitRename()
    {
        var field = _renameColumnField;
        var draft = _renameColumnDraft?.Trim() ?? "";
        HeaderMenuCancelRename();
        if (string.IsNullOrEmpty(field)) return;
        // VB6 FMain.frm:2419 `If s <> "" Then` — OK on an empty box is a no-op.
        if (string.IsNullOrEmpty(draft)) return;
        _headerOverrides[field] = draft;
        StateHasChanged();
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
    }

    private void HeaderMenuCancelRename()
    {
        _showRenameColumn = false;
        _renameColumnField = "";
        _renameColumnDraft = "";
    }

    private GridColumn? RenameColumn =>
        string.IsNullOrWhiteSpace(_renameColumnField)
            ? null
            : Columns.FirstOrDefault(c => string.Equals(c.Field, _renameColumnField, StringComparison.Ordinal));

    /// <summary>VB6 passes the raw column KEY as the InputBox title
    /// (FMain.frm:2418 <c>InputBox(..., MouseCtrl.ColKey(MouseCol))</c>) — not the
    /// caption and not a prettified form of it.</summary>
    private string RenameColumnDialogTitle =>
        string.IsNullOrWhiteSpace(_renameColumnField) ? "Column" : _renameColumnField;

    private string RenameColumnPrompt =>
        $"Change description from \"{HeaderColumnDisplay(RenameColumn)}\" to:";

    // ══════════════════════════════════════════════════════════════════════
    // ── CONTEXT-MENU KEYBOARD NAVIGATION ─────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════
    // VB6 popped these menus with the Win32 PopupMenu API, so first-item
    // highlight, Up/Down, Enter and Escape came from the OS. These menus are
    // <button>s in a div, so the movement has to be driven here. Enter and
    // Space are left alone deliberately — a focused <button> already fires its
    // click on both, and swallowing them would break activation.

    private ElementReference _headerMenuElement;
    private ElementReference _cellMenuElement;
    private bool _focusMenuPending;

    private void OpenMenuKeyboardNav()
    {
        _focusMenuPending = true;
    }

    private async Task HandleMenuKeyDownAsync(KeyboardEventArgs e)
    {
        var menu = _showHeaderContextMenu ? _headerMenuElement
                 : _showCellContextMenu ? _cellMenuElement
                 : default;

        string mode;
        switch (e.Key)
        {
            case "ArrowDown": mode = "next"; break;
            case "ArrowUp": mode = "prev"; break;
            case "Home": mode = "first"; break;
            case "End": mode = "last"; break;
            case "Enter":
            case " ":
                await ActivateMenuItemAsync(menu);
                return;
            case "Escape":
                CloseHeaderContextMenu();
                CloseCellContextMenu();
                await FocusGridHostAsync();
                return;
            default:
                return;
        }

        await FocusMenuItemAsync(menu, mode);
    }

    private async Task ActivateMenuItemAsync(ElementReference menu)
    {
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("activateMenuItem", menu);
        }
        catch (Exception)
        {
        }
    }

    private async Task FocusMenuItemAsync(ElementReference menu, string mode)
    {
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            await _gridJsModule.InvokeVoidAsync("focusMenuItem", menu, mode);
        }
        catch (Exception)
        {
            // Without the module the menu still works by mouse and by Tab —
            // only the arrow-key shortcut is lost.
        }
    }

    private async Task HeaderMenuBestFitColumn()
    {
        var col = CurrentHeaderColumn;
        CloseHeaderContextMenu();
        if (col != null)
            await AutoFitCoreAsync(new List<GridColumn> { col });
    }

    private async Task HeaderMenuBestFitAllColumns()
    {
        CloseHeaderContextMenu();
        await AutoFitColumnsAsync();
    }

    private async Task HeaderMenuBestFitToGrid()
    {
        CloseHeaderContextMenu();
        await FitColumnsToGridAsync();
    }

    /// <summary>Scales every visible column proportionally so together they exactly fill the
    /// grid's available width — unlike Best Fit, which sizes each column to its content.</summary>
    public async Task FitColumnsToGridAsync()
    {
        var targets = VisibleColumns.ToList();
        if (targets.Count == 0)
            return;

        double available = 0;
        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            available = await _gridJsModule.InvokeAsync<double>("measureGridAvailableWidth", _gridHostElement);
        }
        catch
        {
        }
        if (available <= 0 && _autoFitCeilingPx > 80)
            available = _autoFitCeilingPx;
        if (available <= 0)
            return;

        if (ShowCheckboxColumn) available -= 50;
        if (ShowRowReorderColumn) available -= RowReorderColumnWidth;
        if (ShowRowSelectorHandleColumn) available -= ResolvedRowSelectorHandleWidth;
        if (available <= targets.Count * 20)
            return;

        var current = targets.Sum(c => { var w = GetColumnWidthPx(c); return w > 0 ? w : 120d; });
        if (current <= 0)
            return;

        var factor = available / current;
        var assigned = 0d;
        var changes = new List<(GridColumn Col, double Old, double New)>();
        for (var i = 0; i < targets.Count; i++)
        {
            var col = targets[i];
            var old = GetColumnWidthPx(col);
            var basis = old > 0 ? old : 120d;
            // last column takes the rounding remainder so the sum lands exactly on available
            var width = i == targets.Count - 1
                ? Math.Round(available - assigned)
                : Math.Round(basis * factor);
            var (min, _) = ResolveAutoFitBounds(col);
            width = Math.Max(width, min);
            assigned += width;
            if (Math.Abs(old - width) >= 0.5)
                changes.Add((col, old, width));
        }
        if (changes.Count == 0)
            return;

        foreach (var (col, old, width) in changes)
        {
            if (EventsRef?.ColumnResizing.HasDelegate == true)
            {
                var resizing = new ResizeEventArgs { Field = col.Field, OldWidth = old, NewWidth = width };
                await EventsRef.ColumnResizing.InvokeAsync(resizing);
                if (resizing.Cancel)
                    continue;
            }
            col.RuntimeWidth = width;
            if (EventsRef?.ColumnResized.HasDelegate == true)
                await EventsRef.ColumnResized.InvokeAsync(
                    new ResizeEventArgs { Field = col.Field, OldWidth = old, NewWidth = width });
        }

        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HeaderMenuPrint()
    {
        CloseHeaderContextMenu();
        ResetPrintOptionsToDefaults();

        if (ShowPrintOptionsDialog)
        {
            _showPrintOptionsDialog = true;
            return;
        }

        await ExportToPdfAsync("grid-export.pdf", "", CreateCurrentPdfPrintOptions(), showCompletionStatus: false);
    }

    private async Task HeaderMenuSaveAs()
    {
        CloseHeaderContextMenu();
        var title = ResolveExportTitle("Grid Export");
        var table = BuildExportTable(title);
        var result = GridExporter.Export(table, GridExportFormat.Xlsx);
        var saveResult = await GridExporter.SaveAsync(JsRuntime, result);
        await ShowExportResultAsync(table.Rows.Count, saveResult);
    }

    private void ResetPrintOptionsToDefaults()
    {
        _printOrientation = DefaultPrintOrientation;
        _printPageSize = DefaultPrintPageSize;
        _printColumnLayout = DefaultPrintColumnLayout;
        _printShowGridLines = DefaultPrintGridLines;
        _printZoomMode = DefaultPrintZoomMode;
        _printZoomPercent = ClampPrintZoomPercent(DefaultPrintZoomPercent);
    }

    private GridPdfPrintOptions CreateCurrentPdfPrintOptions() =>
        new()
        {
            Orientation = _printOrientation,
            PageSize = _printPageSize,
            ColumnLayout = _printColumnLayout,
            IncludeColumnHeaders = true,
            ShowGridLines = _printShowGridLines,
            ZoomMode = _printZoomMode,
            ZoomPercent = ClampPrintZoomPercent(_printZoomPercent)
        };

    private async Task PrintOptionsOk()
    {
        _printZoomPercent = ClampPrintZoomPercent(_printZoomPercent);
        _showPrintOptionsDialog = false;
        await ExportToPdfAsync("grid-export.pdf", "", CreateCurrentPdfPrintOptions(), showCompletionStatus: false);
    }

    private void PrintOptionsCancel()
    {
        _showPrintOptionsDialog = false;
    }

    private static int ClampPrintZoomPercent(int zoomPercent) =>
        Math.Clamp(zoomPercent, 25, 200);

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

    /// <summary>Returns the index (within <paramref name="visibleCols"/>) of the
    /// first column that has an <see cref="AggregateColumn"/> configured by any
    /// of <paramref name="headerAggRows"/>; -1 if none. Used to compute the
    /// shrunk colspan for the group label cell so the column-aligned aggregate
    /// cells can sit on the same row.</summary>
    private static int ComputeFirstHeaderAggregateColumnIndex(
        List<AggregateRow> headerAggRows, IReadOnlyList<GridColumn> visibleCols)
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

    private async Task FocusGroupHeaderAsync(GroupResult<TValue> group)
    {
        _focusedGroupPath = group.GroupPath;
        _selectedItems.Clear();
        _selectedCells.Clear();
        _activeCell = null;
        await NotifySelectionChangedAsync(GridSelectionChangeSource.Pointer);
        await FocusGridHostAsync();
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
            var groupHeaderClass = string.Equals(_focusedGroupPath, group.GroupPath, StringComparison.Ordinal)
                ? "fx-group-header-row fx-group-header-focused"
                : "fx-group-header-row";
            builder.OpenElement(0, "tr");
            builder.AddAttribute(1, "class", groupHeaderClass);
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => FocusGroupHeaderAsync(group)));

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
                var visibleColsForSpan = GetRenderVisibleColumns();
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
                builder.AddAttribute(34, "onclick", EventCallback.Factory.Create(this, () => ToggleGroupCollapse(group)));
                builder.AddEventStopPropagationAttribute(35, "onclick", true);
                builder.AddContent(36, iconGlyph);
                builder.CloseElement();
            }

            // The grouped column's VALUE is rendered as a plain span, with an
            // optional inline color override when the consumer set
            // GroupedColumnColor (e.g. the app's brand blue). When that
            // parameter is empty (toolkit default) no color is applied — the
            // value inherits from the surrounding row text color.
            builder.OpenElement(40, "span");
            builder.AddAttribute(41, "class", "fx-group-header-value");
            if (!string.IsNullOrEmpty(GroupItemTextStyle))
                builder.AddAttribute(42, "style", GroupItemTextStyle);
            builder.AddContent(43, string.IsNullOrWhiteSpace(group.DisplayText) ? $"{group.Key}" : group.DisplayText);
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
                var visibleCols = GetRenderVisibleColumns();
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
                        var isCellSelectedRow = IsCellSelectionRow(item, resolvedRowIdx);
                        var rowCssClass = GetRowCssClass(item, resolvedRowIdx);

	                        builder.OpenElement(70, "tr");
	                        builder.SetKey(item);
	                        builder.AddAttribute(71, "class",
	                            $"fx-row {(rowIdx % 2 == 1 && EnableAltRow ? "fx-alt-row" : "")} {(isSelected && HighlightSelectedRows ? "fx-selected" : "")} {(isCellSelectedRow && HighlightSelectedRows ? "fx-cell-row-selected" : "")} {(EnableHover ? "fx-hover" : "")} {rowCssClass}");
                        builder.AddAttribute(72, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleRowClick(item, resolvedRowIdx, e)));
                        // Drag-select wiring — see HandleRowMouseDown /
                        // HandleRowMouseEnter for the protocol. Keep
                        // sequence numbers monotonic alongside 71/72/73.
                        builder.AddAttribute(74, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance, (Action<MouseEventArgs>)(e => HandleRowMouseDown(item, resolvedRowIdx, e))));
                        // Inline style as a belt-and-suspenders backstop —
                        // see flat path for the rationale. Wins any CSS
                        // specificity / isolation fight by spec.
                        var rowStyle = GetRowStyle(item, currentIdx, isSelected);
                        if (rowStyle.Length > 0)
                            builder.AddAttribute(73, "style", rowStyle);

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
                            builder.OpenComponent<CheckBoxControl>(93);
                            builder.AddAttribute(94, "Checked", isSelected);
                            builder.AddAttribute(95, "CheckedChanged",
                                EventCallback.Factory.Create<bool>(this,
                                    _ => ToggleRowSelection(item, resolvedRowIdx)));
                            builder.AddAttribute(96, "TabIndex", -1);
                            builder.AddAttribute(97, "StopClickPropagation", true);
                            builder.AddAttribute(98, "StopMouseDownPropagation", true);
                            builder.CloseComponent();
                            builder.CloseElement();
                        }

                        // Data cells
                        var colIdx = 0;
                        foreach (var col in GetRenderVisibleColumns())
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
                            var isPointerFillCell = IsPointerFillCell(resolvedRowIdx, capturedColIdx);
                            var showsEditableCue = CanShowEditableCellCue(capturedCol);
                            var editableClass = showsEditableCue ? " fx-cell-editable" : string.Empty;
                            var activeClass = isActiveCell
                                ? showsEditableCue ? " fx-cell-active fx-cell-active-editable" : " fx-cell-active"
                                : string.Empty;
                            var typeAheadClass = isTypeAheadPreview ? " fx-typeahead-preview-cell" : string.Empty;
                            var rowSelectionEditTargetClass = ShouldShowRowSelectionEditColumnCue(item, capturedCol)
                                ? " fx-row-selection-edit-target"
                                : string.Empty;
                            var cellClass = "fx-cell"
                                + (isCellSelected ? " fx-cell-selected" : string.Empty)
                                + (isPointerFillCell ? " fx-cell-pointer-selected" : string.Empty)
                                + (isBatchEditing ? " fx-batch-editing" : string.Empty)
                                + (isBatchDropdownEditing ? " fx-batch-dropdown-editing" : string.Empty)
                                + activeClass + editableClass + typeAheadClass + rowSelectionEditTargetClass;
                            builder.OpenElement(100, "td");
                            builder.AddAttribute(101, "class", cellClass);
                            // data-field exposes the bound field name as a CSS hook so
                            // consumers can colour / style specific columns from their
                            // own .razor.css (e.g. FPricingWorkSheet greens the
                            // Published* columns and blues the proposed-price set).
                            // Purely cosmetic — no behaviour changes hang off this.
                            if (!string.IsNullOrEmpty(capturedCol.Field))
                                builder.AddAttribute(106, "data-field", capturedCol.Field);
                            builder.AddAttribute(102, "style", col.GetCellStyle());
                            builder.AddAttribute(107, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance, e => HandleCellMouseDown(item, resolvedRowIdx, capturedColIdx, e)));
                            builder.AddEventStopPropagationAttribute(108, "onmousedown", true);
                            builder.AddAttribute(111, "oncontextmenu", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellContextMenu(item, resolvedRowIdx, capturedColIdx, e)));
                            if (EnableCellContextMenu)
                            {
                                builder.AddEventPreventDefaultAttribute(116, "oncontextmenu", true);
                                builder.AddEventStopPropagationAttribute(117, "oncontextmenu", true);
                            }

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
                                    RenderDisplayCellContent(builder, 130, item, col, isActiveCell);
                                    builder.CloseElement();
                                    colIdx++;
                                    continue;
                                }

                                // VB6 flexDTBoolean cell: a togglable checkbox when the
                                // column is editable (disabled only for read-only columns).
                                // Toggling writes the bound bool and raises OnCellSave so
                                // the host persists + marks dirty (VB6 AfterEdit).
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
                                    builder.AddAttribute(135, "OnMouseDown",
                                        EventCallback.Factory.Create<MouseEventArgs>(this,
                                            e => HandleCheckboxMouseDown(cbItem, resolvedRowIdx, capturedColIdx, e)));
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
                                RenderDisplayCellContent(builder, 140, item, col, isActiveCell);
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
                        foreach (var col in GetRenderVisibleColumns())
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

    // ── Render Data Cells (flat mode) ────────────────────────────────────

    private RenderFragment RenderDataCells(TValue item, int rowIndex, Func<GridColumn, GridColumn> transform) => builder =>
    {
        var resolvedRowIndex = ResolveRowIndex(item, rowIndex);
        var visibleColumns = GetRenderVisibleColumns();
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
            var isPointerFillCell = IsPointerFillCell(resolvedRowIndex, colIdx);
            var showsEditableCue = CanShowEditableCellCue(capturedCol);
            var editableClass = showsEditableCue ? " fx-cell-editable" : string.Empty;
            var activeClass = isActiveCell
                ? showsEditableCue ? " fx-cell-active fx-cell-active-editable" : " fx-cell-active"
                : string.Empty;
            var typeAheadClass = isTypeAheadPreview ? " fx-typeahead-preview-cell" : string.Empty;
            var rowSelectionEditTargetClass = ShouldShowRowSelectionEditColumnCue(item, capturedCol)
                ? " fx-row-selection-edit-target"
                : string.Empty;
            var cellClass = "fx-cell"
                + (isCellSelected ? " fx-cell-selected" : string.Empty)
                + (isPointerFillCell ? " fx-cell-pointer-selected" : string.Empty)
                + (isBatchEditing ? " fx-batch-editing" : string.Empty)
                + (isBatchDropdownEditing ? " fx-batch-dropdown-editing" : string.Empty)
                + activeClass + editableClass + typeAheadClass + rowSelectionEditTargetClass;
            builder.AddAttribute(1, "class", cellClass);
            // data-field CSS hook — see same comment on the row-render path above.
            if (!string.IsNullOrEmpty(capturedCol.Field))
                builder.AddAttribute(7, "data-field", capturedCol.Field);
            builder.AddAttribute(2, "style", col.GetCellStyle());
            builder.AddAttribute(8, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance, e => HandleCellMouseDown(item, resolvedRowIndex, capturedColIdx, e)));
            builder.AddEventStopPropagationAttribute(9, "onmousedown", true);
            builder.AddAttribute(12, "oncontextmenu", EventCallback.Factory.Create<MouseEventArgs>(this, e => HandleCellContextMenu(item, resolvedRowIndex, capturedColIdx, e)));
            if (EnableCellContextMenu)
            {
                builder.AddEventPreventDefaultAttribute(16, "oncontextmenu", true);
                builder.AddEventStopPropagationAttribute(17, "oncontextmenu", true);
            }
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
                    RenderDisplayCellContent(builder, 30, item, col, isActiveCell);
                    builder.CloseElement();
                    colIdx++;
                    continue;
                }

                // VB6 flexDTBoolean cell: togglable when editable, disabled when
                // read-only. Toggle writes the bound bool + raises OnCellSave.
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
                    builder.AddAttribute(35, "OnMouseDown",
                        EventCallback.Factory.Create<MouseEventArgs>(this,
                            e => HandleCheckboxMouseDown(cbItem, resolvedRowIndex, capturedColIdx, e)));
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
                RenderDisplayCellContent(builder, 40, item, col, isActiveCell);
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

    // ══════════════════════════════════════════════════════════════════════
    // ── COLUMN RESIZE ────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════

    private async Task StartResize(GridColumn col, MouseEventArgs e)
    {
        var now = DateTime.UtcNow;
        var isDoubleClick = AllowColumnAutoFit
            && _lastGripDownField != null
            && string.Equals(_lastGripDownField, col.Field, StringComparison.Ordinal)
            && (now - _lastGripDownAt).TotalMilliseconds <= GripDoubleClickMs;
        _lastGripDownField = col.Field;
        _lastGripDownAt = now;

        if (isDoubleClick)
        {
            _lastGripDownField = null;
            _resizingCol = null;
            await UnregisterGridResizeCaptureAsync();
            await AutoFitCoreAsync(new List<GridColumn> { col });
            return;
        }

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

        await RegisterGridResizeCaptureAsync(e.ClientX, e.ClientY);
    }

    private async Task HandleResizeMove(MouseEventArgs e)
        => await HandleResizeMove(e.ClientX);

    private async Task HandleResizeMove(double clientX)
    {
        if (_resizingCol == null) return;

        var delta = clientX - _resizeStartX;
        var newWidth = Math.Max(40, _resizeStartWidth + delta);

        // A deliberate drag is not half of a double-click.
        if (Math.Abs(delta) > GripJitterPx)
            _lastGripDownField = null;

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
        => await EndResize();

    private async Task EndResize()
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
        await UnregisterGridResizeCaptureAsync();
    }

    private async Task StartRowResize(TValue item, int resolvedRowIndex, MouseEventArgs e)
    {
        if (!AllowRowResizing)
            return;

        _resizingRowItem = item;
        _resizingRowIndex = resolvedRowIndex;
        _rowResizeStartY = e.ClientY;
        _rowResizeStartHeight = GetEffectiveRowHeight(item, resolvedRowIndex)
            ?? Math.Max(MinRowHeight, RowHeight > 0 ? RowHeight : 24);
        await RegisterGridResizeCaptureAsync(e.ClientX, e.ClientY);
    }

    private async Task HandleGridResizeMove(MouseEventArgs e)
    {
        if (_resizingCol != null)
        {
            await HandleResizeMove(e.ClientX);
            return;
        }

        if (_resizingRowIndex >= 0)
            await HandleRowResizeMove(e.ClientY);
    }

    private async Task EndGridResize(MouseEventArgs e)
    {
        if (_resizingCol != null)
        {
            await EndResize();
            return;
        }

        if (_resizingRowIndex >= 0)
            await EndRowResize();
    }

    private async Task HandleRowResizeMove(MouseEventArgs e)
        => await HandleRowResizeMove(e.ClientY);

    private async Task HandleRowResizeMove(double clientY)
    {
        if (_resizingRowIndex < 0)
            return;

        var delta = clientY - _rowResizeStartY;
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
        => await EndRowResize();

    private async Task EndRowResize()
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
        _rowResizeStartY = 0;
        _rowResizeStartHeight = 0;
        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
        await UnregisterGridResizeCaptureAsync();
    }

    private async Task RegisterGridResizeCaptureAsync(double clientX, double clientY)
    {
        if (_gridResizeCaptureRegistered)
            return;

        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", GridJsModulePath);
            _gridDotNetRef ??= DotNetObjectReference.Create(this);
            await _gridJsModule.InvokeVoidAsync("registerGridResizeCapture", _gridHostElement, _gridDotNetRef, clientX, clientY);
            _gridResizeCaptureRegistered = true;
        }
        catch (Exception)
        {
            // The Blazor overlay still captures ordinary in-grid resize movement.
        }
    }

    private async Task UnregisterGridResizeCaptureAsync()
    {
        if (!_gridResizeCaptureRegistered || _gridJsModule == null)
            return;

        try
        {
            await _gridJsModule.InvokeVoidAsync("unregisterGridResizeCapture", _gridHostElement);
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }
        finally
        {
            _gridResizeCaptureRegistered = false;
        }
    }

    [JSInvokable]
    public async Task ContinueGridResizeFromBrowserAsync(double clientX, double clientY)
    {
        if (_resizingCol != null)
            await HandleResizeMove(clientX);
        else if (_resizingRowIndex >= 0)
            await HandleRowResizeMove(clientY);
    }

    [JSInvokable]
    public async Task EndGridResizeFromBrowserAsync(double clientX, double clientY)
    {
        if (_resizingCol != null)
        {
            await HandleResizeMove(clientX);
            await EndResize();
        }
        else if (_resizingRowIndex >= 0)
        {
            await HandleRowResizeMove(clientY);
            await EndRowResize();
        }
    }

    // ── Render Helpers ───────────────────────────────────────────────────

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
                    var inputType = GetEditorInputType(col);

                    builder.OpenElement(50, "input");
                    builder.AddAttribute(51, "type", inputType);
                    builder.AddAttribute(52, "class", "fx-edit-input");
                    builder.AddAttribute(53, "value", GetPropertyValue(_editItem, col.Field));
                    builder.AddAttribute(55, "style", GetEditorInputStyle(col));
                    if (col.Type == ColumnType.Number && !col.ShowNumericSpinner)
                        builder.AddAttribute(56, "inputmode", "decimal");
                    if (col.MaxLength.HasValue && col.MaxLength.Value > 0)
                        builder.AddAttribute(57, "maxlength", col.MaxLength.Value);
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
        if (col.Type == ColumnType.Password)
            return "password";

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
        return "width:100%;min-width:0;max-width:100%;";
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

    // Runs at the TOP of every render pass (see the @{ } block opening the
    // markup). Widths used to be assigned only in OnAfterRender, so the first
    // wire batch shipped width-less columns at 0px and the width arrived in a
    // SECOND batch — on a slow link the last column visibly materialized
    // seconds after the rest of the header (HHM-756).
    private void EnsureAutoColumnWidthsBeforeRender()
    {
        if (!_autoWidthPending)
            return;
        _autoWidthPending = false;
        EnsureAutoColumnWidths();
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
            var text = GetCellDisplayValue(item, col);
            if (string.IsNullOrEmpty(text))
                continue;
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
        var rawVal = ResolveCellValue(item, col);
        var val = ResolveCellDisplayValue(item, col, rawVal);
        if (val == null) return "";

        if (string.IsNullOrWhiteSpace(col.DisplayField)
            && TryGetEditOptionDisplayValue(col, val, out var optionText))
            return optionText;

        if (!string.IsNullOrEmpty(col.Format))
        {
            if (val is IFormattable formattable)
                return formattable.ToString(col.Format, CultureInfo.CurrentCulture);
        }

        if (col.Type == ColumnType.Number)
            return FormatPlainNumber(val);

        if (col.Type == ColumnType.Password)
            return new string('*', val.ToString()?.Length ?? 0);

        return val.ToString() ?? "";
    }

    private string GetColumnSearchText(TValue item, GridColumn col)
    {
        var rawText = ResolveCellValue(item, col)?.ToString() ?? "";
        var displayText = GetCellDisplayValue(item, col);
        return CombineFilterSearchText(rawText, displayText);
    }

    private object? GetCellExportValue(object? item, GridColumn col)
    {
        var val = ResolveCellValue(item, col);
        if (val == null)
            return null;

        if (col.Type == ColumnType.CheckBox)
            return val is bool boolValue ? (boolValue ? 1 : 0) : val;

        return GetCellDisplayValue(item, col);
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

    private object? ResolveCellDisplayValue(object? item, GridColumn col, object? rawValue)
    {
        if (!string.IsNullOrWhiteSpace(col.DisplayField))
        {
            var displayValue = GetPropertyValue(item, col.DisplayField);
            if (!IsEmptyDisplayValue(displayValue))
                return displayValue;
        }

        return rawValue;
    }

    private static bool IsEmptyDisplayValue(object? value)
    {
        if (value is null or DBNull)
            return true;

        return value is string text && string.IsNullOrWhiteSpace(text);
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

        // Dictionary-shaped rows (e.g. FDBGrid's GridRow which inherits
        // Dictionary<string, object?>) carry their data in the dictionary
        // entries, not in CLR properties. CopyProperties would only see
        // Dictionary's readonly meta-properties (Count / Keys / Values /
        // Comparer) and copy nothing → the edit overlay would render
        // blank. Detect both nullable and non-nullable dictionary shapes
        // and clone entry-wise. Falls back to property-copy for the
        // typical case (concrete row types with normal properties).
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
            // Skip indexer properties (e.g. FDBGrid's `GridRow.this[string]`).
            // GetProperties returns them alongside plain properties, but
            // GetValue/SetValue on an indexer requires the index arguments —
            // calling them without args throws TargetParameterCountException.
            // The grid never edits indexed values via this clone path; the
            // dictionary backing the indexer is its own property and gets
            // copied separately if also exposed (or by ref).
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }

    // ── Public API Methods (SyncFusion equivalent) ───────────────────────
    public int TotalPages => _pageState.TotalPages;
    public int CurrentPage => _pageState.CurrentPage;

    public Task GoToPageAsync(int page) => GoToPage(page);

    public IEnumerable<TValue> GetSelectedRecords() => GetSelectedRecordList();

    public Task<List<TValue>> GetSelectedRecordsAsync() =>
        Task.FromResult(GetSelectedRecordList());

    public IEnumerable<TValue> GetSelectedRecordsForColumn(string? field) =>
        GetSelectedRecordList(field);

    public Task<List<TValue>> GetSelectedRecordsForColumnAsync(string? field) =>
        Task.FromResult(GetSelectedRecordList(field));

    public Task<List<(int RowIndex, int CellIndex)>> GetSelectedRowCellIndexesAsync() =>
        Task.FromResult(_selectedCells.ToList());

    private List<TValue> GetSelectedRecordList(string? field = null)
    {
        if (SelectionSettingsRef?.Mode != SelectionMode.Cell || _selectedCells.Count == 0)
            return _selectedItems.ToList();

        var columnIndex = ResolveVisibleColumnIndex(field);
        if (!string.IsNullOrWhiteSpace(field) && columnIndex < 0)
            return new List<TValue>();

        var records = new List<TValue>();
        var seen = new HashSet<TValue>();
        foreach (var cell in _selectedCells
                     .Where(c => columnIndex < 0 || c.CellIndex == columnIndex)
                     .OrderBy(c => c.RowIndex)
                     .ThenBy(c => c.CellIndex))
        {
            var item = GetItemAtResolvedRowIndex(cell.RowIndex);
            if (item != null && seen.Add(item))
                records.Add(item);
        }

        return records;
    }

    public int? GetCurrentRowIndex() => _lastSelectedRowIndex;

    public TValue? GetCurrentRecord()
    {
        if (_activeCell.HasValue)
        {
            var item = GetItemAtResolvedRowIndex(_activeCell.Value.RowIndex);
            if (item != null)
                return item;
        }

        if (_lastSelectedItem != null)
            return _lastSelectedItem;

        if (_lastSelectedRowIndex.HasValue)
        {
            var item = GetItemAtResolvedRowIndex(_lastSelectedRowIndex.Value);
            if (item != null)
                return item;
        }

        return _selectedItems.LastOrDefault();
    }

    public string GetCurrentColumnField()
    {
        var columns = VisibleColumns.ToList();

        if (_activeCell.HasValue
            && _activeCell.Value.CellIndex >= 0
            && _activeCell.Value.CellIndex < columns.Count)
        {
            return columns[_activeCell.Value.CellIndex].Field ?? string.Empty;
        }

        if (_lastSelectedCell.HasValue
            && _lastSelectedCell.Value.CellIndex >= 0
            && _lastSelectedCell.Value.CellIndex < columns.Count)
        {
            return columns[_lastSelectedCell.Value.CellIndex].Field ?? string.Empty;
        }

        return string.Empty;
    }

    public void ClearSelection()
    {
        _selectedItems.Clear();
        _selectedCells.Clear();
        _pointerFillCell = null;
        _cellDragAnchor = null;
        _isCellDragSelecting = false;
        ResetRowSelectionTypeAheadTarget();
        _ = InvokeAsync(StateHasChanged);
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

    /// <summary>Clears the active-cell / last-selected-cell cues without touching row or
    /// cell selections. VB6 flexgrids move the current cell during SaveData walks, so a
    /// post-save highlight lingering on the edited column has no legacy equivalent —
    /// hosts call this after a save to drop the cue (HHM-420).</summary>
    public async Task ClearActiveCellAsync()
    {
        _activeCell = null;
        _lastSelectedCell = null;
        await InvokeAsync(StateHasChanged);
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

    /// <summary>
    /// Fires <see cref="GridControlEvents{TValue}.SelectionChanged"/> with
    /// the current selected-row count. Centralizes the "selection set
    /// changed" notification so consumers can update count labels / status
    /// bars in real time even during drag-select (where per-row events are
    /// intentionally suppressed). Call after every mutation of
    /// <c>_selectedItems</c>; the event is no-op when no consumer subscribes.
    /// </summary>
    private async Task NotifySelectionChangedAsync(GridSelectionChangeSource source = GridSelectionChangeSource.Unknown)
    {
        if (_selectedItems.Count <= 1)
            ResetRowSelectionTypeAheadTarget();

        if (_selectedItems.Count <= 1 && _typeAheadBuffer.Length > 0)
        {
            _typeAheadBuffer = "";
            await NotifyTypeAheadChangedAsync();
        }

        var count = SelectionSettingsRef?.Mode == SelectionMode.Cell
            ? _selectedCells.Count
            : _selectedItems.Count;

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

        // Make it the navigation origin too — arrow keys start from _activeCell.
        _activeCell = cell;
        _lastSelectedCell = cell;

        return InvokeAsync(StateHasChanged);
    }

    public Task SelectCellsAsync(IEnumerable<(int RowIndex, int CellIndex)> cells)
    {
        _selectedCells.Clear();
        _selectedCells.UnionWith(cells);
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

    // ══════════════════════════════════════════════════════════════════════
    // ── BEST FIT (column auto-size) ──────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Sizes every visible column to its content.</summary>
    public Task AutoFitColumnsAsync()
        => AutoFitCoreAsync(_columnsContainer?.Columns.Where(IsColumnVisible).ToList());

    /// <summary>Sizes one column to its content.</summary>
    public Task AutoFitColumnAsync(string field)
    {
        var col = _columnsContainer?.Columns.FirstOrDefault(
            c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        return col == null ? Task.CompletedTask : AutoFitCoreAsync(new List<GridColumn> { col });
    }

    /// <summary>Double-click on the resize grip. The two mousedowns that precede a
    /// double-click each opened a drag, so the capture is torn down first — silently,
    /// since neither one moved the column and re-persisting them would be noise.</summary>
    private async Task HandleResizeHandleDoubleClickAsync(GridColumn col)
    {
        if (!AllowColumnAutoFit)
            return;

        _resizingCol = null;
        await UnregisterGridResizeCaptureAsync();
        await AutoFitCoreAsync(new List<GridColumn> { col });
    }

    /// <summary>Applies best-fit widths and raises the resize / layout events ONCE for
    /// the whole batch, so a host's OnLayoutChanged persistence keeps working unchanged.</summary>
    private async Task AutoFitCoreAsync(List<GridColumn>? columns)
    {
        if (columns == null || columns.Count == 0)
            return;

        var targets = columns.Where(c => c != null && IsColumnVisible(c)).ToList();
        if (targets.Count == 0)
            return;

        var sample = BuildAutoFitSample();
        var measured = await MeasureColumnContentWidthsAsync(targets);

        var changes = new List<(GridColumn Col, double Old, double New)>();
        foreach (var col in targets)
        {
            var content = measured != null
                          && col.Field != null
                          && measured.TryGetValue(col.Field, out var px)
                          && px > 0
                ? px
                : EstimateColumnWidth(col, sample);

            var (min, max) = ResolveAutoFitBounds(col);
            var width = Math.Round(Math.Clamp(content, min, max));

            var old = GetColumnWidthPx(col);
            if (Math.Abs(old - width) < 0.5)
                continue;

            changes.Add((col, old, width));
        }

        if (changes.Count == 0)
            return;

        foreach (var (col, old, width) in changes)
        {
            if (EventsRef?.ColumnResizing.HasDelegate == true)
            {
                var resizing = new ResizeEventArgs { Field = col.Field, OldWidth = old, NewWidth = width };
                await EventsRef.ColumnResizing.InvokeAsync(resizing);
                if (resizing.Cancel)
                    continue;
            }
            col.RuntimeWidth = width;

            if (EventsRef?.ColumnResized.HasDelegate == true)
            {
                await EventsRef.ColumnResized.InvokeAsync(
                    new ResizeEventArgs { Field = col.Field, OldWidth = old, NewWidth = width });
            }
        }

        await SaveGridSettingsAsync();
        await FireLayoutChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>A column's own MinWidth/MaxWidth beat the grid-wide defaults; px only,
    /// since a percentage cannot be compared against a measured pixel width.</summary>
    private (double Min, double Max) ResolveAutoFitBounds(GridColumn col)
    {
        var min = TryParseWidthPx(col.MinWidth) ?? AutoFitMinWidth;
        var max = TryParseWidthPx(col.MaxWidth)
                  ?? (AutoFitMaxWidth > 0 ? AutoFitMaxWidth : _autoFitCeilingPx);
        if (max < min) max = min;
        return (min, max);
    }

    /// <summary>Rows to estimate from when the DOM measurement is unavailable — taken
    /// from the rendered view (sorted, filtered, current page), not the raw DataSource.</summary>
    private List<TValue> BuildAutoFitSample()
    {
        var take = Math.Max(1, AutoFitSampleSize);
        try
        {
            return PagedData.Take(take).ToList();
        }
        catch (Exception)
        {
            return (DataSource?.Take(take).ToList()) ?? new List<TValue>();
        }
    }

    private double _autoFitCeilingPx = 2000;
    private int _autoFocusFirstCellAttempts;

    private async Task<Dictionary<string, double>?> MeasureColumnContentWidthsAsync(List<GridColumn> columns)
    {
        if (!AutoFitUseDomMeasurement)
            return null;

        var fields = columns
            .Select(c => c.Field)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (fields.Length == 0)
            return null;

        try
        {
            _gridJsModule ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", GridJsModulePath);
            var measured = await _gridJsModule.InvokeAsync<Dictionary<string, double>?>(
                "measureColumnContentWidths", _gridHostElement, fields, Math.Max(1, AutoFitSampleSize));
            if (measured != null && measured.TryGetValue("__fxContainerWidth", out var containerPx))
            {
                measured.Remove("__fxContainerWidth");
                if (containerPx > 80) _autoFitCeilingPx = containerPx;
            }
            return measured;
        }
        catch (Exception)
        {
            // Prerendering, a disposed circuit, or a grid that isn't laid out yet.
            // EstimateColumnWidth covers every one of those.
            return null;
        }
    }

    private string ResolveExportTitle(string fallback)
    {
        if (!string.IsNullOrWhiteSpace(Title))
            return Title.Trim();

        if (!string.IsNullOrWhiteSpace(Data))
            return Data.Trim();

        if (!string.IsNullOrWhiteSpace(Id))
            return Id.Trim();

        return string.IsNullOrWhiteSpace(fallback) ? "Export" : fallback.Trim();
    }

    /// <summary>Export all filtered/sorted grid data in the requested format and prompt for a save location when supported.</summary>
    public async Task ExportAsync(
        GridExportFormat format,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        bool showCompletionStatus = true)
    {
        var table = BuildExportTable(ResolveExportTitle(title));
        var result = GridExporter.Export(table, format, fileName, pdfOptions);
        var saveResult = await GridExporter.SaveAsync(JsRuntime, result);
        if (showCompletionStatus)
            await ShowExportResultAsync(table.Rows.Count, saveResult);
    }

    private async Task ShowExportResultAsync(int rowCount, string? result)
    {
        if (string.Equals(result, "cancelled", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(result, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            await ShowExportStatusAsync("Export could not open the file save dialog.");
            return;
        }

        await ShowExportCompletedAsync(rowCount);
    }

    private async Task ShowExportCompletedAsync(int rowCount)
    {
        await ShowExportStatusAsync(FormatExportCompletedMessage(rowCount), delayBeforeShow: 150);
    }

    private async Task ShowExportStatusAsync(string message, int delayBeforeShow = 0)
    {
        if (delayBeforeShow > 0)
            await Task.Delay(delayBeforeShow);

        var generation = ++_exportStatusGeneration;
        _exportStatusMessage = message;
        await InvokeAsync(StateHasChanged);
        _ = ClearExportStatusAfterDelayAsync(generation);
    }

    private static string FormatExportCompletedMessage(int rowCount)
    {
        var formattedCount = rowCount.ToString("N0", CultureInfo.CurrentCulture);
        return rowCount == 1
            ? "1 row exported successfully."
            : $"{formattedCount} rows exported successfully.";
    }

    private async Task ClearExportStatusAfterDelayAsync(int generation)
    {
        try
        {
            await Task.Delay(2500);
            if (generation != _exportStatusGeneration)
                return;

            _exportStatusMessage = null;
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            // Transient feedback should never affect the export operation.
        }
    }

    /// <summary>Create export bytes for all filtered/sorted grid data without triggering a browser download.</summary>
    public GridExportResult CreateExport(
        GridExportFormat format,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null)
    {
        var table = BuildExportTable(ResolveExportTitle(title));
        return GridExporter.Export(table, format, fileName, pdfOptions);
    }

    /// <summary>Export grid data to CSV and trigger browser download.</summary>
    public Task ExportToCsvAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Csv, fileName);

    /// <summary>Export grid data to tab-delimited text and trigger browser download.</summary>
    public Task ExportToTsvAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Tsv, fileName);

    /// <summary>Export grid data to a real Excel workbook (.xlsx) and trigger browser download.</summary>
    public Task ExportToXlsxAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Xlsx, fileName);

    /// <summary>Compatibility alias for existing callers that expect Excel-compatible .xls output.</summary>
    public Task ExportToExcelAsync(string? fileName = null) =>
        ExportToXlsAsync(fileName);

    /// <summary>Export grid data to Excel-compatible HTML (.xls) and trigger browser download.</summary>
    public Task ExportToXlsAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Xls, fileName);

    /// <summary>Export grid data to HTML and trigger browser download.</summary>
    public Task ExportToHtmlAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Html, fileName);

    /// <summary>Export grid data to JSON and trigger browser download.</summary>
    public Task ExportToJsonAsync(string? fileName = null) =>
        ExportAsync(GridExportFormat.Json, fileName);

    /// <summary>Export grid data to a PDF file and trigger browser download.</summary>
    public Task ExportToPdfAsync(string title = "Export") =>
        ExportAsync(GridExportFormat.Pdf, null, title);

    /// <summary>Export grid data to a PDF file with an explicit file name and title.</summary>
    public Task ExportToPdfAsync(string fileName, string title) =>
        ExportAsync(GridExportFormat.Pdf, fileName, title);

    /// <summary>Export grid data to a PDF file with print-layout options.</summary>
    public Task ExportToPdfAsync(
        string fileName,
        string title,
        GridPdfPrintOptions? pdfOptions,
        bool showCompletionStatus = true) =>
        ExportAsync(GridExportFormat.Pdf, fileName, title, pdfOptions, showCompletionStatus);

    /// <summary>Export grid data to a PDF file with an explicit file name and title.</summary>
    public Task ExportToPdfFileAsync(string fileName = "export.pdf", string title = "Export") =>
        ExportAsync(GridExportFormat.Pdf, fileName, title);

    private GridExportTable BuildExportTable(string title)
    {
        if (_pivotMode && _pivotControlRef != null)
        {
            var pivotTable = _pivotControlRef.CreateExportTable(title);
            if (pivotTable.Columns.Count > 0 || pivotTable.Rows.Count > 0)
                return pivotTable;
        }

        return BuildGridExportTable(title);
    }

    private GridExportTable BuildGridExportTable(string title)
    {
        var table = new GridExportTable
        {
            Title = title,
            SheetName = title
        };

        var cols = VisibleColumns.ToList();
        foreach (var col in cols)
        {
            var width = GetColumnWidthPx(col);
            table.Columns.Add(new GridExportColumn(
                col.DisplayHeader,
                format: col.Format,
                textAlign: col.TextAlign,
                width: width > 0 ? width : null));
        }

        // Export all filtered+sorted data, not just the current page.
        var items = SortedData.ToList();
        foreach (var item in items)
            table.Rows.Add(new GridExportRow(cols.Select(col => GetCellExportValue(item, col))));

        AddExportTotalRows(table, cols, items);
        return table;
    }

    private void AddExportTotalRows(GridExportTable table, IReadOnlyList<GridColumn> cols, IReadOnlyList<TValue> items)
    {
        if (!IncludeTotalsInExport || AggregateRows is not { Count: > 0 } || items.Count == 0)
            return;

        var footerRows = AggregateRows.Where(row => row.ShowInFooter).ToList();
        if (footerRows.Count == 0)
            return;

        var footerAggs = ComputeAggregates(items);
        foreach (var aggRow in footerRows)
        {
            var values = new object?[cols.Count];
            for (var colIndex = 0; colIndex < cols.Count; colIndex++)
            {
                var col = cols[colIndex];
                var aggCol = aggRow.Columns.FirstOrDefault(a => string.Equals(a.Field, col.Field, StringComparison.OrdinalIgnoreCase));
                if (aggCol == null)
                    continue;

                var key = $"{aggCol.Field}_{aggCol.Type}";
                footerAggs.TryGetValue(key, out var val);
                values[colIndex] = FormatAggregateValue(aggCol, val);
            }

            table.Rows.Add(new GridExportRow(values, isBold: true));
        }
    }

    private async Task EnsureTrailingNewRowIfNeededAsync()
    {
        if (!EnsureTrailingNewRow
            || _ensuringTrailingNewRow
            || EditSettingsRef?.AllowAdding != true
            || DataSource is not IList<TValue> list
            || Columns.Count == 0)
            return;

        if (_deferredTrailingNewRowEnsureRequested
            && _batchEditItem != null
            && !string.IsNullOrWhiteSpace(_batchEditField))
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
                        PreserveRowWindowOnNextListChange(marker);
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
            PreserveRowWindowOnNextListChange(row);
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

    /// <summary>
    /// Bring a row into view AND open one of its cells in edit mode — the
    /// programmatic equivalent of a single click on that cell. Used by hosts
    /// after adding a "New" row so the new (often off-screen, bottom-of-list)
    /// row scrolls into view with the cursor already in the given column.
    ///
    /// Pure Blazor: the row is paged to if needed, the cell enters batch edit
    /// via the same path as a mouse click, and the post-render focus uses
    /// <c>FocusAsync(preventScroll:false)</c> so the browser scrolls it into
    /// view natively — NO JavaScript scrollIntoView. The sticky column header
    /// (CSS) stays visible through the scroll.
    ///
    /// Requires Batch edit mode (EditSettingsRef.Mode = EditMode.Batch) and an
    /// editable, non-primary-key column. Honors the same per-row/column veto as
    /// a click (OnCellEdit Cancel). No-op if the row/field can't be resolved or
    /// the column isn't editable.
    /// </summary>
    public async Task BeginEditCellAsync(TValue row, string field)
    {
        if (row == null || string.IsNullOrEmpty(field)) return;

        var col = VisibleColumns.FirstOrDefault(
            c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        if (col == null || !col.AllowEditing || col.IsPrimaryKey) return;

        var rowIndex = ResolveRowIndex(row, -1);
        if (rowIndex < 0) return;

        // Page the row onto the visible page when paging is on, so the edit
        // cell actually renders (PagedData only emits the current page).
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

        // StartBatchEdit arms the post-render focus; upgrade it to scroll the
        // freshly-shown row into view (a new bottom row is usually off-screen).
        // If StartBatchEdit vetoed (e.g. OnCellEdit Cancel), _batchEditField is
        // null and we must not force a stale focus/scroll.
        if (_pendingBatchEditFocus && _batchEditField == col.Field)
            _pendingBatchEditScrollIntoView = true;

        // Re-baseline the data-source change trackers to the CURRENT DataSource.
        // Programmatic edit typically follows a host "add new row" (DataSource
        // count just grew). Without this, the next OnParametersSet sees a changed
        // signature and runs ClearSelectionIfDataSourceChanged →
        // ClearTransientSelectionState, which would wipe the edit/scroll just
        // armed — a render-order race. Capturing the new signature here makes the
        // begin-edit deterministic regardless of when the host called
        // StateHasChanged relative to adding the row.
        SyncDataSourceChangeTrackers();

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Snapshot the current DataSource into the filter/selection change-trackers
    /// so a pending add/remove the host just made won't be re-detected as a
    /// "data source changed" on the next OnParametersSet (which would clear
    /// transient selection / batch-edit state). Used by BeginEditCellAsync after
    /// a host adds a row and immediately starts editing it.
    /// </summary>
    private void SyncDataSourceChangeTrackers()
    {
        _lastSelectionDataSource = DataSource;
        _lastSelectionDataSourceSignature = ComputeSelectionDataSourceSignature();
        _selectionDataSourceCaptured = true;
        _lastFilterDataSource = DataSource;
        _filterDataSourceCaptured = true;
    }

    /// <summary>
    /// Scroll a cell into view by (column, row) index — pure Blazor, no JS.
    /// Implemented by entering edit on the cell (which focuses its input with
    /// preventScroll:false, scrolling it on screen). For the common "new row"
    /// case prefer <see cref="BeginEditCellAsync"/>, which is field-based and
    /// doesn't depend on the caller knowing the rendered column index.
    /// rowIndex is the index into the underlying data source; columnIndex is
    /// into the VISIBLE columns in display order.
    /// </summary>
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
            if (_gridScrollSyncRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridScrollSync", _gridHostElement);
            }
            if (_scrollbarActivityRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterScrollbarActivity", _gridHostElement);
            }
            if (_gridResizeCaptureRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridResizeCapture", _gridHostElement);
            }
            if (_windowScrollRegistered && _gridJsModule != null)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridWindowScroll", _scrollElement);
            }
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        _headerDragPreviewRegistered = false;
        _rowDragSelectionAutoScrollRegistered = false;
        _gridKeyboardTrapRegistered = false;
        _gridScrollSyncRegistered = false;
        _scrollbarActivityRegistered = false;
        _gridResizeCaptureRegistered = false;
        _windowScrollRegistered = false;

        if (_gridJsModule != null)
        {
            try
            {
                await _gridJsModule.DisposeAsync();
            }
            catch (Exception)
            {
                // Best-effort teardown.
            }
            _gridJsModule = null;
        }

        _gridDotNetRef?.Dispose();
        _gridDotNetRef = null;

        _windowSelfRef?.Dispose();
        _windowSelfRef = null;
        _dragDotNetRef?.Dispose();
        _dragDotNetRef = null;
    }
}
