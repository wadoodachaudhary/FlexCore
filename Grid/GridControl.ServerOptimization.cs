using Microsoft.AspNetCore.Components;

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
    private readonly Dictionary<object, Dictionary<GridColumn, string>> _optimizedDisplayValueCache =
        new(ReferenceEqualityComparer.Instance);
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
        _optimizedDisplayValueCache.Clear();
    }

    private void PrepareBlazorServerOptimizationForRender()
    {
        _optimizedRenderSelectedCellRows = null;
        if (!UseBlazorServerOptimization)
            return;

        EnsureOptimizedDataSourceIdentity();
        _optimizedRenderColumnStyles = new();
        _optimizedRenderEditableCues = new();
    }

    private void EndBlazorServerOptimizationRenderPass()
    {
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
        _optimizedDisplayValueCache.Clear();
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
        _optimizedDisplayValueCache.Clear();
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
        _optimizedDisplayValueCache.Clear();
    }

    private bool IsFlatUntransformedServerView =>
        _groupDescriptors.Count == 0
        && string.IsNullOrEmpty(SearchText)
        && _expressionFilterRoot == null
        && !_columnStates.Values.Any(state => state.SortDirection.HasValue || state.FilterActive)
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

        if (!_optimizedDisplayValueCache.TryGetValue(item, out var rowValues))
        {
            rowValues = new();
            _optimizedDisplayValueCache[item] = rowValues;
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
            _optimizedDisplayValueCache.Remove(item);
    }

    private void InvalidateBlazorServerHostDisplayValues()
    {
        if (UseBlazorServerOptimization)
            _optimizedDisplayValueCache.Clear();
    }
}
