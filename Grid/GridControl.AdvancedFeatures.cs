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
    private readonly HashSet<TValue> _expandedRows = new();
    public bool HasDetailTemplate => DetailTemplate != null;
    public bool AllRowsExpanded => PagedData.Any() && PagedData.All(r => _expandedRows.Contains(r));

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
        foreach (var row in PagedData)
            _expandedRows.Add(row);
        StateHasChanged();
    }

    public void CollapseAll()
    {
        _expandedRows.Clear();
        StateHasChanged();
    }

    public void ToggleExpandAll()
    {
        if (AllRowsExpanded)
            CollapseAll();
        else
            ExpandAll();
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
        return _simpleColumnFilters.TryGetValue(field, out var val) ? val : "";
    }

    public void OnColumnFilterInput(string field, string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
            _simpleColumnFilters.Remove(field);
        else
            _simpleColumnFilters[field] = val;

        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        _pageState.CurrentPage = 1;
        StateHasChanged();
    }

    public void ClearColumnFilter(string field)
    {
        _simpleColumnFilters.Remove(field);
        _columnAdvancedFilters.Remove(field);
        _columnCheckboxFilters.Remove(field);
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        _pageState.CurrentPage = 1;
        StateHasChanged();
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
        return _simpleColumnFilters.ContainsKey(field)
            || _columnAdvancedFilters.ContainsKey(field)
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

    public void ApplyAdvancedFilter(string field)
    {
        _activeFilterPopupField = null;
        _pageState.CurrentPage = 1;
        StateHasChanged();
    }

    public HashSet<string> GetDistinctColumnValues(string field)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var cellVal = GetColumnSearchText(item, col) ?? "";
            if (!cellVal.Contains(kvp.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Advanced 2-condition popup filters
        foreach (var kvp in _columnAdvancedFilters)
        {
            var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (col == null) continue;
            var cellVal = GetColumnSearchText(item, col) ?? "";
            var crit = kvp.Value;

            bool cond1 = EvaluateFilterCondition(cellVal, crit.Operator1, crit.Value1);
            if (string.IsNullOrEmpty(crit.Value2) && crit.Operator2 is not GridFilterOperator.IsNull and not GridFilterOperator.IsNotNull and not GridFilterOperator.IsEmpty and not GridFilterOperator.IsNotEmpty)
            {
                if (!cond1) return false;
            }
            else
            {
                bool cond2 = EvaluateFilterCondition(cellVal, crit.Operator2, crit.Value2);
                bool overall = crit.LogicalOperator == LogicalFilterOperator.And ? (cond1 && cond2) : (cond1 || cond2);
                if (!overall) return false;
            }
        }

        // CheckBoxList filters
        foreach (var kvp in _columnCheckboxFilters)
        {
            var col = VisibleColumns.FirstOrDefault(c => string.Equals(c.Field, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (col == null) continue;
            var cellVal = GetColumnSearchText(item, col) ?? "";
            if (!kvp.Value.Contains(cellVal))
                return false;
        }

        return true;
    }

    private static bool EvaluateFilterCondition(string cellVal, GridFilterOperator op, string? filterVal)
    {
        filterVal ??= "";
        return op switch
        {
            GridFilterOperator.Equals => string.Equals(cellVal, filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.NotEquals => !string.Equals(cellVal, filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.Contains => cellVal.Contains(filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.DoesNotContain => !cellVal.Contains(filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.StartsWith => cellVal.StartsWith(filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.EndsWith => cellVal.EndsWith(filterVal, StringComparison.OrdinalIgnoreCase),
            GridFilterOperator.IsNull or GridFilterOperator.IsEmpty => string.IsNullOrEmpty(cellVal),
            GridFilterOperator.IsNotNull or GridFilterOperator.IsNotEmpty => !string.IsNullOrEmpty(cellVal),
            GridFilterOperator.GreaterThan when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn > fn,
            GridFilterOperator.GreaterThanOrEquals when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn >= fn,
            GridFilterOperator.LessThan when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn < fn,
            GridFilterOperator.LessThanOrEquals when double.TryParse(cellVal, out var cn) && double.TryParse(filterVal, out var fn) => cn <= fn,
            _ => cellVal.Contains(filterVal, StringComparison.OrdinalIgnoreCase)
        };
    }

    // ── Multi-Column Sorting Priority Index ─────────────────────────────
    public int? GetSortPriorityIndex(string field)
    {
        var sortedColumns = _columnStates.Where(kvp => kvp.Value.SortDirection.HasValue).Select(kvp => kvp.Key).ToList();
        if (sortedColumns.Count <= 1) return null;
        var idx = sortedColumns.IndexOf(field);
        return idx >= 0 ? idx + 1 : null;
    }

}
