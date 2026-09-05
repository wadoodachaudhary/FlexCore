using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Optional range provider for large datasets. When null, GridControl
    /// continues to use <see cref="DataSource"/> and its existing local pipeline.
    /// Flat row windows are the default; provider-executed lazy grouping is an
    /// additive opt-in through <see cref="EnableProviderGrouping"/>.
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

    /// <summary>
    /// Opts the Excel-style checklist menu into distinct-value requests through
    /// <see cref="ItemsProvider"/>. The default is false so existing providers
    /// receive exactly the same request purposes as before. When enabled, the
    /// provider receives <see cref="GridItemsRequestPurpose.FilterValues"/> with
    /// <see cref="GridItemsRequest.FilterField"/> populated.
    /// </summary>
    [Parameter] public bool EnableProviderFilterValueRequests { get; set; }

    /// <summary>Maximum distinct values requested for one checklist menu.</summary>
    [Parameter] public int ProviderFilterValueRequestSize { get; set; } = 1_000;

    /// <summary>True while the current provider request is outstanding.</summary>
    public bool IsItemsProviderLoading { get; private set; }

    /// <summary>
    /// Most recent non-cancellation provider failure. The previous painted range
    /// remains committed when a request fails.
    /// </summary>
    public Exception? ItemsProviderLastError { get; private set; }

    /// <summary>True while an opt-in provider checklist-value request is running.</summary>
    public bool IsProviderFilterValuesLoading { get; private set; }

    /// <summary>Most recent provider checklist-value failure.</summary>
    public Exception? ProviderFilterValuesLastError { get; private set; }

    private bool UsesItemsProvider => ItemsProvider is not null;

    private IReadOnlyList<TValue> _providerWindowItems = Array.Empty<TValue>();
    private int _providerWindowStart;
    private int _providerTotalCount = -1;
    private long _providerQueryVersion;
    private long _providerRequestSerial;
    private CancellationTokenSource? _providerLoadCts;
    private GridItemsProvider<TValue>? _observedItemsProvider;
    private Func<TValue, object?>? _observedItemKeySelector;
    private object? _observedItemsProviderContextKey;
    private int _observedProviderQuerySignature;
    private bool _observedProviderGroupingMode;
    private bool _providerIdentityCaptured;
    private GridQueryDescriptor _providerQuery = GridQueryDescriptor.Empty;
    private readonly LinkedList<ProviderCacheEntry> _providerCache = new();
    private readonly Dictionary<string, IReadOnlyList<FilterValueCandidate>> _providerFilterValueCandidates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _providerFilterValuesHaveMore =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _providerFilterValuesCts;
    private long _providerFilterValuesRequestSerial;

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
            if (UsesProviderGrouping)
            {
                await LoadAndCommitProviderRootGroupsAsync();
                return;
            }
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

        // A flat provider can only fetch an index range, so the two spacer rows
        // must represent exactly one fixed-pitch rendered row per returned item.
        // Local DataSource grids can safely fall back to render-all for variable
        // height features; a provider cannot do that without loading the entire
        // result set, so fail early instead of producing an incorrect scroll
        // extent (and the characteristic white gaps near a window boundary).
        if (!IsProviderGroupingConfiguredForValidation())
        {
            var incompatibleOptions = new List<string>();
            if (RowTemplate is not null)
                incompatibleOptions.Add(nameof(RowTemplate));
            if (DataLayoutMode == GridDataLayoutMode.Stacked)
                incompatibleOptions.Add($"{nameof(DataLayoutMode)}=Stacked");
            if (AdaptiveMode != GridAdaptiveMode.None)
                incompatibleOptions.Add($"{nameof(AdaptiveMode)}={AdaptiveMode}");
            if (HasDetailTemplate)
                incompatibleOptions.Add(nameof(DetailTemplate));
            if (RowHeightSelector is not null)
                incompatibleOptions.Add(nameof(RowHeightSelector));
            if (AllowRowResizing || _runtimeRowHeights.Count > 0)
                incompatibleOptions.Add($"{nameof(AllowRowResizing)} / runtime row heights");
            if (EditSettingsRef is { Mode: EditMode.Inline } inlineEdit
                && (inlineEdit.AllowEditing || inlineEdit.AllowAdding))
            {
                incompatibleOptions.Add("inline row editing/adding");
            }

            if (incompatibleOptions.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Flat {nameof(ItemsProvider)} mode requires one deterministic fixed-height row " +
                    $"per item and cannot be combined with: {string.Join(", ", incompatibleOptions)}. " +
                    $"Remove those options, or use {nameof(DataSource)} so GridControl can safely " +
                    "fall back to rendering all rows. DetailTemplate is also supported with lazy " +
                    $"provider grouping when {nameof(EnableProviderGrouping)}, {nameof(AllowGrouping)}, " +
                    $"and at least one {nameof(GroupColumns)} entry are configured.");
            }
        }
    }

    private bool IsProviderGroupingConfiguredForValidation()
    {
        if (!EnableProviderGrouping || !AllowGrouping)
            return false;

        // Validation runs before SyncGroupDescriptorsFromParameter. When the
        // host replaces GroupColumns, inspect that incoming list; otherwise the
        // live descriptors may include groups created interactively in the UI.
        return ReferenceEquals(GroupColumns, _lastSyncedGroupColumns)
            ? _groupDescriptors.Count > 0
            : GroupColumns?.Any(field => !string.IsNullOrWhiteSpace(field)) == true;
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
        if (UsesProviderGrouping)
        {
            if (!changed && (_providerGroupsLoaded || IsItemsProviderLoading))
                return;

            await LoadAndCommitProviderRootGroupsAsync();
            return;
        }
        if (!changed
            && (_providerTotalCount >= 0
                || IsItemsProviderLoading
                || ItemsProviderLastError is not null))
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
        var itemKeySelectorChanged = !Equals(_observedItemKeySelector, ItemKeySelector);
        var contextChanged = !_providerIdentityCaptured
            || !Equals(_observedItemsProviderContextKey, ItemsProviderContextKey);
        var queryChanged = !_providerIdentityCaptured
            || signature != _observedProviderQuerySignature;
        var groupingModeChanged = _providerIdentityCaptured
            && _observedProviderGroupingMode != UsesProviderGrouping;

        if (!forceNewGeneration
            && !providerChanged
            && !itemKeySelectorChanged
            && !contextChanged
            && !queryChanged
            && !groupingModeChanged)
            return false;

        _providerIdentityCaptured = true;
        _observedItemsProvider = ItemsProvider;
        _observedItemKeySelector = ItemKeySelector;
        _observedItemsProviderContextKey = ItemsProviderContextKey;
        _observedProviderQuerySignature = signature;
        _observedProviderGroupingMode = UsesProviderGrouping;
        _providerQuery = query;
        _providerQueryVersion++;
        // Cell/row selection and edit targets store loaded object references or
        // positional row indexes. A sort/filter/search/context change can put a
        // different database record at the same index, so retaining that state
        // could highlight or edit the wrong row. Clear it at the generation
        // boundary; SetStateAsync restores identities after the new first range
        // is committed when those identities are available in that range.
        ClearTransientSelectionState(clearRows: true);
        if (_isEditing)
            CancelEdit();
        _expandedRows.Clear();
        // Keep the last committed rows and total painted while the first range
        // for this generation is in flight. They are replaced atomically only
        // after the new request succeeds.
        _providerCache.Clear();
        ResetProviderGroupingState();
        CancelProviderLoad();
        CancelProviderFilterValuesLoad();
        CancelProviderExport();
        ItemsProviderLastError = null;
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        return true;
    }

    private GridQueryDescriptor BuildProviderQueryDescriptor()
    {
        var sorts = GetActiveSortDescriptors();

        var filters = new List<GridFilterDescriptor>();
        foreach (var pair in _columnStates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var state = pair.Value;
            var hasFirstCondition = !string.IsNullOrWhiteSpace(state.FilterValue)
                || IsValueOptionalFilterOperator(state.FilterOperator);
            var hasSecondCondition = EnableAdvancedFilterPopup
                && (!string.IsNullOrWhiteSpace(state.SecondFilterValue)
                    || IsValueOptionalFilterOperator(state.SecondFilterOperator));
            var blankFilter = EnableBlankRowFilter
                ? state.BlankRowFilter
                : BlankRowFilterMode.All;
            if (hasFirstCondition || hasSecondCondition || blankFilter != BlankRowFilterMode.All)
            {
                filters.Add(new GridFilterDescriptor(
                    pair.Key,
                    GridProviderFilterKind.Text,
                    state.FilterOperator,
                    state.FilterValue,
                    Array.Empty<string>(),
                    null,
                    null)
                {
                    Source = GridProviderFilterSource.ColumnMenu,
                    SecondOperator = hasSecondCondition ? state.SecondFilterOperator : null,
                    SecondValue = hasSecondCondition ? state.SecondFilterValue : null,
                    LogicalOperator = state.LogicalFilterOperator,
                    BlankRowFilter = blankFilter
                });
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
                    null)
                {
                    Source = GridProviderFilterSource.ColumnMenu
                });
            }

            if (state.UseNumericRangeFilter)
            {
                filters.Add(new GridFilterDescriptor(
                    pair.Key,
                    GridProviderFilterKind.NumericRanges,
                    TextFilterOperator.ChooseOne,
                    null,
                    state.CheckedNumericRangeKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    null,
                    null)
                {
                    Source = GridProviderFilterSource.ColumnMenu,
                    NumericRanges = BuildProviderNumericRanges(pair.Key, state.CheckedNumericRangeKeys)
                });
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
                    state.NumericFilterMax)
                {
                    Source = GridProviderFilterSource.ColumnMenu
                });
            }
        }

        // The typed filter row is additive state outside ColumnState. Emit its
        // selected per-column operator for provider hosts.
        foreach (var pair in _simpleColumnFilters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var column = FindColumnByField(pair.Key);
            var filterOperator = column == null
                ? (_filterRowOperators.TryGetValue(pair.Key, out var configuredOperator)
                    ? configuredOperator
                    : TextFilterOperator.Contains)
                : GetFilterRowOperator(column);
            if (string.IsNullOrWhiteSpace(pair.Value) && !IsValueOptionalFilterOperator(filterOperator))
                continue;
            filters.Add(new GridFilterDescriptor(
                pair.Key,
                GridProviderFilterKind.Text,
                filterOperator,
                pair.Value,
                Array.Empty<string>(),
                null,
                null)
            {
                Source = GridProviderFilterSource.FilterRow
            });
        }

        foreach (var pair in _columnAdvancedFilters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var criteria = pair.Value;
            if (!IsAdvancedFilterCriteriaActive(criteria))
                continue;

            var hasFirstCondition = IsAdvancedFilterConditionActive(criteria.Operator1, criteria.Value1);
            var hasSecondCondition = IsAdvancedFilterConditionActive(criteria.Operator2, criteria.Value2);

            filters.Add(new GridFilterDescriptor(
                pair.Key,
                GridProviderFilterKind.Text,
                MapAdvancedFilterOperator(criteria.Operator1),
                criteria.Value1,
                Array.Empty<string>(),
                null,
                null)
            {
                Source = GridProviderFilterSource.AdvancedColumn,
                AdvancedOperator = criteria.Operator1,
                SecondOperator = hasSecondCondition ? MapAdvancedFilterOperator(criteria.Operator2) : null,
                SecondAdvancedOperator = hasSecondCondition ? criteria.Operator2 : null,
                SecondValue = hasSecondCondition ? criteria.Value2 : null,
                LogicalOperator = criteria.LogicalOperator
            });
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
                null)
            {
                Source = GridProviderFilterSource.ColumnCheckBox
            });
        }

        var searchFields = VisibleColumns
            .Select(column => column.Field)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var groups = _groupDescriptors
            .Select(group => new GridGroupQueryDescriptor(group.Field)
            {
                Direction = GetGroupSortDirection(group.Field)
            })
            .ToArray();
        var aggregates = (AggregateRows ?? Enumerable.Empty<AggregateRow>())
            .SelectMany(row => row.Columns)
            .Where(column => !string.IsNullOrWhiteSpace(column.Field))
            .Select(column => new GridAggregateQueryDescriptor(column.Field, column.Type))
            .Distinct()
            .ToArray();

        return new GridQueryDescriptor(sorts, filters, SearchText, searchFields)
        {
            ExpressionFilterText = _expressionFilterText,
            Groups = groups,
            Aggregates = aggregates,
            CaseSensitive = FilterSettingsRef?.EnableCaseSensitivity == true
        };
    }

    private async Task LoadProviderFilterValuesAsync(string field)
    {
        var provider = ItemsProvider;
        if (provider == null || !EnableProviderFilterValueRequests || string.IsNullOrWhiteSpace(field))
            return;

        CancelProviderFilterValuesLoad();
        var cts = _providerFilterValuesCts = new CancellationTokenSource();
        var requestSerial = ++_providerFilterValuesRequestSerial;
        var queryVersion = _providerQueryVersion;
        var contextKey = ItemsProviderContextKey;
        IsProviderFilterValuesLoading = true;
        var applyChecklistSearch = false;
        ProviderFilterValuesLastError = null;
        _providerFilterValueCandidates.Remove(field);
        _providerFilterValuesHaveMore.Remove(field);

        try
        {
            // Distinct values should be constrained by the other active
            // columns, not by the field whose checklist is being edited.
            var query = BuildProviderQueryDescriptor();
            query = query with
            {
                Filters = query.Filters
                    .Where(filter => !string.Equals(filter.Field, field, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };

            var result = await provider(new GridItemsRequest(
                0,
                Math.Clamp(ProviderFilterValueRequestSize, 1, 100_000),
                queryVersion,
                true,
                query,
                contextKey,
                cts.Token)
            {
                Purpose = GridItemsRequestPurpose.FilterValues,
                FilterField = field
            });

            if (cts.IsCancellationRequested
                || requestSerial != _providerFilterValuesRequestSerial
                || queryVersion != _providerQueryVersion
                || !Equals(provider, ItemsProvider)
                || !Equals(contextKey, ItemsProviderContextKey))
                return;

            var candidates = new Dictionary<string, FilterValueCandidate>(FilterTextComparer);
            foreach (var value in result.FilterValues ?? Array.Empty<GridProviderFilterValue>())
            {
                var rawValue = value.Value ?? string.Empty;
                var displayText = string.IsNullOrWhiteSpace(value.DisplayText)
                    ? (string.IsNullOrEmpty(rawValue) ? "(blank)" : rawValue)
                    : value.DisplayText;
                var candidate = new FilterValueCandidate(rawValue, displayText, value.Count);
                if (!candidates.TryGetValue(rawValue, out var existing)
                    || IsBetterFilterCandidate(candidate, existing))
                {
                    candidates[rawValue] = candidate;
                }
            }

            var ordered = candidates.Values
                .OrderBy(value => value.DisplayText, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(value => value.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _providerFilterValueCandidates[field] = ordered;
            _providerFilterValuesHaveMore[field] = result.HasMore
                || (result.ResultSetCount.HasValue && result.ResultSetCount.Value > ordered.Length);
            _filterChecklistCommitError = null;

            // An unfiltered checklist represents "all" without an active
            // descriptor. Seed the newly-arrived universe only when the user
            // has not already changed the draft.
            var state = GetColumnState(field);
            if (IsCurrentFilterPopupField(field) && !_filterChecklistDraftTouched && !state.UseCheckedFilter)
            {
                _filterCheckedDraft = new HashSet<string>(ordered.Select(value => value.Value), FilterTextComparer);
            }
            applyChecklistSearch = IsCurrentFilterPopupField(field)
                && !string.IsNullOrWhiteSpace(_filterChecklistSearchDraft);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Closing/reopening the menu superseded this request.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && requestSerial == _providerFilterValuesRequestSerial)
                ProviderFilterValuesLastError = ex;
        }
        finally
        {
            if (ReferenceEquals(_providerFilterValuesCts, cts))
            {
                _providerFilterValuesCts = null;
                IsProviderFilterValuesLoading = false;
            }
            cts.Dispose();
            await InvokeAsync(StateHasChanged);
        }

        // A user may type before the distinct-value request completes. Apply
        // that search to the returned universe once the loading guard is clear.
        if (applyChecklistSearch && IsCurrentFilterPopupField(field)
            && requestSerial == _providerFilterValuesRequestSerial)
            await SetFilterChecklistSearchAsync(_filterChecklistSearchDraft);
    }

    private void CancelProviderFilterValuesLoad()
    {
        if (_providerFilterValuesCts is null)
            return;
        try { _providerFilterValuesCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _providerFilterValuesCts = null;
        IsProviderFilterValuesLoading = false;
    }

    private static bool IsValueOptionalFilterOperator(TextFilterOperator filterOperator) =>
        filterOperator is TextFilterOperator.IsEmpty or TextFilterOperator.IsNotEmpty;

    private static bool IsValueOptionalAdvancedFilterOperator(GridFilterOperator filterOperator) =>
        filterOperator is GridFilterOperator.IsNull
            or GridFilterOperator.IsNotNull
            or GridFilterOperator.IsEmpty
            or GridFilterOperator.IsNotEmpty;

    private static TextFilterOperator MapAdvancedFilterOperator(GridFilterOperator filterOperator) =>
        filterOperator switch
        {
            GridFilterOperator.Equals => TextFilterOperator.Equals,
            GridFilterOperator.NotEquals => TextFilterOperator.DoesNotEqual,
            GridFilterOperator.Contains => TextFilterOperator.Contains,
            GridFilterOperator.DoesNotContain => TextFilterOperator.DoesNotContain,
            GridFilterOperator.StartsWith => TextFilterOperator.BeginsWith,
            GridFilterOperator.EndsWith => TextFilterOperator.EndsWith,
            GridFilterOperator.GreaterThan => TextFilterOperator.GreaterThan,
            GridFilterOperator.GreaterThanOrEquals => TextFilterOperator.GreaterThanOrEqual,
            GridFilterOperator.LessThan => TextFilterOperator.LessThan,
            GridFilterOperator.LessThanOrEquals => TextFilterOperator.LessThanOrEqual,
            GridFilterOperator.IsNull or GridFilterOperator.IsEmpty => TextFilterOperator.IsEmpty,
            GridFilterOperator.IsNotNull or GridFilterOperator.IsNotEmpty => TextFilterOperator.IsNotEmpty,
            _ => TextFilterOperator.Contains
        };

    private IReadOnlyList<GridNumericRangeDescriptor> BuildProviderNumericRanges(
        string field,
        IReadOnlySet<string> selectedKeys)
    {
        var ranges = new List<GridNumericRangeDescriptor>();
        var known = GetNumericFilterRanges(field)
            .Where(range => selectedKeys.Contains(range.Key))
            .ToDictionary(range => range.Key, StringComparer.Ordinal);
        var restored = _restoredProviderNumericRanges.TryGetValue(field, out var restoredRanges)
            ? restoredRanges.ToDictionary(range => range.Key, StringComparer.Ordinal)
            : new Dictionary<string, GridNumericRangeDescriptor>(StringComparer.Ordinal);

        foreach (var key in selectedKeys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (known.TryGetValue(key, out var range))
            {
                ranges.Add(new GridNumericRangeDescriptor(
                    range.Key,
                    range.IsBlank ? null : range.Min,
                    range.IsBlank ? null : range.Max,
                    range.IncludeMax,
                    range.IsBlank));
                continue;
            }

            if (restored.TryGetValue(key, out var restoredRange))
            {
                ranges.Add(restoredRange);
                continue;
            }

            // Keys are deliberately self-describing. This fallback preserves
            // restored/provider-only state even when no local DataSource exists.
            if (string.Equals(key, NumericFilterRange.BlankKey, StringComparison.Ordinal))
            {
                ranges.Add(new GridNumericRangeDescriptor(key, null, null, false, true));
                continue;
            }

            var parts = key.Split(':');
            if (parts.Length == 2
                && string.Equals(parts[0], "value", StringComparison.Ordinal)
                && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
            {
                ranges.Add(new GridNumericRangeDescriptor(key, exact, exact, true, false));
            }
            else if (parts.Length == 4
                && string.Equals(parts[0], "range", StringComparison.Ordinal)
                && decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum)
                && decimal.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum))
            {
                ranges.Add(new GridNumericRangeDescriptor(key, minimum, maximum, false, false));
            }
        }

        return ranges;
    }

    private static int ComputeProviderQuerySignature(GridQueryDescriptor query)
    {
        var hash = new HashCode();
        hash.Add(query.SearchText, StringComparer.Ordinal);
        hash.Add(query.ExpressionFilterText, StringComparer.Ordinal);
        hash.Add(query.CaseSensitive);
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
            hash.Add(filter.Source);
            hash.Add(filter.SecondOperator);
            hash.Add(filter.AdvancedOperator);
            hash.Add(filter.SecondAdvancedOperator);
            hash.Add(filter.SecondValue, StringComparer.Ordinal);
            hash.Add(filter.LogicalOperator);
            hash.Add(filter.BlankRowFilter);
            foreach (var value in filter.Values)
                hash.Add(value, StringComparer.Ordinal);
            foreach (var range in filter.NumericRanges)
            {
                hash.Add(range.Key, StringComparer.Ordinal);
                hash.Add(range.Minimum);
                hash.Add(range.Maximum);
                hash.Add(range.IncludeMaximum);
                hash.Add(range.IsBlank);
            }
        }
        foreach (var group in query.Groups)
        {
            hash.Add(group.Field, StringComparer.OrdinalIgnoreCase);
            hash.Add(group.Direction);
        }
        foreach (var aggregate in query.Aggregates)
        {
            hash.Add(aggregate.Field, StringComparer.OrdinalIgnoreCase);
            hash.Add(aggregate.Type);
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
        var renderInitialStatus = _providerTotalCount < 0 && _providerWindowItems.Count == 0;

        try
        {
            if (renderInitialStatus)
                await InvokeAsync(StateHasChanged);

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
            // Flat providers can return authoritative aggregates for the
            // complete query just like grouped providers. Keep them outside
            // the range cache so later window navigation never replaces the
            // totals with calculations over only the painted rows.
            CaptureProviderAggregates(result.Aggregates, _providerQueryAggregates);
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
            var renderSettledStatus = false;
            if (serial == _providerRequestSerial)
            {
                IsItemsProviderLoading = false;
                // Success is rendered after CommitProviderWindow. Here only a
                // failure needs a render because there is no window to commit.
                renderSettledStatus = ItemsProviderLastError is not null;
            }
            if (ReferenceEquals(_providerLoadCts, cts))
                _providerLoadCts = null;
            cts.Dispose();
            if (renderSettledStatus)
                await InvokeAsync(StateHasChanged);
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
        if (UsesProviderGrouping)
            return ProviderScrollLoadStatus.NoChange;

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
        CancelProviderFilterValuesLoad();
        CancelProviderExport();
        _providerIdentityCaptured = false;
        _observedItemsProvider = null;
        _observedItemKeySelector = null;
        _observedItemsProviderContextKey = null;
        _observedProviderQuerySignature = 0;
        _observedProviderGroupingMode = false;
        _providerQuery = GridQueryDescriptor.Empty;
        _providerWindowItems = Array.Empty<TValue>();
        _providerWindowStart = 0;
        _providerTotalCount = -1;
        _providerCache.Clear();
        _providerFilterValueCandidates.Clear();
        _providerFilterValuesHaveMore.Clear();
        ResetProviderGroupingState();
        IsItemsProviderLoading = false;
        ItemsProviderLastError = null;
        ProviderFilterValuesLastError = null;
    }

    private void DisposeItemsProviderState()
    {
        CancelProviderLoad();
        CancelProviderFilterValuesLoad();
        _providerCache.Clear();
        _providerFilterValueCandidates.Clear();
        _providerFilterValuesHaveMore.Clear();
        ResetProviderGroupingState();
    }
}
