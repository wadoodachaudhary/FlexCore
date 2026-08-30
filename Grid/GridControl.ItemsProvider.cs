using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Optional range provider for large, flat datasets. When null, GridControl
    /// continues to use <see cref="DataSource"/> and its existing local pipeline.
    /// The initial provider mode supports a bounded, fixed-height, ungrouped body;
    /// paging and client-computed aggregate footers are intentionally bypassed.
    /// </summary>
    [Parameter] public GridItemsProvider<TValue>? ItemsProvider { get; set; }

    /// <summary>
    /// Opaque host value included with every provider request. Changing it starts
    /// a new query generation and reloads from the first row.
    /// </summary>
    [Parameter] public object? ItemsProviderContextKey { get; set; }

    /// <summary>
    /// Stable identity used as the rendered row key when provider calls create
    /// new object instances for the same database row.
    /// </summary>
    [Parameter] public Func<TValue, object?>? ItemKeySelector { get; set; }

    /// <summary>
    /// Maximum number of completed provider ranges retained by this grid.
    /// Defaults to four and is always clamped to a small bounded range.
    /// </summary>
    [Parameter] public int ItemsProviderCacheSize { get; set; } = 4;

    /// <summary>True while the current provider request is outstanding.</summary>
    public bool IsItemsProviderLoading { get; private set; }

    /// <summary>
    /// Most recent non-cancellation provider failure. The previous painted range
    /// remains committed when a request fails.
    /// </summary>
    public Exception? ItemsProviderLastError { get; private set; }

    private bool UsesItemsProvider => ItemsProvider is not null;

    private IReadOnlyList<TValue> _providerWindowItems = Array.Empty<TValue>();
    private int _providerWindowStart;
    private int _providerTotalCount = -1;
    private long _providerQueryVersion;
    private long _providerRequestSerial;
    private CancellationTokenSource? _providerLoadCts;
    private GridItemsProvider<TValue>? _observedItemsProvider;
    private object? _observedItemsProviderContextKey;
    private int _observedProviderQuerySignature;
    private bool _providerIdentityCaptured;
    private GridQueryDescriptor _providerQuery = GridQueryDescriptor.Empty;
    private readonly LinkedList<ProviderCacheEntry> _providerCache = new();

    private sealed record ProviderCacheEntry(
        long QueryVersion,
        int StartIndex,
        IReadOnlyList<TValue> Items,
        int TotalCount);

    private sealed record ProviderWindow(
        int StartIndex,
        IReadOnlyList<TValue> Items,
        int TotalCount);

    private enum ProviderScrollLoadStatus : byte
    {
        NoChange,
        Loaded,
        Failed
    }

    private object? GetRowRenderKey(TValue item)
    {
        if (ItemKeySelector is null)
            return item;

        try
        {
            return ItemKeySelector(item) ?? item;
        }
        catch
        {
            // A host key selector must not make the entire body unrenderable.
            return item;
        }
    }

    /// <summary>
    /// Invalidates the current provider generation and reloads its first range.
    /// This method has no effect on DataSource-backed grids.
    /// </summary>
    public Task ReloadItemsAsync()
    {
        if (!UsesItemsProvider)
            return Task.CompletedTask;

        return InvokeAsync(async () =>
        {
            CaptureCurrentProviderIdentity(forceNewGeneration: true);
            await LoadAndCommitProviderWindowAsync(
                startIndex: 0,
                count: Math.Max(1, _winCount),
                includeTotalCount: true,
                resetScrollPosition: true);
        });
    }

    private void ValidateItemsProviderConfiguration()
    {
        if (!UsesItemsProvider)
            return;

        if (DataSource is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(GridControl<TValue>)} cannot use {nameof(DataSource)} and " +
                $"{nameof(ItemsProvider)} at the same time.");
        }

        if (string.IsNullOrWhiteSpace(Height))
        {
            throw new InvalidOperationException(
                $"{nameof(ItemsProvider)} currently requires a bounded {nameof(Height)}.");
        }
    }

    /// <summary>
    /// Called after render because column children and their fields are guaranteed
    /// to be registered at that point. Any query/context change starts a fresh
    /// generation; the old rows stay painted until the first new range arrives.
    /// </summary>
    private async Task EnsureItemsProviderLoadedAsync()
    {
        if (!UsesItemsProvider)
        {
            if (_providerIdentityCaptured)
                ResetItemsProviderState();
            return;
        }

        var changed = CaptureCurrentProviderIdentity(forceNewGeneration: false);
        if (!changed && _providerTotalCount >= 0)
            return;

        await LoadAndCommitProviderWindowAsync(
            startIndex: 0,
            count: Math.Max(1, _winCount),
            includeTotalCount: true,
            resetScrollPosition: changed);
    }

    private bool CaptureCurrentProviderIdentity(bool forceNewGeneration)
    {
        var query = BuildProviderQueryDescriptor();
        var signature = ComputeProviderQuerySignature(query);
        // Razor can recreate an equivalent method-group delegate every parent
        // render. Delegate equality compares method + target and therefore does
        // not turn those harmless allocations into a new query generation.
        var providerChanged = !Equals(_observedItemsProvider, ItemsProvider);
        var contextChanged = !_providerIdentityCaptured
            || !Equals(_observedItemsProviderContextKey, ItemsProviderContextKey);
        var queryChanged = !_providerIdentityCaptured
            || signature != _observedProviderQuerySignature;

        if (!forceNewGeneration && !providerChanged && !contextChanged && !queryChanged)
            return false;

        _providerIdentityCaptured = true;
        _observedItemsProvider = ItemsProvider;
        _observedItemsProviderContextKey = ItemsProviderContextKey;
        _observedProviderQuerySignature = signature;
        _providerQuery = query;
        _providerQueryVersion++;
        // Keep the last committed rows and total painted while the first range
        // for this generation is in flight. They are replaced atomically only
        // after the new request succeeds.
        _providerCache.Clear();
        CancelProviderLoad();
        ItemsProviderLastError = null;
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        return true;
    }

    private GridQueryDescriptor BuildProviderQueryDescriptor()
    {
        var sorts = _columnStates
            .Where(pair => pair.Value.SortDirection.HasValue)
            .Select(pair => new GridSortDescriptor(pair.Key, pair.Value.SortDirection!.Value))
            .ToArray();

        var filters = new List<GridFilterDescriptor>();
        foreach (var pair in _columnStates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var state = pair.Value;
            if (!string.IsNullOrWhiteSpace(state.FilterValue))
            {
                filters.Add(new GridFilterDescriptor(
                    pair.Key,
                    GridProviderFilterKind.Text,
                    state.FilterOperator,
                    state.FilterValue,
                    Array.Empty<string>(),
                    null,
                    null));
            }

            if (state.UseCheckedFilter)
            {
                filters.Add(new GridFilterDescriptor(
                    pair.Key,
                    GridProviderFilterKind.CheckedValues,
                    TextFilterOperator.Equals,
                    null,
                    state.CheckedFilterValues.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    null,
                    null));
            }

            if (state.UseNumericBoundsFilter)
            {
                filters.Add(new GridFilterDescriptor(
                    pair.Key,
                    GridProviderFilterKind.NumericBounds,
                    TextFilterOperator.ChooseOne,
                    null,
                    Array.Empty<string>(),
                    state.NumericFilterMin,
                    state.NumericFilterMax));
            }
        }

        // The lightweight filter row is an additive feature implemented outside
        // ColumnState. Emit it as a normal contains predicate for provider hosts.
        foreach (var pair in _simpleColumnFilters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            filters.Add(new GridFilterDescriptor(
                pair.Key,
                GridProviderFilterKind.Text,
                TextFilterOperator.Contains,
                pair.Value,
                Array.Empty<string>(),
                null,
                null));
        }

        foreach (var pair in _columnCheckboxFilters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            filters.Add(new GridFilterDescriptor(
                pair.Key,
                GridProviderFilterKind.CheckedValues,
                TextFilterOperator.Equals,
                null,
                pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                null,
                null));
        }

        var searchFields = VisibleColumns
            .Select(column => column.Field)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GridQueryDescriptor(sorts, filters, SearchText, searchFields);
    }

    private static int ComputeProviderQuerySignature(GridQueryDescriptor query)
    {
        var hash = new HashCode();
        hash.Add(query.SearchText, StringComparer.Ordinal);
        foreach (var field in query.SearchFields)
            hash.Add(field, StringComparer.OrdinalIgnoreCase);
        foreach (var sort in query.Sorts)
        {
            hash.Add(sort.Field, StringComparer.OrdinalIgnoreCase);
            hash.Add(sort.Direction);
        }
        foreach (var filter in query.Filters)
        {
            hash.Add(filter.Field, StringComparer.OrdinalIgnoreCase);
            hash.Add(filter.Kind);
            hash.Add(filter.Operator);
            hash.Add(filter.Value, StringComparer.Ordinal);
            hash.Add(filter.Minimum);
            hash.Add(filter.Maximum);
            foreach (var value in filter.Values)
                hash.Add(value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private async Task<bool> LoadAndCommitProviderWindowAsync(
        int startIndex,
        int count,
        bool includeTotalCount,
        bool resetScrollPosition)
    {
        var window = await LoadProviderWindowAsync(startIndex, count, includeTotalCount);
        if (window is null)
            return false;

        CommitProviderWindow(window, count);
        if (resetScrollPosition)
            _pendingWindowScrollReset = true;

        ClearPassViewMemos();
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private async Task<ProviderWindow?> LoadProviderWindowAsync(
        int startIndex,
        int count,
        bool includeTotalCount)
    {
        var provider = ItemsProvider;
        if (provider is null)
            return null;

        startIndex = Math.Max(0, startIndex);
        count = Math.Max(1, count);
        var queryVersion = _providerQueryVersion;

        var cached = TryGetCachedProviderWindow(queryVersion, startIndex, count);
        if (cached is not null)
        {
            // A cache hit is still the newest navigation intent. Cancel and
            // invalidate any older in-flight miss so it cannot later replace
            // this already-committed range.
            CancelProviderLoad();
            _providerRequestSerial++;
            IsItemsProviderLoading = false;
            return cached;
        }

        CancelProviderLoad();
        var cts = new CancellationTokenSource();
        _providerLoadCts = cts;
        var serial = ++_providerRequestSerial;
        IsItemsProviderLoading = true;
        ItemsProviderLastError = null;

        try
        {
            var request = new GridItemsRequest(
                startIndex,
                count,
                queryVersion,
                includeTotalCount || _providerTotalCount < 0,
                _providerQuery,
                ItemsProviderContextKey,
                cts.Token);
            var result = await provider(request);

            if (cts.IsCancellationRequested
                || serial != _providerRequestSerial
                || queryVersion != _providerQueryVersion
                || !Equals(provider, ItemsProvider))
            {
                return null;
            }

            var totalCount = Math.Max(0, result.TotalCount);
            var items = (result.Items ?? Array.Empty<TValue>())
                .Take(count)
                .ToArray();
            var entry = new ProviderCacheEntry(queryVersion, startIndex, items, totalCount);
            AddProviderCacheEntry(entry);
            return new ProviderWindow(startIndex, items, totalCount);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (serial == _providerRequestSerial && queryVersion == _providerQueryVersion)
                ItemsProviderLastError = ex;
            return null;
        }
        finally
        {
            if (serial == _providerRequestSerial)
                IsItemsProviderLoading = false;
            if (ReferenceEquals(_providerLoadCts, cts))
                _providerLoadCts = null;
            cts.Dispose();
        }
    }

    private ProviderWindow? TryGetCachedProviderWindow(long queryVersion, int startIndex, int count)
    {
        var requestedEnd = (long)startIndex + count;
        var node = _providerCache.First;
        while (node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            var availableEnd = (long)entry.StartIndex + entry.Items.Count;
            var expectedEnd = Math.Min(requestedEnd, entry.TotalCount);
            if (entry.QueryVersion == queryVersion
                && entry.StartIndex <= startIndex
                && availableEnd >= expectedEnd)
            {
                _providerCache.Remove(node);
                _providerCache.AddFirst(node);
                var offset = startIndex - entry.StartIndex;
                var take = Math.Max(0, Math.Min(count, entry.TotalCount - startIndex));
                var items = entry.Items.Skip(offset).Take(take).ToArray();
                return new ProviderWindow(startIndex, items, entry.TotalCount);
            }
            node = next;
        }
        return null;
    }

    private void AddProviderCacheEntry(ProviderCacheEntry entry)
    {
        _providerCache.AddFirst(entry);
        var limit = Math.Clamp(ItemsProviderCacheSize, 1, 8);
        while (_providerCache.Count > limit)
            _providerCache.RemoveLast();
    }

    private void CommitProviderWindow(ProviderWindow window, int requestedCount)
    {
        _providerTotalCount = window.TotalCount;
        _pageState.TotalRecords = window.TotalCount;
        _providerWindowStart = Math.Min(window.StartIndex, Math.Max(0, window.TotalCount - 1));
        _providerWindowItems = window.Items;
        _winStart = _providerWindowStart;
        _winCount = Math.Max(1, requestedCount);
    }

    private async Task<ProviderScrollLoadStatus> LoadProviderWindowForScrollAsync(
        double scrollTop,
        double clientHeight,
        bool deferredCommit = false,
        int scrollDirection = 0,
        bool forceRecenter = false)
    {
        var oldStart = _winStart;
        var oldCount = _winCount;
        var changed = UpdateGridWindow(
            scrollTop,
            clientHeight,
            deferredCommit,
            scrollDirection,
            forceRecenter);
        var requestedStart = _winStart;
        var requestedCount = _winCount;
        _winStart = oldStart;
        _winCount = oldCount;

        if (!changed)
            return ProviderScrollLoadStatus.NoChange;

        var window = await LoadProviderWindowAsync(
            requestedStart,
            requestedCount,
            includeTotalCount: _providerTotalCount < 0);
        if (window is null)
            return ProviderScrollLoadStatus.Failed;

        // The underlying query may have shrunk between scroll samples. If the
        // requested offset is now past its end, fetch the last valid window
        // instead of committing an empty range at a fabricated index.
        if (window.TotalCount > 0 && requestedStart >= window.TotalCount)
        {
            requestedStart = Math.Max(0, window.TotalCount - requestedCount);
            window = await LoadProviderWindowAsync(
                requestedStart,
                requestedCount,
                includeTotalCount: false);
            if (window is null)
                return ProviderScrollLoadStatus.Failed;
        }

        CommitProviderWindow(window, requestedCount);
        ClearPassViewMemos();
        return ProviderScrollLoadStatus.Loaded;
    }

    private void CancelProviderLoad()
    {
        if (_providerLoadCts is null)
            return;
        try { _providerLoadCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _providerLoadCts = null;
    }

    private void ResetItemsProviderState()
    {
        CancelProviderLoad();
        _providerIdentityCaptured = false;
        _observedItemsProvider = null;
        _observedItemsProviderContextKey = null;
        _observedProviderQuerySignature = 0;
        _providerQuery = GridQueryDescriptor.Empty;
        _providerWindowItems = Array.Empty<TValue>();
        _providerWindowStart = 0;
        _providerTotalCount = -1;
        _providerCache.Clear();
        IsItemsProviderLoading = false;
        ItemsProviderLastError = null;
    }

    private void DisposeItemsProviderState()
    {
        CancelProviderLoad();
        _providerCache.Clear();
    }
}
