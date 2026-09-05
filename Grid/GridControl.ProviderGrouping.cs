using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Enables provider-executed grouping when <see cref="ItemsProvider"/> and
    /// one or more group descriptors are active. Off by default so every
    /// existing flat provider keeps its current request and rendering contract.
    /// </summary>
    [Parameter] public bool EnableProviderGrouping { get; set; }

    /// <summary>Maximum groups requested for each lazy group page.</summary>
    [Parameter] public int ProviderGroupPageSize { get; set; } = 100;

    /// <summary>Maximum leaf rows requested for each lazy group page.</summary>
    [Parameter] public int ProviderGroupItemPageSize { get; set; } = 250;

    /// <summary>
    /// Collapses newly received provider groups until the user expands them.
    /// This defaults to true so enabling remote grouping never triggers a
    /// cascade that eagerly loads the entire hierarchy.
    /// </summary>
    [Parameter] public bool ProviderGroupsInitiallyCollapsed { get; set; } = true;

    /// <summary>True while a root or child provider group request is active.</summary>
    public bool IsProviderGroupLoading =>
        UsesProviderGrouping
        && (IsItemsProviderLoading || _providerGroupStates.Values.Any(state => state.IsLoading));

    private bool UsesProviderGrouping =>
        UsesItemsProvider
        && EnableProviderGrouping
        && AllowGrouping
        && _groupDescriptors.Count > 0;

    private readonly List<GroupResult<TValue>> _providerRootGroups = new();
    private readonly Dictionary<string, ProviderGroupState> _providerGroupStates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _providerQueryAggregates =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _providerGroupsLoaded;
    private int _providerRootNextIndex;
    private int? _providerRootResultCount;
    private bool _providerRootHasMore;
    private Exception? _providerRootError;
    private bool _restoreProviderGroupExpansionFromState;

    private sealed class ProviderGroupState
    {
        public required GroupResult<TValue> Group { get; init; }
        public required IReadOnlyList<GridProviderGroupKey> Keys { get; init; }
        public required int Level { get; init; }
        public bool ChildrenLoaded { get; set; }
        public int NextIndex { get; set; }
        public int? ResultSetCount { get; set; }
        public bool HasMore { get; set; }
        public bool IsLoading { get; set; }
        public Exception? Error { get; set; }
        public CancellationTokenSource? LoadCts { get; set; }
    }

    private async Task<bool> LoadAndCommitProviderRootGroupsAsync()
    {
        var provider = ItemsProvider;
        if (provider is null)
            return false;

        CancelProviderLoad();
        var cts = new CancellationTokenSource();
        _providerLoadCts = cts;
        var serial = ++_providerRequestSerial;
        var queryVersion = _providerQueryVersion;
        IsItemsProviderLoading = true;
        ItemsProviderLastError = null;
        _providerRootError = null;
        var renderInitialStatus = _providerRootGroups.Count == 0;

        try
        {
            if (renderInitialStatus)
                await InvokeAsync(StateHasChanged);

            var request = new GridItemsRequest(
                0,
                Math.Clamp(ProviderGroupPageSize, 1, 10_000),
                queryVersion,
                IncludeTotalCount: true,
                _providerQuery,
                ItemsProviderContextKey,
                cts.Token)
            {
                Purpose = GridItemsRequestPurpose.Groups
            };
            var result = await provider(request);

            if (cts.IsCancellationRequested
                || serial != _providerRequestSerial
                || queryVersion != _providerQueryVersion
                || !Equals(provider, ItemsProvider))
            {
                return false;
            }

            _providerRootGroups.Clear();
            _providerGroupStates.Clear();
            _providerQueryAggregates.Clear();
            CaptureProviderAggregates(result.Aggregates, _providerQueryAggregates);

            var groups = result.Groups ?? Array.Empty<GridProviderGroup<TValue>>();
            foreach (var group in groups)
                _providerRootGroups.Add(CreateProviderGroup(group, 0, "", Array.Empty<GridProviderGroupKey>()));

            _allGroupsCollapsed = _providerRootGroups.Count > 0
                && _providerRootGroups.All(group => group.IsCollapsed);
            if (_allGroupsCollapsed)
                _expandAllGroups = false;

            _providerTotalCount = Math.Max(0, result.TotalCount);
            _pageState.TotalRecords = _providerTotalCount;
            _providerRootNextIndex = groups.Count;
            _providerRootResultCount = result.ResultSetCount;
            _providerRootHasMore = HasProviderContinuation(
                result.HasMore,
                _providerRootNextIndex,
                _providerRootResultCount,
                groups.Count);
            _providerGroupsLoaded = true;

            if (groups.Count == 0 && result.Items is { Count: > 0 })
            {
                ItemsProviderLastError = new InvalidOperationException(
                    $"{nameof(EnableProviderGrouping)} is enabled, but the provider returned rows " +
                    $"instead of {nameof(GridItemsResult<TValue>.Groups)} for a " +
                    $"{nameof(GridItemsRequestPurpose.Groups)} request.");
            }

            // When a host explicitly opts out of initially-collapsed groups,
            // there is no user expansion event to trigger the first lazy load.
            // Materialize only those branches that are currently expanded;
            // the default collapsed mode remains strictly load-on-demand.
            foreach (var group in _providerRootGroups.Where(group => !group.IsCollapsed).ToList())
                await EnsureProviderGroupLoadedAsync(group);

            ClearPassViewMemos();
            return ItemsProviderLastError is null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (serial == _providerRequestSerial && queryVersion == _providerQueryVersion)
            {
                ItemsProviderLastError = ex;
                _providerRootError = ex;
                // Record the attempt so an exception does not create an
                // after-render retry loop. ReloadItemsAsync remains explicit.
                _providerGroupsLoaded = true;
            }
            return false;
        }
        finally
        {
            var renderSettledStatus = false;
            if (serial == _providerRequestSerial)
            {
                IsItemsProviderLoading = false;
                renderSettledStatus = renderInitialStatus || ItemsProviderLastError is not null;
            }
            if (ReferenceEquals(_providerLoadCts, cts))
                _providerLoadCts = null;
            cts.Dispose();
            if (renderSettledStatus)
                await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Loads the next root-group page, when the provider reports one.</summary>
    public Task LoadMoreProviderGroupsAsync()
    {
        if (!UsesProviderGrouping || !_providerRootHasMore || IsItemsProviderLoading)
            return Task.CompletedTask;

        return LoadMoreProviderRootGroupsCoreAsync();
    }

    private async Task LoadMoreProviderRootGroupsCoreAsync()
    {
        var provider = ItemsProvider;
        if (provider is null)
            return;

        CancelProviderLoad();
        var cts = new CancellationTokenSource();
        _providerLoadCts = cts;
        var serial = ++_providerRequestSerial;
        var queryVersion = _providerQueryVersion;
        IsItemsProviderLoading = true;
        ItemsProviderLastError = null;
        _providerRootError = null;

        try
        {
            var request = new GridItemsRequest(
                _providerRootNextIndex,
                Math.Clamp(ProviderGroupPageSize, 1, 10_000),
                queryVersion,
                IncludeTotalCount: false,
                _providerQuery,
                ItemsProviderContextKey,
                cts.Token)
            {
                Purpose = GridItemsRequestPurpose.Groups
            };
            var result = await provider(request);
            if (cts.IsCancellationRequested
                || serial != _providerRequestSerial
                || queryVersion != _providerQueryVersion
                || !Equals(provider, ItemsProvider))
            {
                return;
            }

            var groups = result.Groups ?? Array.Empty<GridProviderGroup<TValue>>();
            var addedGroups = groups
                .Select(group => CreateProviderGroup(group, 0, "", Array.Empty<GridProviderGroupKey>()))
                .ToList();
            _providerRootGroups.AddRange(addedGroups);

            _allGroupsCollapsed = _providerRootGroups.Count > 0
                && _providerRootGroups.All(group => group.IsCollapsed);

            _providerRootNextIndex += groups.Count;
            _providerRootResultCount = result.ResultSetCount ?? _providerRootResultCount;
            _providerRootHasMore = HasProviderContinuation(
                result.HasMore,
                _providerRootNextIndex,
                _providerRootResultCount,
                groups.Count);
            if (result.TotalCount >= 0)
            {
                _providerTotalCount = result.TotalCount;
                _pageState.TotalRecords = result.TotalCount;
            }
            CaptureProviderAggregates(result.Aggregates, _providerQueryAggregates);
            foreach (var group in addedGroups.Where(group => !group.IsCollapsed))
                await EnsureProviderGroupLoadedAsync(group);
            ClearPassViewMemos();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (serial == _providerRequestSerial && queryVersion == _providerQueryVersion)
            {
                ItemsProviderLastError = ex;
                _providerRootError = ex;
            }
        }
        finally
        {
            if (serial == _providerRequestSerial)
                IsItemsProviderLoading = false;
            if (ReferenceEquals(_providerLoadCts, cts))
                _providerLoadCts = null;
            cts.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    private GroupResult<TValue> CreateProviderGroup(
        GridProviderGroup<TValue> source,
        int level,
        string parentPath,
        IReadOnlyList<GridProviderGroupKey> parentKeys)
    {
        var path = string.IsNullOrWhiteSpace(source.GroupPath)
            ? BuildProviderGroupPath(parentPath, source.Field, source.Key)
            : source.GroupPath!;
        var keys = parentKeys
            .Append(new GridProviderGroupKey(source.Field, source.Key))
            .ToArray();
        var isCollapsed = ResolveInitialProviderGroupCollapsed(path);
        if (isCollapsed)
            _collapsedGroupPaths.Add(path);

        var group = new GroupResult<TValue>
        {
            Field = source.Field,
            HeaderText = _groupDescriptors.ElementAtOrDefault(level)?.HeaderText ?? source.Field,
            Key = source.Key,
            DisplayText = string.IsNullOrWhiteSpace(source.DisplayText)
                ? Convert.ToString(source.Key, CultureInfo.CurrentCulture) ?? "(empty)"
                : source.DisplayText!,
            GroupPath = path,
            Count = Math.Max(0, source.Count),
            Items = source.Items ?? Array.Empty<TValue>(),
            IsCollapsed = isCollapsed
        };
        CaptureProviderAggregates(source.Aggregates, group.Aggregates);

        var childGroups = new List<GroupResult<TValue>>();
        foreach (var child in source.Groups ?? Array.Empty<GridProviderGroup<TValue>>())
            childGroups.Add(CreateProviderGroup(child, level + 1, path, keys));
        group.SubGroups = childGroups;

        var isLeaf = level >= _groupDescriptors.Count - 1;
        var eagerCount = isLeaf ? group.Items.Count() : childGroups.Count;
        var knownCount = source.ChildCount ?? (isLeaf ? group.Count : null);
        var hasChildren = source.HasChildren || eagerCount > 0;
        var state = new ProviderGroupState
        {
            Group = group,
            Keys = keys,
            Level = level,
            ChildrenLoaded = !hasChildren || eagerCount > 0,
            NextIndex = eagerCount,
            ResultSetCount = knownCount,
            HasMore = hasChildren && eagerCount > 0 && (!knownCount.HasValue || eagerCount < knownCount.Value)
        };
        _providerGroupStates[path] = state;
        return group;
    }

    private bool ResolveInitialProviderGroupCollapsed(string path)
    {
        if (_collapsedGroupPaths.Contains(path))
            return true;
        if (_restoreProviderGroupExpansionFromState)
            return false;
        if (_expandAllGroups)
            return false;
        if (_allGroupsCollapsed)
            return true;
        return ProviderGroupsInitiallyCollapsed || DefaultGroupsCollapsed;
    }

    private static string BuildProviderGroupPath(string parentPath, string field, object? key)
    {
        var value = Convert.ToString(key, CultureInfo.InvariantCulture) ?? "(null)";
        var segment = $"{Uri.EscapeDataString(field)}:{Uri.EscapeDataString(value)}";
        return string.IsNullOrEmpty(parentPath) ? segment : $"{parentPath}/{segment}";
    }

    private static void CaptureProviderAggregates(
        IEnumerable<GridAggregateResult>? source,
        IDictionary<string, object?> destination)
    {
        if (source is null)
            return;
        foreach (var aggregate in source)
            destination[$"{aggregate.Field}_{aggregate.Type}"] = aggregate.Value;
    }

    private static bool HasProviderContinuation(
        bool explicitHasMore,
        int nextIndex,
        int? resultSetCount,
        int returnedCount)
    {
        if (returnedCount == 0)
            return false;
        return explicitHasMore || (resultSetCount.HasValue && nextIndex < resultSetCount.Value);
    }

    private IEnumerable<TValue> EnumerateLoadedProviderRows()
    {
        static IEnumerable<TValue> Walk(IEnumerable<GroupResult<TValue>> groups)
        {
            foreach (var group in groups)
            {
                foreach (var item in group.Items)
                    yield return item;
                foreach (var item in Walk(group.SubGroups))
                    yield return item;
            }
        }

        return Walk(_providerRootGroups);
    }

    private async Task EnsureProviderGroupLoadedAsync(GroupResult<TValue> group)
    {
        if (!_providerGroupStates.TryGetValue(group.GroupPath, out var state)
            || state.IsLoading)
            return;

        if (!state.ChildrenLoaded)
            await LoadProviderGroupPageAsync(state, append: false);

        // Eager child metadata can itself describe initially-expanded groups.
        // Walk those descendants now so expanded headers never render as an
        // unexplained empty branch. Collapsed descendants remain lazy.
        if (!group.IsCollapsed)
        {
            foreach (var child in group.SubGroups.Where(child => !child.IsCollapsed).ToList())
                await EnsureProviderGroupLoadedAsync(child);
        }
    }

    private Task LoadMoreProviderGroupAsync(GroupResult<TValue> group)
    {
        if (!_providerGroupStates.TryGetValue(group.GroupPath, out var state)
            || !state.HasMore
            || state.IsLoading)
        {
            return Task.CompletedTask;
        }

        return LoadProviderGroupPageAsync(state, append: true);
    }

    private async Task ExpandAllProviderGroupsAsync(IEnumerable<GroupResult<TValue>> groups)
    {
        foreach (var group in groups.ToList())
        {
            if (!_expandAllGroups || !UsesProviderGrouping)
                return;

            group.IsCollapsed = false;
            _collapsedGroupPaths.Remove(group.GroupPath);
            await EnsureProviderGroupLoadedAsync(group);
            if (group.SubGroups.Any())
                await ExpandAllProviderGroupsAsync(group.SubGroups);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadProviderGroupPageAsync(ProviderGroupState state, bool append)
    {
        var provider = ItemsProvider;
        if (provider is null)
            return;

        state.LoadCts?.Cancel();
        state.LoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        state.LoadCts = cts;
        state.IsLoading = true;
        state.Error = null;
        var queryVersion = _providerQueryVersion;
        await InvokeAsync(StateHasChanged);

        try
        {
            var childLevel = state.Level + 1;
            var loadsGroups = childLevel < _groupDescriptors.Count;
            var startIndex = append ? state.NextIndex : 0;
            var count = loadsGroups
                ? Math.Clamp(ProviderGroupPageSize, 1, 10_000)
                : Math.Clamp(ProviderGroupItemPageSize, 1, 100_000);
            var request = new GridItemsRequest(
                startIndex,
                count,
                queryVersion,
                IncludeTotalCount: false,
                _providerQuery,
                ItemsProviderContextKey,
                cts.Token)
            {
                Purpose = loadsGroups
                    ? GridItemsRequestPurpose.GroupChildren
                    : GridItemsRequestPurpose.GroupItems,
                GroupRequest = new GridProviderGroupRequest(
                    state.Level,
                    state.Keys,
                    state.Group.GroupPath)
            };
            var result = await provider(request);
            if (cts.IsCancellationRequested
                || queryVersion != _providerQueryVersion
                || !Equals(provider, ItemsProvider))
            {
                return;
            }

            int received;
            if (loadsGroups)
            {
                var newGroups = (result.Groups ?? Array.Empty<GridProviderGroup<TValue>>())
                    .Select(group => CreateProviderGroup(
                        group,
                        childLevel,
                        state.Group.GroupPath,
                        state.Keys))
                    .ToList();
                var existing = append
                    ? state.Group.SubGroups.ToList()
                    : new List<GroupResult<TValue>>();
                existing.AddRange(newGroups);
                state.Group.SubGroups = existing;
                received = newGroups.Count;
            }
            else
            {
                var newItems = (result.Items ?? Array.Empty<TValue>()).ToList();
                var existing = append
                    ? state.Group.Items.ToList()
                    : new List<TValue>();
                existing.AddRange(newItems);
                state.Group.Items = existing;
                received = newItems.Count;
            }

            CaptureProviderAggregates(result.Aggregates, state.Group.Aggregates);
            state.ChildrenLoaded = true;
            state.NextIndex = startIndex + received;
            state.ResultSetCount = result.ResultSetCount
                ?? state.ResultSetCount
                ?? (loadsGroups ? null : state.Group.Count);
            state.HasMore = HasProviderContinuation(
                result.HasMore,
                state.NextIndex,
                state.ResultSetCount,
                received);
            ClearPassViewMemos();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (queryVersion == _providerQueryVersion)
            {
                state.Error = ex;
                ItemsProviderLastError = ex;
            }
        }
        finally
        {
            if (ReferenceEquals(state.LoadCts, cts))
            {
                state.LoadCts = null;
                state.IsLoading = false;
            }
            cts.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    private RenderFragment RenderProviderGroupedRows() => builder =>
    {
        var sequence = 0;
        var rowIndex = 0;
        if (IsItemsProviderLoading && _providerRootGroups.Count == 0)
        {
            RenderProviderGroupStatusRow(builder, ref sequence, "Loading groups…", null, null);
            return;
        }

        if (ItemsProviderLastError is not null && _providerRootGroups.Count == 0)
        {
            RenderProviderGroupStatusRow(
                builder,
                ref sequence,
                "Could not load groups. Retry",
                ReloadItemsAsync,
                null);
            return;
        }

        RenderProviderGroupLevel(builder, ref sequence, ref rowIndex, _providerRootGroups, 0);
        if (_providerRootHasMore)
        {
            RenderProviderGroupStatusRow(
                builder,
                ref sequence,
                IsItemsProviderLoading
                    ? "Loading more groups…"
                    : _providerRootError is not null ? "Could not load more groups. Retry" : "Load more groups",
                IsItemsProviderLoading ? null : LoadMoreProviderGroupsAsync,
                null);
        }
    };

    private void RenderProviderGroupLevel(
        RenderTreeBuilder builder,
        ref int sequence,
        ref int rowIndex,
        IEnumerable<GroupResult<TValue>> groups,
        int level)
    {
        foreach (var group in groups)
        {
            RenderGroupHeaderRow(builder, group, level);
            if (group.IsCollapsed)
                continue;

            _providerGroupStates.TryGetValue(group.GroupPath, out var state);
            if (state?.IsLoading == true && !state.ChildrenLoaded)
            {
                RenderProviderGroupStatusRow(builder, ref sequence, "Loading…", null, level + 1);
            }
            else if (state?.Error is not null && !state.ChildrenLoaded)
            {
                RenderProviderGroupStatusRow(
                    builder,
                    ref sequence,
                    "Could not load this group. Retry",
                    () => LoadProviderGroupPageAsync(state, append: false),
                    level + 1);
            }
            else if (group.SubGroups.Any())
            {
                RenderProviderGroupLevel(builder, ref sequence, ref rowIndex, group.SubGroups, level + 1);
            }
            else
            {
                foreach (var item in group.Items)
                    RenderGroupedItemRow(builder, item, rowIndex++);
            }

            if (state?.HasMore == true)
            {
                RenderProviderGroupStatusRow(
                    builder,
                    ref sequence,
                    state.IsLoading
                        ? "Loading more…"
                        : state.Error is not null ? "Could not load more. Retry" : "Load more",
                    state.IsLoading ? null : () => LoadMoreProviderGroupAsync(group),
                    level + 1);
            }

            RenderGroupFooterRows(builder, group);
        }
    }

    private void RenderProviderGroupStatusRow(
        RenderTreeBuilder builder,
        ref int sequence,
        string text,
        Func<Task>? action,
        int? level)
    {
        builder.OpenElement(sequence++, "tr");
        builder.AddAttribute(sequence++, "class", "fx-provider-group-status-row");
        builder.OpenElement(sequence++, "td");
        builder.AddAttribute(sequence++, "class", "fx-cell fx-provider-group-status-cell");
        builder.AddAttribute(sequence++, "colspan", Math.Max(1, TotalColumnCount));
        if (level.GetValueOrDefault() > 0)
        {
            builder.AddAttribute(
                sequence++,
                "style",
                $"padding-left:{level.GetValueOrDefault() * 24 + 8}px;");
        }

        if (action is null)
        {
            builder.AddContent(sequence++, text);
        }
        else
        {
            builder.OpenElement(sequence++, "button");
            builder.AddAttribute(sequence++, "type", "button");
            builder.AddAttribute(sequence++, "class", "fx-provider-group-more");
            builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, action));
            builder.AddContent(sequence++, text);
            builder.CloseElement();
        }
        builder.CloseElement();
        builder.CloseElement();
    }

    private void ResetProviderGroupingState()
    {
        foreach (var state in _providerGroupStates.Values)
        {
            try { state.LoadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            state.LoadCts?.Dispose();
        }
        _providerGroupStates.Clear();
        _providerRootGroups.Clear();
        _providerQueryAggregates.Clear();
        _providerGroupsLoaded = false;
        _providerRootNextIndex = 0;
        _providerRootResultCount = null;
        _providerRootHasMore = false;
        _providerRootError = null;
    }
}
