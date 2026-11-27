using System.Globalization;
using Fx.ControlKit.Logging;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Result returned by <see cref="GridControl{TValue}.FindAsync"/> when searching rows across all visible columns or a target column.
/// </summary>
public sealed record GridFindResult<TItem>(
    bool Found,
    TItem? MatchedItem,
    int MatchedRowIndex,
    int MatchOrdinal,
    int TotalMatches,
    string ScopeField,
    string OperationId);

public partial class GridControl<TValue>
{
    private string? _lastDragSelectionOpId;
    private DateTime _lastDragSelectionUtc;
    private int _lastFindCursorIndex = -1;
    private string _lastFindQuery = string.Empty;

    private string? ConsumeRecentDragSelectionOpId()
    {
        if (!string.IsNullOrEmpty(_lastDragSelectionOpId)
            && DateTime.UtcNow - _lastDragSelectionUtc <= TimeSpan.FromMinutes(3))
        {
            return _lastDragSelectionOpId;
        }
        return null;
    }

    // ── 1. Grid Sort Custom Graph Telemetry ──────────────────────────────────

    internal string RecordGridSortGraph(
        IReadOnlyList<GridSortDescriptor> activeSorts,
        string subKind,
        string? primaryFieldOverride = null,
        string? directionOverride = null)
    {
        var gridId = ResolveControlIdentifier() ?? typeof(TValue).Name;
        var totalRows = SafeTotalRowCount();
        var levels = activeSorts.Count;
        var primaryField = primaryFieldOverride
            ?? (levels > 0 ? activeSorts[0].Field : "(none)");
        var primaryDir = directionOverride
            ?? (levels > 0 ? activeSorts[0].Direction.ToString() : "Cleared");
        var spec = levels > 0
            ? string.Join(", ", activeSorts.Select(s => $"{s.Field} {s.Direction}"))
            : $"{primaryField} Cleared";

        return RecordCustomAnalytics(FlexCustomAnalyticsEvents.GridSort, "GridAnalytics", b =>
        {
            b.WithSubKind(subKind)
             .WithOutcome(levels > 0 ? "Sorted" : "Cleared")
             .WithValue(spec)
             .WithSummary($"Sorted '{gridId}' via {subKind}: {spec} ({totalRows:N0} rows)")
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.SortLevels, levels)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.TotalRows, totalRows)
             .WithTag("mode", subKind)
             .WithTag("primaryField", primaryField)
             .WithTag("direction", primaryDir)
             .WithTag("sortSpec", spec)
             .AddNode("grid", "Grid", $"Grid:{gridId}", totalRows);

            if (levels == 0)
            {
                b.AddNode("clear", "SortCleared", $"Cleared {primaryField}")
                 .AddEdge("grid", "clear", subKind, 1);
            }
            else
            {
                string prevNode = "grid";
                for (int i = 0; i < activeSorts.Count; i++)
                {
                    var s = activeSorts[i];
                    var nodeId = $"sort-{i + 1}";
                    b.AddNode(nodeId, "SortColumn", $"L{i + 1}: {s.Field} ({s.Direction})", totalRows);
                    b.AddEdge(prevNode, nodeId, i == 0 ? subKind : "then-by", Math.Max(1, totalRows));
                    prevNode = nodeId;
                }
                b.AddNode("result", "OrderedView", $"{totalRows:N0} Ordered Rows", totalRows)
                 .AddEdge(prevNode, "result", "orders", Math.Max(1, totalRows));
            }
        });
    }

    /// <summary>Programmatically sorts this grid by one or more columns and records a <c>Grid.Sort</c> telemetry graph.</summary>
    public async Task<string> ApplySortDescriptorsAsync(IReadOnlyList<GridSortDescriptor> sorts, string sourceMode = "ProgrammaticSort")
    {
        foreach (var st in _columnStates.Values)
            st.SortDirection = null;
        _sortPriorityFields.Clear();

        foreach (var s in sorts)
        {
            if (string.IsNullOrWhiteSpace(s.Field)) continue;
            GetColumnState(s.Field).SortDirection = s.Direction;
            _sortPriorityFields.Add(s.Field);
        }

        ResetToFirstPage();
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        if (UsesItemsProvider)
            await ReloadItemsAsync();

        var opId = RecordGridSortGraph(sorts, sourceMode);
        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Sorting);
        return opId;
    }

    // ── 2. Grid Find Custom Graph Telemetry & Built-In FindAsync ─────────────

    /// <summary>
    /// Searches the grid's filtered records across all visible columns (or a specific column) with wildcard (<c>*</c>, <c>?</c>) support,
    /// optionally selects and reveals the next match, and records a <c>Grid.Find</c> custom telemetry graph.
    /// </summary>
    public async Task<GridFindResult<TValue>> FindAsync(
        string findText,
        string? columnField = null,
        bool matchCase = false,
        bool matchWholeWord = false,
        bool searchDown = true,
        int? startAfterRowIndex = null,
        bool revealMatch = true)
    {
        var scope = string.IsNullOrWhiteSpace(columnField) || columnField == "*" ? "AllColumns" : columnField.Trim();
        if (string.IsNullOrWhiteSpace(findText))
            return new GridFindResult<TValue>(false, default, -1, 0, 0, scope, string.Empty);

        if (!string.Equals(_lastFindQuery, findText, StringComparison.Ordinal))
        {
            _lastFindQuery = findText;
            _lastFindCursorIndex = -1;
        }

        var rows = GetFilteredRecords().ToList();
        var cols = scope == "AllColumns"
            ? VisibleColumns.ToList()
            : VisibleColumns.Where(c => string.Equals(c.Field, scope, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cols.Count == 0 && scope != "AllColumns")
            cols = Columns.Where(c => string.Equals(c.Field, scope, StringComparison.OrdinalIgnoreCase)).ToList();

        var pattern = BuildGridWildcardPattern(findText, matchWholeWord);
        var matchIndices = new List<int>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (ItemMatchesGridFind(rows[i], cols, scope, pattern, matchCase))
                matchIndices.Add(i);
        }

        if (matchIndices.Count == 0)
        {
            var missOpId = RecordGridFindGraph(
                subKind: "FindNext",
                pattern: findText,
                scopeField: scope,
                direction: searchDown ? "Down" : "Up",
                matchCount: 0,
                matchOrdinal: 0,
                totalRows: rows.Count);
            return new GridFindResult<TValue>(false, default, -1, 0, 0, scope, missOpId);
        }

        int anchor = startAfterRowIndex ?? _lastFindCursorIndex;
        int chosenOrdinal = searchDown
            ? matchIndices.FindIndex(idx => idx > anchor)
            : matchIndices.FindLastIndex(idx => idx < (anchor < 0 ? rows.Count : anchor));
        if (chosenOrdinal < 0)
            chosenOrdinal = searchDown ? 0 : matchIndices.Count - 1;

        int matchedIdx = matchIndices[chosenOrdinal];
        var matchedItem = rows[matchedIdx];
        _lastFindCursorIndex = matchedIdx;

        if (revealMatch)
        {
            if (_groupDescriptors.Count > 0)
                await ExpandAllGroupAsync();
            await SelectAndRevealRowAsync(matchedItem, clearExistingSelection: true);
        }

        var hitOpId = RecordGridFindGraph(
            subKind: "FindNext",
            pattern: findText,
            scopeField: scope,
            direction: searchDown ? "Down" : "Up",
            matchCount: matchIndices.Count,
            matchOrdinal: chosenOrdinal + 1,
            totalRows: rows.Count);

        return new GridFindResult<TValue>(true, matchedItem, matchedIdx, chosenOrdinal + 1, matchIndices.Count, scope, hitOpId);
    }

    internal string RecordGridFindGraph(
        string subKind,
        string pattern,
        string scopeField,
        string direction,
        int matchCount,
        int matchOrdinal,
        int totalRows)
    {
        var gridId = ResolveControlIdentifier() ?? typeof(TValue).Name;
        var outcome = string.IsNullOrWhiteSpace(pattern)
            ? "Cleared"
            : (matchCount > 0 ? "Matched" : "NoMatch");

        return RecordCustomAnalytics(FlexCustomAnalyticsEvents.GridFind, "GridAnalytics", b =>
        {
            b.WithSubKind(subKind)
             .WithOutcome(outcome)
             .WithValue(string.IsNullOrWhiteSpace(pattern) ? "(cleared)" : pattern)
             .WithSummary(matchCount > 0
                 ? $"Grid Find '{pattern}' in {scopeField}: {matchCount:N0} match(es) of {totalRows:N0} rows"
                 : $"Grid Find '{pattern}' in {scopeField}: 0 matches ({totalRows:N0} rows scanned)")
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.MatchCount, matchCount)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.MatchOrdinal, matchOrdinal)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.TotalRows, totalRows)
             .WithTag("mode", subKind)
             .WithTag("pattern", pattern)
             .WithTag("scope", scopeField)
             .WithTag("direction", direction)
             .AddNode("grid", "Grid", $"Grid:{gridId}", totalRows)
             .AddNode("query", "FindQuery", $"Find: \"{TruncateLabel(pattern, 24)}\"", Math.Max(1, totalRows))
             .AddNode("scope", "ColumnScope", $"Scope: {scopeField}", Math.Max(1, totalRows))
             .AddNode("outcome", outcome, matchCount > 0 ? $"Matched ({matchCount:N0} rows)" : "No Match (0)", Math.Max(1, matchCount))
             .AddEdge("grid", "query", subKind, Math.Max(1, totalRows))
             .AddEdge("query", "scope", $"dir:{direction}", Math.Max(1, totalRows))
             .AddEdge("scope", "outcome", outcome.ToLowerInvariant(), Math.Max(1, matchCount));
        });
    }

    // ── 3. Grid Drag Custom Graph Telemetry ──────────────────────────────────

    internal string RecordGridDragGraph(
        string subKind,
        string sourceLabel,
        string targetLabel,
        int selectedRows = 0,
        int selectedCells = 0,
        int dragSpanRows = 0,
        int groupLevels = 0,
        string? sourceField = null,
        string? targetField = null)
    {
        var gridId = ResolveControlIdentifier() ?? typeof(TValue).Name;
        var effectiveCount = Math.Max(selectedRows, Math.Max(selectedCells, Math.Max(1, dragSpanRows)));

        var opId = RecordCustomAnalytics(FlexCustomAnalyticsEvents.GridDrag, "DragDrop", b =>
        {
            b.WithSubKind(subKind)
             .WithOutcome("Dropped")
             .WithValue($"{sourceLabel} -> {targetLabel}")
             .WithSummary($"Grid Drag ({subKind}) on '{gridId}': {sourceLabel} -> {targetLabel}"
                 + (selectedRows > 1 ? $" ({selectedRows:N0} rows selected)" : string.Empty))
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.SelectedRows, selectedRows)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.SelectedCells, selectedCells)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.DragSpanRows, dragSpanRows)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.GroupLevels, groupLevels)
             .WithTag("subKind", subKind)
             .WithTag("source", sourceLabel)
             .WithTag("target", targetLabel)
             .WithTag("sourceField", sourceField)
             .WithTag("targetField", targetField)
             .AddNode("grid", "Grid", $"Grid:{gridId}", effectiveCount)
             .AddNode("drag-src", "DragSource", sourceLabel, effectiveCount)
             .AddNode("drag-dst", "DropTarget", targetLabel, effectiveCount)
             .AddEdge("grid", "drag-src", "drag-start", effectiveCount)
             .AddEdge("drag-src", "drag-dst", subKind, effectiveCount);

            if (selectedRows > 0 || selectedCells > 0)
            {
                var selLabel = selectedRows > 0 ? $"{selectedRows:N0} Rows Selected" : $"{selectedCells:N0} Cells Selected";
                b.AddNode("selection", "SelectionSet", selLabel, effectiveCount)
                 .AddEdge("drag-dst", "selection", "produces", effectiveCount);
            }
        });

        if (subKind is "RowRangeSelect" or "CellRangeSelect")
        {
            _lastDragSelectionOpId = opId;
            _lastDragSelectionUtc = DateTime.UtcNow;
        }

        return opId;
    }

    /// <summary>
    /// Selects a contiguous row range (modeling a drag-range selection) and records a <c>Grid.Drag</c> telemetry graph,
    /// arming causal linking for a subsequent <c>Grid.MultiSelectEdit</c>.
    /// </summary>
    public async Task<string> SelectRowDragRangeAsync(int anchorRowIndex, int endRowIndex)
    {
        var visible = GetVisibleRowItems();
        if (visible.Count == 0)
            return string.Empty;

        int a = Math.Clamp(anchorRowIndex, 0, visible.Count - 1);
        int b = Math.Clamp(endRowIndex, 0, visible.Count - 1);
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);

        _selectedItems.Clear();
        for (int i = lo; i <= hi; i++)
            _selectedItems.Add(visible[i]);

        _lastSelectedItem = visible[b];
        _lastSelectedRowIndex = ResolveRowIndex(visible[b], b);

        var opId = RecordGridDragGraph(
            subKind: "RowRangeSelect",
            sourceLabel: $"Row {a}",
            targetLabel: $"Row {b}",
            selectedRows: _selectedItems.Count,
            dragSpanRows: hi - lo + 1);

        await NotifySelectionChangedAsync(GridSelectionChangeSource.MouseDrag);
        await InvokeAsync(StateHasChanged);
        return opId;
    }

    /// <summary>Programmatically reorders a row from <paramref name="oldIndex"/> to <paramref name="newIndex"/> and logs a <c>Grid.Drag</c> graph.</summary>
    public async Task<string> DragReorderRowAsync(int oldIndex, int newIndex)
    {
        if (DataSource is not IList<TValue> list || list.Count < 2)
            return string.Empty;

        int from = Math.Clamp(oldIndex, 0, list.Count - 1);
        int to = Math.Clamp(newIndex, 0, list.Count - 1);
        if (from == to)
            return string.Empty;

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        InvalidateBlazorServerOptimizationCaches();

        _selectedItems.Clear();
        _selectedItems.Add(item);
        _lastSelectedItem = item;
        _lastSelectedRowIndex = to;

        var opId = RecordGridDragGraph(
            subKind: "RowReorder",
            sourceLabel: $"Row {from}",
            targetLabel: $"Row {to}",
            selectedRows: 1,
            dragSpanRows: Math.Abs(to - from));

        await NotifySelectionChangedAsync(GridSelectionChangeSource.Pointer);
        await InvokeAsync(StateHasChanged);
        return opId;
    }

    // ── 4. Grid Multi-Select Batch Edit Custom Graph Telemetry ───────────────

    internal string RecordGridMultiSelectEditGraph(
        string editMode,
        string field,
        object? newValue,
        int selectedRowCount,
        int selectedCellCount,
        int targetRowCount,
        int updatedCellCount)
    {
        var gridId = ResolveControlIdentifier() ?? typeof(TValue).Name;
        var isSensitive = field.Contains("Password", StringComparison.OrdinalIgnoreCase)
                          || field.Contains("Secret", StringComparison.OrdinalIgnoreCase);
        var safeVal = isSensitive ? "[REDACTED]" : TruncateLabel(newValue?.ToString() ?? string.Empty, 48);
        int effSelected = Math.Max(selectedRowCount, Math.Max(selectedCellCount, targetRowCount));
        var parentDragOp = ConsumeRecentDragSelectionOpId();

        return RecordCustomAnalytics(FlexCustomAnalyticsEvents.GridMultiSelectEdit, "BatchEdit", b =>
        {
            b.WithSubKind(editMode)
             .WithParentOperation(parentDragOp)
             .WithOutcome("BatchCommitted")
             .WithValue($"{field}={safeVal} ({updatedCellCount} cells / {effSelected} selected)")
             .WithSummary($"Multi-Select Edit ({editMode}) on '{gridId}.{field}' = '{safeVal}' across {updatedCellCount:N0} of {effSelected:N0} selected rows")
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.SelectedRows, effSelected)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.SelectedCells, selectedCellCount)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.TargetRows, targetRowCount)
             .WithMetric(FlexCustomAnalyticsEvents.Metrics.UpdatedCells, updatedCellCount)
             .WithTag("editMode", editMode)
             .WithTag("field", field)
             .WithTag("linkedDrag", parentDragOp is not null ? "true" : "false");

            if (parentDragOp is not null)
            {
                b.AddNode("drag", "PriorDragSelect", $"Drag Select ({effSelected:N0} rows)", effSelected)
                 .AddNode("grid", "Grid", $"Grid:{gridId}", effSelected)
                 .AddEdge("drag", "grid", "chained-selection", effSelected);
            }
            else
            {
                b.AddNode("grid", "Grid", $"Grid:{gridId}", effSelected);
            }

            b.AddNode("selection", "MultiSelection", $"{effSelected:N0} Selected Rows", effSelected)
             .AddNode("column", "TargetColumn", $"Column: {field}", targetRowCount)
             .AddNode("commit", "BatchCommit", $"{updatedCellCount:N0} Updated ({editMode})", updatedCellCount)
             .AddEdge("grid", "selection", "selected", effSelected)
             .AddEdge("selection", "column", editMode, targetRowCount)
             .AddEdge("column", "commit", "applied", Math.Max(1, updatedCellCount));
        });
    }

    /// <summary>
    /// Applies <paramref name="newValue"/> to <paramref name="field"/> across all currently selected rows (or selected cells)
    /// and emits both <c>CellSaved</c> and a <c>Grid.MultiSelectEdit</c> interaction graph.
    /// </summary>
    public async Task<int> ApplyMultiSelectBatchEditAsync(string field, object? newValue, string editMode = "BatchToolbarApply")
    {
        if (string.IsNullOrWhiteSpace(field))
            return 0;

        var col = Columns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        var targets = _selectedItems.Count > 0
            ? _selectedItems.ToList()
            : _selectedCells
                .Select(c => GetItemAtResolvedRowIndex(c.RowIndex))
                .Where(item => item is not null)
                .Select(item => item!)
                .Distinct()
                .ToList();

        if (targets.Count == 0)
            return 0;

        int updated = 0;
        foreach (var target in targets)
        {
            if (col is not null && !IsCellEditableForItem(target, col))
                continue;

            bool setOk;
            if (newValue is string strVal)
            {
                SetPropertyValue(target, field, strVal);
                setOk = true;
            }
            else
            {
                setOk = SetPropertyObjectValue(target, field, newValue);
            }

            if (!setOk)
                continue;

            updated++;
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

        if (updated > 0)
        {
            LogGridCellSave(field, newValue, updated);
            RecordGridMultiSelectEditGraph(
                editMode: editMode,
                field: field,
                newValue: newValue,
                selectedRowCount: _selectedItems.Count,
                selectedCellCount: _selectedCells.Count,
                targetRowCount: targets.Count,
                updatedCellCount: updated);
            ClearPassViewMemos();
            InvalidateBlazorServerOptimizationCaches();
            await InvokeAsync(StateHasChanged);
        }

        return updated;
    }

    // ── Internal Helpers ─────────────────────────────────────────────────────

    private int SafeTotalRowCount()
    {
        try
        {
            if (DataSource is ICollection<TValue> coll)
                return coll.Count;
            if (DataSource is System.Collections.ICollection nonGeneric)
                return nonGeneric.Count;
            return DataSource?.Count() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool ItemMatchesGridFind(TValue item, List<GridColumn> cols, string scope, string wildcardPattern, bool matchCase)
    {
        if (item is null)
            return false;

        if (cols.Count > 0)
        {
            foreach (var col in cols)
            {
                var cellText = GetColumnSearchText(item, col);
                if (MatchesGridWildcard(cellText, wildcardPattern, matchCase))
                    return true;
            }
            return false;
        }

        if (scope != "AllColumns")
        {
            var raw = GetPropertyValue(item, scope)?.ToString();
            return MatchesGridWildcard(raw, wildcardPattern, matchCase);
        }

        var props = typeof(TValue).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (props.Length > 0)
        {
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    var val = p.GetValue(item)?.ToString();
                    if (MatchesGridWildcard(val, wildcardPattern, matchCase))
                        return true;
                }
                catch
                {
                }
            }
            return false;
        }

        return MatchesGridWildcard(item.ToString(), wildcardPattern, matchCase);
    }

    private static string BuildGridWildcardPattern(string findText, bool wholeWord)
    {
        var trimmed = findText.Trim();
        if (wholeWord)
            return $"* {trimmed} *";
        if (trimmed.Contains('*') || trimmed.Contains('?'))
            return trimmed.StartsWith('*') || trimmed.EndsWith('*') ? trimmed : $"*{trimmed}*";
        return $"*{trimmed}*";
    }

    private static bool MatchesGridWildcard(string? text, string pattern, bool matchCase)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var padded = " " + text + " ";
        var src = matchCase ? padded : padded.ToUpperInvariant();
        var pat = matchCase ? pattern : pattern.ToUpperInvariant();

        int tIdx = 0, pIdx = 0, tStar = -1, pStar = -1;
        while (tIdx < src.Length)
        {
            if (pIdx < pat.Length && (pat[pIdx] == '?' || pat[pIdx] == src[tIdx]))
            {
                tIdx++;
                pIdx++;
            }
            else if (pIdx < pat.Length && pat[pIdx] == '*')
            {
                pStar = pIdx++;
                tStar = tIdx;
            }
            else if (pStar >= 0)
            {
                pIdx = pStar + 1;
                tIdx = ++tStar;
            }
            else
            {
                return false;
            }
        }
        while (pIdx < pat.Length && pat[pIdx] == '*')
            pIdx++;
        return pIdx == pat.Length;
    }

    private static string TruncateLabel(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
}
