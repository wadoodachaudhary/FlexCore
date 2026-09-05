using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    // ── Filter Mode Parameters ──────────────────────────────────────────
    [Parameter] public GridFilterMode FilterMode { get; set; } = GridFilterMode.None;
    [Parameter] public GridDensity Density { get; set; } = GridDensity.Default;
    [Parameter] public bool ShowMultiColumnSortingIndex { get; set; }

    // ── Master-Detail Row Expansion Parameters ──────────────────────────
    [Parameter] public RenderFragment<TValue>? DetailTemplate { get; set; }
    [Parameter] public GridExpandMode ExpandMode { get; set; } = GridExpandMode.Multiple;
    [Parameter] public bool ShowExpandColumn { get; set; } = true;
    [Parameter] public bool ShowExpandAll { get; set; }
    [Parameter] public EventCallback<TValue> RowExpand { get; set; }
    [Parameter] public EventCallback<TValue> RowCollapse { get; set; }

    // ── Group Footer Template Parameters ────────────────────────────────
    [Parameter] public RenderFragment<object>? GroupFooterTemplate { get; set; }
    [Parameter] public bool GroupFootersAlwaysVisible { get; set; }

    // ── Master-Detail State ─────────────────────────────────────────────
    private HashSet<TValue>? _expandedRowsSet;
    private HashSet<TValue> _expandedRows =>
        _expandedRowsSet ??= new HashSet<TValue>(new GridItemIdentityComparer(this));
    public bool HasDetailTemplate => DetailTemplate != null;
    private bool ShowDetailExpandColumn => HasDetailTemplate && ShowExpandColumn;

    public bool AllRowsExpanded
    {
        get
        {
            var rows = GetExpandableDetailRows();
            return rows.Count > 0 && rows.All(r => _expandedRows.Contains(r));
        }
    }

    public bool IsRowExpanded(TValue item) => item != null && _expandedRows.Contains(item);

    public async Task ExpandRow(TValue item)
    {
        if (item == null) return;
        if (ExpandMode == GridExpandMode.Single)
            _expandedRows.Clear();

        if (_expandedRows.Add(item))
        {
            if (RowExpand.HasDelegate)
                await RowExpand.InvokeAsync(item);
            StateHasChanged();
            await NotifyGridStateChangedAsync(GridStateChangeKind.Expansion);
        }
    }

    public async Task CollapseRow(TValue item)
    {
        if (item == null) return;
        if (_expandedRows.Remove(item))
        {
            if (RowCollapse.HasDelegate)
                await RowCollapse.InvokeAsync(item);
            StateHasChanged();
            await NotifyGridStateChangedAsync(GridStateChangeKind.Expansion);
        }
    }

    public async Task ToggleRowExpand(TValue item)
    {
        if (IsRowExpanded(item))
            await CollapseRow(item);
        else
            await ExpandRow(item);
    }

    public void ExpandAll()
    {
        foreach (var row in GetExpandableDetailRows())
            _expandedRows.Add(row);
        StateHasChanged();
        _ = NotifyGridStateChangedAsync(GridStateChangeKind.Expansion);
    }

    public void CollapseAll()
    {
        _expandedRows.Clear();
        StateHasChanged();
        _ = NotifyGridStateChangedAsync(GridStateChangeKind.Expansion);
    }

    public void ToggleExpandAll()
    {
        if (AllRowsExpanded)
            CollapseAll();
        else
            ExpandAll();
    }

    private IReadOnlyList<TValue> GetExpandableDetailRows()
    {
        if (UsesProviderGrouping)
            return EnumerateLoadedProviderRows().ToList();

        return PagedData.ToList();
    }

    // ── In-Header Simple / Advanced Column Filter State ─────────────────
    private readonly Dictionary<string, string> _simpleColumnFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ColumnAdvancedFilterCriteria> _columnAdvancedFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _columnCheckboxFilters = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeFilterPopupField;

    public sealed class ColumnAdvancedFilterCriteria
    {
        public GridFilterOperator Operator1 { get; set; } = GridFilterOperator.Contains;
        public string? Value1 { get; set; }
        public LogicalFilterOperator LogicalOperator { get; set; } = LogicalFilterOperator.And;
        public GridFilterOperator Operator2 { get; set; } = GridFilterOperator.Contains;
        public string? Value2 { get; set; }
    }

    public string GetColumnFilterValue(string field)
    {
        if (_filterRowDrafts.TryGetValue(field, out var draft))
            return draft;
        return _simpleColumnFilters.TryGetValue(field, out var val) ? val : "";
    }

    public void OnColumnFilterInput(string field, string? val)
    {
        QueueFilterRowValue(field, val);
    }

    /// <summary>
    /// Compatibility wrapper for callers that used the original synchronous
    /// API. State is cleared before the first await; provider-backed callers
    /// can use <see cref="ClearColumnFilterAsync"/> when they need to await the
    /// refreshed range.
    /// </summary>
    public void ClearColumnFilter(string field) => _ = ClearColumnFilterAsync(field);

    /// <summary>
    /// Clears every filter surface for one column and refreshes the active
    /// ItemsProvider query when applicable.
    /// </summary>
    public async Task ClearColumnFilterAsync(string field)
    {
        ClearFilter(field);
        if (string.Equals(_activeFilterPopupField, field, StringComparison.OrdinalIgnoreCase))
            _activeFilterPopupField = null;
        if (string.Equals(_filterPopupField, field, StringComparison.OrdinalIgnoreCase))
            CloseFilterPopup();

        if (UsesItemsProvider)
            await ReloadItemsAsync();

        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    public void ToggleColumnFilterPopup(string field)
    {
        if (_activeFilterPopupField == field)
            _activeFilterPopupField = null;
        else
            _activeFilterPopupField = field;
        StateHasChanged();
    }

    public bool HasActiveColumnFilter(string field)
    {
        return GetColumnState(field).FilterActive
            || _simpleColumnFilters.ContainsKey(field)
            || (_columnAdvancedFilters.TryGetValue(field, out var criteria)
                && IsAdvancedFilterCriteriaActive(criteria))
            || _columnCheckboxFilters.ContainsKey(field);
    }

    public ColumnAdvancedFilterCriteria GetAdvancedFilterCriteria(string field)
    {
        if (!_columnAdvancedFilters.TryGetValue(field, out var crit))
        {
            crit = new ColumnAdvancedFilterCriteria();
            _columnAdvancedFilters[field] = crit;
        }
        return crit;
    }

    /// <summary>
    /// Compatibility wrapper for the original synchronous API. Provider hosts
    /// can await <see cref="ApplyAdvancedFilterAsync"/> to observe completion.
    /// </summary>
    public void ApplyAdvancedFilter(string field) => _ = ApplyAdvancedFilterAsync(field);

    /// <summary>Applies the staged advanced criteria and refreshes provider rows.</summary>
    public async Task ApplyAdvancedFilterAsync(string field)
    {
        _activeFilterPopupField = null;
        if (_columnAdvancedFilters.TryGetValue(field, out var criteria)
            && !IsAdvancedFilterCriteriaActive(criteria))
        {
            // GetAdvancedFilterCriteria stages a mutable object for the editor.
            // An untouched/cleared object is not an applied filter and should
            // not leave a filtered header indicator or disable fast paths.
            _columnAdvancedFilters.Remove(field);
        }
        _pageState.CurrentPage = 1;
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();

        if (UsesItemsProvider)
            await ReloadItemsAsync();

        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    public HashSet<string> GetDistinctColumnValues(string field)
    {
        var set = new HashSet<string>(FilterTextComparer);
        if (DataSource == null) return set;
        var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        if (col == null) return set;

        foreach (var item in DataSource)
        {
            var text = GetColumnSearchText(item, col);
            if (!string.IsNullOrEmpty(text))
                set.Add(text);
        }
        return set;
    }

    private bool PassesColumnFilters(TValue item)
    {
        // Simple in-header filters
        foreach (var kvp in _simpleColumnFilters)
        {
            var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (col == null) continue;
            if (!PassesTypedFilterRow(item, col, kvp.Value))
                return false;
        }

        // Advanced 2-condition popup filters
        foreach (var kvp in _columnAdvancedFilters)
        {
            var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (col == null) continue;
            var cellVal = GetColumnSearchText(item, col) ?? "";
            var crit = kvp.Value;
            if (!IsAdvancedFilterCriteriaActive(crit))
                continue;

            var hasFirstCondition = IsAdvancedFilterConditionActive(crit.Operator1, crit.Value1);
            var hasSecondCondition = IsAdvancedFilterConditionActive(crit.Operator2, crit.Value2);

            var passes = hasFirstCondition && hasSecondCondition
                ? crit.LogicalOperator == LogicalFilterOperator.And
                    ? EvaluateFilterCondition(cellVal, crit.Operator1, crit.Value1)
                        && EvaluateFilterCondition(cellVal, crit.Operator2, crit.Value2)
                    : EvaluateFilterCondition(cellVal, crit.Operator1, crit.Value1)
                        || EvaluateFilterCondition(cellVal, crit.Operator2, crit.Value2)
                : hasFirstCondition
                    ? EvaluateFilterCondition(cellVal, crit.Operator1, crit.Value1)
                    : EvaluateFilterCondition(cellVal, crit.Operator2, crit.Value2);
            if (!passes)
                return false;
        }

        // CheckBoxList filters
        foreach (var kvp in _columnCheckboxFilters)
        {
            var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (col == null) continue;
            var cellVal = GetColumnSearchText(item, col) ?? "";
            if (!kvp.Value.Any(value => string.Equals(value, cellVal, FilterTextComparison)))
                return false;
        }

        return true;
    }

    private static bool IsAdvancedFilterCriteriaActive(ColumnAdvancedFilterCriteria? criteria) =>
        criteria is not null
        && (IsAdvancedFilterConditionActive(criteria.Operator1, criteria.Value1)
            || IsAdvancedFilterConditionActive(criteria.Operator2, criteria.Value2));

    private static bool IsAdvancedFilterConditionActive(GridFilterOperator filterOperator, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        || IsValueOptionalAdvancedFilterOperator(filterOperator);

    private bool EvaluateFilterCondition(string cellVal, GridFilterOperator op, string? filterVal)
    {
        filterVal ??= "";
        return op switch
        {
            GridFilterOperator.Equals => string.Equals(cellVal, filterVal, FilterTextComparison),
            GridFilterOperator.NotEquals => !string.Equals(cellVal, filterVal, FilterTextComparison),
            GridFilterOperator.Contains => cellVal.Contains(filterVal, FilterTextComparison),
            GridFilterOperator.DoesNotContain => !cellVal.Contains(filterVal, FilterTextComparison),
            GridFilterOperator.StartsWith => cellVal.StartsWith(filterVal, FilterTextComparison),
            GridFilterOperator.EndsWith => cellVal.EndsWith(filterVal, FilterTextComparison),
            GridFilterOperator.IsNull or GridFilterOperator.IsEmpty => string.IsNullOrEmpty(cellVal),
            GridFilterOperator.IsNotNull or GridFilterOperator.IsNotEmpty => !string.IsNullOrEmpty(cellVal),
            GridFilterOperator.GreaterThan when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn > fn,
            GridFilterOperator.GreaterThanOrEquals when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn >= fn,
            GridFilterOperator.LessThan when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn < fn,
            GridFilterOperator.LessThanOrEquals when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn <= fn,
            _ => cellVal.Contains(filterVal, FilterTextComparison)
        };
    }

    // ── Multi-Column Sorting Priority Index ─────────────────────────────
    public int? GetSortPriorityIndex(string field)
    {
        var sorts = GetActiveSortDescriptors();
        if (sorts.Length <= 1) return null;
        var idx = Array.FindIndex(sorts, sort =>
            string.Equals(sort.Field, field, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx + 1 : null;
    }

}
