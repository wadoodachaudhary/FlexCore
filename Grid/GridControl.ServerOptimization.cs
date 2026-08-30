using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Selects the grid's navigation and server-render performance strategy.
    /// The default combines immediate client navigation preview with stable
    /// server-side row lookup and render caches.
    /// </summary>
    [Parameter]
    public GridPerformanceMode PerformanceMode { get; set; } =
        GridPerformanceMode.ClientPreviewAndServerOptimized;


    // ── Stable per-cell event callbacks ──────────────────────────────────
    // Blazor's diff keeps an event-handler registration only when the new
    // EventCallback compares equal to the old one. A lambda that captures loop
    // locals allocates a FRESH closure every render, so the diff emitted a
    // SetEventHandler edit for EVERY cell on EVERY render — measured 588,013
    // bytes for one arrow-key move over a ~40x29 window (12 bytes with the cell
    // handlers removed entirely). Reusing ONE delegate instance per
    // (row item, column position) makes those frames compare equal, so they
    // vanish from the diff and only genuinely changed cells are sent.
    //
    // The delegates capture NO positional values: row index and column index are
    // per-render facts (the row index moves under in-place DataSource mutation,
    // the column position under reorder/hide). They read a mutable context box
    // that every render pass rewrites, and SNAPSHOT it synchronously before
    // dispatch — the handlers are async and a later render can rewrite the box
    // while one is parked on an await.
    private sealed class CellHandlerContext
    {
        public TValue Item = default!;
        public int RowIndexHint;
        public int ColIndex;
        public GridColumn Column = default!;
    }

    private sealed class RowCellHandlers
    {
        public CellHandlerContext[] Contexts = [];
        public EventCallback<MouseEventArgs>[] MouseDown = [];
        public EventCallback<MouseEventArgs>[] ContextMenu = [];
        public EventCallback<MouseEventArgs>[] DblClick = [];
        public EventCallback<MouseEventArgs>[] Click = [];
        // Checkbox cells host a CheckBoxControl COMPONENT: a changed callback
        // parameter re-renders that child component, so these churned even
        // harder than the plain attribute handlers.
        public EventCallback<bool>[] CheckToggle = [];
        public EventCallback<MouseEventArgs>[] CheckMouseDown = [];
        public EventCallback<FocusEventArgs>[] CheckFocus = [];
        public EventCallback<KeyboardEventArgs>[] CheckKeyDown = [];
        // "..." lookup/action button inside a display cell.
        public EventCallback<MouseEventArgs>[] ActionClick = [];
    }

    private Dictionary<object, RowCellHandlers>? _cellHandlersCurrent;
    private Dictionary<object, RowCellHandlers>? _cellHandlersPrevious;

    // Boxed value types never hit a reference-keyed cache and would grow it
    // without bound — same bail-out the display-value cache uses.
    private bool CellHandlerCacheEnabled => !typeof(TValue).IsValueType;

    /// <summary>
    /// Per-render-pass generation swap. Rows that left the rendered window are
    /// simply never promoted out of the previous generation and die on the next
    /// swap, so the cache stays bounded to ~2 windows with no eviction policy.
    /// </summary>
    private void BeginCellHandlerRenderPass()
    {
        if (!CellHandlerCacheEnabled)
            return;

        (_cellHandlersPrevious, _cellHandlersCurrent) = (_cellHandlersCurrent, _cellHandlersPrevious);
        _cellHandlersCurrent?.Clear();
    }

    /// <summary>
    /// Cached mouse callbacks for one row, one entry per VISIBLE COLUMN POSITION.
    /// A column reorder needs no invalidation: the position's context box is
    /// rewritten with the current column/index every pass and read at invoke
    /// time. A change in column COUNT rebuilds the row's arrays.
    /// </summary>
    private RowCellHandlers? GetRowCellHandlers(TValue item, int columnCount)
    {
        if (!CellHandlerCacheEnabled || item is null)
            return null;

        _cellHandlersCurrent ??= new Dictionary<object, RowCellHandlers>(ReferenceEqualityComparer.Instance);

        if (_cellHandlersCurrent.TryGetValue(item, out var handlers))
        {
            if (handlers.Contexts.Length == columnCount)
                return handlers;
            _cellHandlersCurrent.Remove(item);
        }
        else if (_cellHandlersPrevious != null
            && _cellHandlersPrevious.TryGetValue(item, out handlers)
            && handlers.Contexts.Length == columnCount)
        {
            _cellHandlersCurrent[item] = handlers;   // still in the window — promote
            return handlers;
        }

        handlers = BuildRowCellHandlers(columnCount);
        _cellHandlersCurrent[item] = handlers;
        return handlers;
    }

    private RowCellHandlers BuildRowCellHandlers(int columnCount)
    {
        var handlers = new RowCellHandlers
        {
            Contexts = new CellHandlerContext[columnCount],
            MouseDown = new EventCallback<MouseEventArgs>[columnCount],
            ContextMenu = new EventCallback<MouseEventArgs>[columnCount],
            DblClick = new EventCallback<MouseEventArgs>[columnCount],
            Click = new EventCallback<MouseEventArgs>[columnCount],
            CheckToggle = new EventCallback<bool>[columnCount],
            CheckMouseDown = new EventCallback<MouseEventArgs>[columnCount],
            CheckFocus = new EventCallback<FocusEventArgs>[columnCount],
            CheckKeyDown = new EventCallback<KeyboardEventArgs>[columnCount],
            ActionClick = new EventCallback<MouseEventArgs>[columnCount],
        };

        var clickReceiver = (object)this;

        for (var i = 0; i < columnCount; i++)
        {
            var ctx = new CellHandlerContext();
            handlers.Contexts[i] = ctx;

            handlers.MouseDown[i] = EventCallback.Factory.Create<MouseEventArgs>(
                NonRenderingEventReceiver.Instance,
                (MouseEventArgs e) =>
                {
                    var item = ctx.Item; var row = ctx.RowIndexHint; var col = ctx.ColIndex;
                    return HandleCellMouseDown(item, row, col, e);
                });

            handlers.ContextMenu[i] = EventCallback.Factory.Create<MouseEventArgs>(
                this,
                (MouseEventArgs e) =>
                {
                    var item = ctx.Item; var row = ctx.RowIndexHint; var col = ctx.ColIndex;
                    HandleCellContextMenu(item, row, col, e);   // void handler
                });

            handlers.DblClick[i] = EventCallback.Factory.Create<MouseEventArgs>(
                this,
                (MouseEventArgs e) =>
                {
                    var item = ctx.Item; var row = ctx.RowIndexHint; var col = ctx.Column;
                    return HandleCellDblClick(item, row, col, e);
                });

            handlers.Click[i] = EventCallback.Factory.Create<MouseEventArgs>(
                clickReceiver,
                (MouseEventArgs e) =>
                {
                    var item = ctx.Item; var row = ctx.RowIndexHint; var col = ctx.ColIndex;
                    return HandleCellClick(item, row, col, e);
                });

            handlers.CheckToggle[i] = EventCallback.Factory.Create<bool>(this,
                (bool v) => { var item = ctx.Item; var col = ctx.Column; return HandleCheckboxToggle(item, col, v); });
            handlers.CheckMouseDown[i] = EventCallback.Factory.Create<MouseEventArgs>(this,
                (MouseEventArgs e) => { var item = ctx.Item; var row = ctx.RowIndexHint; var c = ctx.ColIndex; return HandleCheckboxMouseDown(item, row, c, e); });
            handlers.CheckFocus[i] = EventCallback.Factory.Create<FocusEventArgs>(this,
                (FocusEventArgs _) => { var item = ctx.Item; var row = ctx.RowIndexHint; var c = ctx.ColIndex; return ActivateCheckboxCellAsync(item, row, c, false); });
            handlers.CheckKeyDown[i] = EventCallback.Factory.Create<KeyboardEventArgs>(this,
                (KeyboardEventArgs e) => { var item = ctx.Item; var row = ctx.RowIndexHint; var c = ctx.ColIndex; var col = ctx.Column; return HandleCheckboxKeyDown(item, row, c, col, e); });
            handlers.ActionClick[i] = EventCallback.Factory.Create<MouseEventArgs>(this,
                (MouseEventArgs _) => { var item = ctx.Item; var col = ctx.Column; return HandleEditButtonClick(item, col); });
        }

        return handlers;
    }


    // Row-level twin of the per-cell cache above: the <tr>'s onclick/ondblclick/
    // onmousedown/ondragover/ondrop were also inline lambdas capturing (item,
    // currentIdx), so they re-registered on every render for every rendered row.
    private sealed class RowHandlerContext
    {
        public TValue Item = default!;
        public int RowIndexHint;
    }

    private sealed class RowHandlerSet
    {
        public RowHandlerContext Context = new();
        public EventCallback<MouseEventArgs> Click;
        public EventCallback DblClick;
        public EventCallback<MouseEventArgs> MouseDown;
        public EventCallback<DragEventArgs> DragOver;
        public EventCallback<DragEventArgs> Drop;
    }

    private Dictionary<object, RowHandlerSet>? _rowHandlersCurrent;
    private Dictionary<object, RowHandlerSet>? _rowHandlersPrevious;

    private void BeginRowHandlerRenderPass()
    {
        if (!CellHandlerCacheEnabled)
            return;
        (_rowHandlersPrevious, _rowHandlersCurrent) = (_rowHandlersCurrent, _rowHandlersPrevious);
        _rowHandlersCurrent?.Clear();
    }

    private RowHandlerSet? GetRowHandlers(TValue item, int rowIndexHint)
    {
        if (!CellHandlerCacheEnabled || item is null)
            return null;

        _rowHandlersCurrent ??= new Dictionary<object, RowHandlerSet>(ReferenceEqualityComparer.Instance);

        if (!_rowHandlersCurrent.TryGetValue(item, out var set))
        {
            if (_rowHandlersPrevious != null && _rowHandlersPrevious.TryGetValue(item, out set))
            {
                _rowHandlersCurrent[item] = set;
            }
            else
            {
                set = BuildRowHandlerSet();
                _rowHandlersCurrent[item] = set;
            }
        }

        set.Context.Item = item;
        set.Context.RowIndexHint = rowIndexHint;
        return set;
    }

    private RowHandlerSet BuildRowHandlerSet()
    {
        var set = new RowHandlerSet();
        var ctx = set.Context;

        set.Click = EventCallback.Factory.Create<MouseEventArgs>(this,
            (MouseEventArgs e) => { var i = ctx.Item; var r = ctx.RowIndexHint; return HandleRowClick(i, r, e); });
        set.DblClick = EventCallback.Factory.Create(this,
            () => { var i = ctx.Item; var r = ctx.RowIndexHint; return HandleRowDblClick(i, r); });
        set.MouseDown = EventCallback.Factory.Create<MouseEventArgs>(NonRenderingEventReceiver.Instance,
            (Action<MouseEventArgs>)(e => { var i = ctx.Item; var r = ctx.RowIndexHint; HandleRowMouseDown(i, r, e); }));
        set.DragOver = EventCallback.Factory.Create<DragEventArgs>(this,
            (Action<DragEventArgs>)(e => { var i = ctx.Item; var r = ctx.RowIndexHint; HandleRowReorderDragOver(i, r, e); }));
        set.Drop = EventCallback.Factory.Create<DragEventArgs>(this,
            (DragEventArgs e) => { var i = ctx.Item; var r = ctx.RowIndexHint; return HandleRowReorderDrop(i, r, e); });
        return set;
    }

    // Accessors used by the row template — each refreshes the shared context
    // box (all five read the same values) and returns the cached callback.
    private EventCallback<MouseEventArgs> RowClickHandler(TValue item, int rowIndex)
        => GetRowHandlers(item, rowIndex) is { } s ? s.Click
            : EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs e) => HandleRowClick(item, rowIndex, e));

    private EventCallback RowDblClickHandler(TValue item, int rowIndex)
        => GetRowHandlers(item, rowIndex) is { } s ? s.DblClick
            : EventCallback.Factory.Create(this, () => HandleRowDblClick(item, rowIndex));

    private EventCallback<MouseEventArgs> RowMouseDownHandler(TValue item, int rowIndex)
        => GetRowHandlers(item, rowIndex) is { } s ? s.MouseDown : NonRenderingRowMouseDown(item, rowIndex);

    private EventCallback<DragEventArgs> RowDragOverHandler(TValue item, int rowIndex)
        => GetRowHandlers(item, rowIndex) is { } s ? s.DragOver
            : EventCallback.Factory.Create<DragEventArgs>(this, (Action<DragEventArgs>)(e => HandleRowReorderDragOver(item, rowIndex, e)));

    private EventCallback<DragEventArgs> RowDropHandler(TValue item, int rowIndex)
        => GetRowHandlers(item, rowIndex) is { } s ? s.Drop
            : EventCallback.Factory.Create<DragEventArgs>(this, (DragEventArgs e) => HandleRowReorderDrop(item, rowIndex, e));

    private bool UseBlazorServerOptimization =>
        PerformanceMode is GridPerformanceMode.ServerOptimized
            or GridPerformanceMode.ClientPreviewAndServerOptimized;

    private bool UseClientNavigationPreview =>
        PerformanceMode is GridPerformanceMode.ClientPreview
            or GridPerformanceMode.ClientPreviewAndServerOptimized;

    private IEnumerable<TValue>? _optimizedDataSource;
    private DataSourceSelectionSignature _optimizedDataSignature;
    private bool _optimizedDataCaptured;
    private int _optimizedDataCount = -1;
    private Dictionary<object, int>? _optimizedRowIndexLookup;
    private List<TValue>? _optimizedNavigationRows;
    private HashSet<int>? _optimizedRenderSelectedCellRows;
    // Display strings, bounded to ~2 rendered windows by the same per-render
    // generation swap the cell-handler caches use above: rows that leave the
    // window are never promoted out of the previous generation and die on the
    // next swap. Before this, a single dictionary with no eviction accumulated
    // every row visited by an uninterrupted scroll (~1.5KB/row, per circuit).
    private Dictionary<object, Dictionary<GridColumn, string>> _displayValuesCurrent =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<object, Dictionary<GridColumn, string>> _displayValuesPrevious =
        new(ReferenceEqualityComparer.Instance);

    private void ClearDisplayValueGenerations()
    {
        _displayValuesCurrent.Clear();
        _displayValuesPrevious.Clear();
    }
    private Dictionary<GridColumn, string>? _optimizedRenderColumnStyles;
    private Dictionary<GridColumn, bool>? _optimizedRenderEditableCues;

    private void SyncBlazorServerOptimizationState(DataSourceSelectionSignature signature)
    {
        if (!UseBlazorServerOptimization)
        {
            ClearBlazorServerOptimizationCaches();
            return;
        }

        if (_optimizedDataCaptured
            && ReferenceEquals(_optimizedDataSource, DataSource)
            && _optimizedDataSignature.Equals(signature))
        {
            return;
        }

        _optimizedDataSource = DataSource;
        _optimizedDataSignature = signature;
        _optimizedDataCaptured = true;
        _optimizedDataCount = GetCurrentDataSourceCount();
        _optimizedRowIndexLookup = null;
        _optimizedNavigationRows = null;
        ClearDisplayValueGenerations();
    }

    private void PrepareBlazorServerOptimizationForRender()
    {
        _optimizedRenderSelectedCellRows = null;
        if (!UseBlazorServerOptimization)
            return;

        EnsureOptimizedDataSourceIdentity();
        (_displayValuesPrevious, _displayValuesCurrent) = (_displayValuesCurrent, _displayValuesPrevious);
        _displayValuesCurrent.Clear();
        _optimizedRenderColumnStyles = new();
        _optimizedRenderEditableCues = new();
    }

    private void EndBlazorServerOptimizationRenderPass()
    {
        GridServerOptimizationDiagnostics.DisplayValueCacheRows =
            _displayValuesCurrent.Count + _displayValuesPrevious.Count;
        _optimizedRenderSelectedCellRows = null;
        _optimizedRenderColumnStyles = null;
        _optimizedRenderEditableCues = null;
    }

    private void ClearBlazorServerOptimizationCaches()
    {
        _optimizedDataSource = null;
        _optimizedDataSignature = default;
        _optimizedDataCaptured = false;
        _optimizedDataCount = -1;
        _optimizedRowIndexLookup = null;
        _optimizedNavigationRows = null;
        _optimizedRenderSelectedCellRows = null;
        ClearDisplayValueGenerations();
        _optimizedRenderColumnStyles = null;
        _optimizedRenderEditableCues = null;
    }

    private void InvalidateBlazorServerOptimizationCaches()
    {
        if (!UseBlazorServerOptimization)
            return;

        _optimizedDataSource = DataSource;
        _optimizedDataCount = GetCurrentDataSourceCount();
        _optimizedRowIndexLookup = null;
        _optimizedNavigationRows = null;
        ClearDisplayValueGenerations();
    }

    private int GetCurrentDataSourceCount() => DataSource switch
    {
        ICollection<TValue> collection => collection.Count,
        IReadOnlyCollection<TValue> readOnlyCollection => readOnlyCollection.Count,
        _ => -1
    };

    private void EnsureOptimizedDataSourceIdentity()
    {
        var count = GetCurrentDataSourceCount();
        if (ReferenceEquals(_optimizedDataSource, DataSource)
            && (_optimizedDataCount < 0 || count < 0 || _optimizedDataCount == count))
        {
            return;
        }

        _optimizedDataSource = DataSource;
        _optimizedDataCount = count;
        _optimizedRowIndexLookup = null;
        _optimizedNavigationRows = null;
        ClearDisplayValueGenerations();
    }

    private bool IsFlatUntransformedServerView =>
        _groupDescriptors.Count == 0
        && string.IsNullOrEmpty(SearchText)
        && _expressionFilterRoot == null
        && !_columnStates.Values.Any(state => state.SortDirection.HasValue || state.FilterActive)
        && _simpleColumnFilters.Count == 0
        && _columnAdvancedFilters.Count == 0
        && _columnCheckboxFilters.Count == 0
        && !IsPagingActive;

    private bool TryResolveBlazorServerRowIndex(TValue item, int fallbackIndex, out int rowIndex)
    {
        rowIndex = -1;
        if (!UseBlazorServerOptimization || DataSource is not IList<TValue> list)
            return false;

        GridServerOptimizationDiagnostics.RowIndexRequests++;
        EnsureOptimizedDataSourceIdentity();

        // In the common large FItems/FAssembly view, the rendered absolute
        // index is already the DataSource index. Verify it in O(1) and avoid
        // building or searching any whole-row structure.
        if (IsFlatUntransformedServerView
            && fallbackIndex >= 0
            && fallbackIndex < list.Count
            && EqualityComparer<TValue>.Default.Equals(list[fallbackIndex], item))
        {
            GridServerOptimizationDiagnostics.DirectRowIndexHits++;
            rowIndex = fallbackIndex;
            return true;
        }

        _optimizedRowIndexLookup ??= BuildOptimizedRowIndexLookup(list);
        if (item is not null && _optimizedRowIndexLookup.TryGetValue(item, out rowIndex))
        {
            GridServerOptimizationDiagnostics.PersistentRowIndexHits++;
            return true;
        }

        return false;
    }

    private static Dictionary<object, int> BuildOptimizedRowIndexLookup(IList<TValue> list)
    {
        GridServerOptimizationDiagnostics.PersistentRowIndexBuilds++;
        var lookup = new Dictionary<object, int>(Math.Max(0, list.Count));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is { } item)
                lookup.TryAdd(item, i);
        }

        return lookup;
    }

    private List<TValue>? GetBlazorServerNavigationRows()
    {
        if (!UseBlazorServerOptimization || !IsFlatUntransformedServerView)
            return null;

        GridServerOptimizationDiagnostics.NavigationRowRequests++;
        EnsureOptimizedDataSourceIdentity();

        if (DataSource is List<TValue> list)
        {
            GridServerOptimizationDiagnostics.NavigationRowReuses++;
            return list;
        }

        if (_optimizedNavigationRows != null)
        {
            GridServerOptimizationDiagnostics.NavigationRowReuses++;
            return _optimizedNavigationRows;
        }

        GridServerOptimizationDiagnostics.NavigationRowBuilds++;
        return _optimizedNavigationRows = DataSource?.ToList() ?? new List<TValue>();
    }

    private bool TryGetBlazorServerDisplayIndex(
        IReadOnlyList<TValue> rows,
        TValue item,
        int candidateIndex,
        out int displayIndex)
    {
        displayIndex = -1;
        if (!UseBlazorServerOptimization
            || !IsFlatUntransformedServerView
            || candidateIndex < 0
            || candidateIndex >= rows.Count
            || !EqualityComparer<TValue>.Default.Equals(rows[candidateIndex], item))
        {
            return false;
        }

        displayIndex = candidateIndex;
        return true;
    }

    private bool IsCellSelectionRowFromOptimizedRenderSet(int resolvedRowIndex)
    {
        if (!UseBlazorServerOptimization || !_renderPassActive)
            return false;

        if (_optimizedRenderSelectedCellRows == null)
        {
            GridServerOptimizationDiagnostics.SelectedRowSetBuilds++;
            _optimizedRenderSelectedCellRows = _selectedCells
                .Select(cell => cell.RowIndex)
                .ToHashSet();
        }

        return _optimizedRenderSelectedCellRows.Contains(resolvedRowIndex);
    }

    private IList<TValue>? GetBlazorServerRenderRows()
    {
        if (!UseBlazorServerOptimization || !IsFlatUntransformedServerView)
            return null;

        EnsureOptimizedDataSourceIdentity();
        return DataSource as IList<TValue>;
    }

    private string GetBlazorServerRenderCellStyle(GridColumn column)
    {
        if (!UseBlazorServerOptimization || !_renderPassActive)
        {
            GridServerOptimizationDiagnostics.BaselineColumnStyleBuilds++;
            return column.GetCellStyle();
        }

        _optimizedRenderColumnStyles ??= new();
        if (_optimizedRenderColumnStyles.TryGetValue(column, out var style))
        {
            GridServerOptimizationDiagnostics.ColumnStyleCacheHits++;
            return style;
        }

        GridServerOptimizationDiagnostics.ColumnStyleCacheMisses++;
        style = column.GetCellStyle();
        _optimizedRenderColumnStyles[column] = style;
        return style;
    }

    private bool GetBlazorServerEditableCue(GridColumn column)
    {
        if (!UseBlazorServerOptimization || !_renderPassActive)
        {
            GridServerOptimizationDiagnostics.BaselineEditableCueBuilds++;
            return CanShowEditableCellCue(column);
        }

        _optimizedRenderEditableCues ??= new();
        if (_optimizedRenderEditableCues.TryGetValue(column, out var canShow))
        {
            GridServerOptimizationDiagnostics.EditableCueCacheHits++;
            return canShow;
        }

        GridServerOptimizationDiagnostics.EditableCueCacheMisses++;
        canShow = CanShowEditableCellCue(column);
        _optimizedRenderEditableCues[column] = canShow;
        return canShow;
    }

    private string GetBlazorServerRenderDisplayValue(object? item, GridColumn column)
    {
        if (!UseBlazorServerOptimization)
        {
            GridServerOptimizationDiagnostics.BaselineDisplayValueBuilds++;
            return GetCellDisplayValue(item, column);
        }

        if (item == null
            || item.GetType().IsValueType
            || !string.IsNullOrWhiteSpace(column.Formula)
            || AllowCellFormulas
            || column.EditOptionsProvider != null)
        {
            GridServerOptimizationDiagnostics.DisplayValueCacheMisses++;
            return GetCellDisplayValue(item, column);
        }

        if (!_displayValuesCurrent.TryGetValue(item, out var rowValues))
        {
            if (_displayValuesPrevious.TryGetValue(item, out rowValues))
            {
                _displayValuesCurrent[item] = rowValues;   // still in the window — promote
            }
            else
            {
                rowValues = new();
                _displayValuesCurrent[item] = rowValues;
            }
        }

        if (rowValues.TryGetValue(column, out var cachedValue))
        {
            GridServerOptimizationDiagnostics.DisplayValueCacheHits++;
            return cachedValue;
        }

        GridServerOptimizationDiagnostics.DisplayValueCacheMisses++;
        var value = GetCellDisplayValue(item, column);
        rowValues[column] = value;
        return value;
    }

    private void InvalidateBlazorServerDisplayValue(object? item)
    {
        if (UseBlazorServerOptimization && item != null && !item.GetType().IsValueType)
        {
            // Both generations: a promoted entry is REFERENCED from both until
            // the next swap — removing from one would leave a resurrectable
            // stale entry serving old cell text after an edit.
            _displayValuesCurrent.Remove(item);
            _displayValuesPrevious.Remove(item);
        }
    }

    private void InvalidateBlazorServerHostDisplayValues()
    {
        if (UseBlazorServerOptimization)
            ClearDisplayValueGenerations();
    }
}
