using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.Reflection;

namespace Fx.ControlKit.Grid;

public partial class PivotControl<TValue>
{

    [Parameter] public IEnumerable<TValue> DataSource { get; set; } = Enumerable.Empty<TValue>();
    [Parameter] public IReadOnlyList<string> RowFields { get; set; } = Array.Empty<string>();
    [Parameter] public IReadOnlyList<string> ColumnFields { get; set; } = Array.Empty<string>();
    [Parameter] public string ValueField { get; set; } = "";
    [Parameter] public AggregateType Aggregation { get; set; } = AggregateType.Sum;
    [Parameter] public bool ShowGrandTotals { get; set; } = true;
    [Parameter] public bool ShowSubTotals { get; set; } = true;
    [Parameter] public string ValueFormat { get; set; } = "";
    [Parameter] public string Height { get; set; } = "420px";
    [Parameter] public string Width { get; set; } = "";
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public string CssClass { get; set; } = "";
    [Parameter] public string Style { get; set; } = "";

    [Parameter] public bool Interactive { get; set; }

    [Parameter] public IReadOnlyList<PivotValueConfig>? ValueFields { get; set; }

    [Parameter] public Dictionary<string, string>? FieldLabels { get; set; }

    private string ResolvedStyle => Style;

    private readonly List<HeaderLevel> _headerLevels = new();
    private readonly List<PivotDisplayRow> _displayRows = new();
    private readonly List<PivotValueColumn> _valueColumns = new();
    private string? _sortField;
    private bool _sortAscending = true;

    private List<string> _allDiscoveredFields = new();
    private readonly List<string> _rowFieldsActive = new();
    private readonly List<string> _columnFieldsActive = new();
    private readonly List<PivotValueConfig> _valueFieldsActive = new();
    private readonly List<string> _filterFieldsActive = new();
    private readonly List<PivotFilterState> _activeFilters = new();

    private string? _dragField;
    private PivotArea _dragSourceArea;
    private PivotArea? _dragOverArea;

    private IReadOnlyList<string> _effectiveRowFields => Interactive ? _rowFieldsActive : RowFields;
    private IReadOnlyList<string> _effectiveColumnFields => Interactive ? _columnFieldsActive : ColumnFields;

    private IReadOnlyList<PivotValueConfig> _effectiveValueFields
    {
        get
        {
            if (Interactive) return _valueFieldsActive;
            if (ValueFields is { Count: > 0 }) return ValueFields;
            if (!string.IsNullOrWhiteSpace(ValueField))
                return new[] { new PivotValueConfig { Field = ValueField, Aggregation = Aggregation, Format = ValueFormat } };
            return Array.Empty<PivotValueConfig>();
        }
    }

    private List<string> _unplacedFields =>
        _allDiscoveredFields
            .Where(f => !_rowFieldsActive.Contains(f) &&
                        !_columnFieldsActive.Contains(f) &&
                        !_valueFieldsActive.Any(v => v.Field == f) &&
                        !_filterFieldsActive.Contains(f))
            .ToList();

    private bool _initialized;

    protected override void OnParametersSet()
    {
        if (Interactive && !_initialized)
        {
            DiscoverFields();
            SeedInteractiveState();
            _initialized = true;
        }

        BuildPivot();
    }

    private void DiscoverFields()
    {
        _allDiscoveredFields.Clear();
        var type = typeof(TValue);

        if (typeof(IDictionary<string, object>).IsAssignableFrom(type))
        {
            var first = DataSource.FirstOrDefault();
            if (first is IDictionary<string, object> dict)
                _allDiscoveredFields.AddRange(dict.Keys);
        }
        else
        {
            _allDiscoveredFields.AddRange(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead
                                && !p.Name.EndsWith("Formula", StringComparison.Ordinal))
                    .Select(p => p.Name));
        }
    }

    private void SeedInteractiveState()
    {
        _rowFieldsActive.Clear();
        _columnFieldsActive.Clear();
        _valueFieldsActive.Clear();
        _filterFieldsActive.Clear();

        foreach (var f in RowFields)
            if (_allDiscoveredFields.Contains(f)) _rowFieldsActive.Add(f);
        foreach (var f in ColumnFields)
            if (_allDiscoveredFields.Contains(f)) _columnFieldsActive.Add(f);

        if (ValueFields is { Count: > 0 })
        {
            foreach (var vc in ValueFields)
                _valueFieldsActive.Add(new PivotValueConfig
                    { Field = vc.Field, Aggregation = vc.Aggregation, Format = vc.Format, Label = vc.Label });
        }
        else if (!string.IsNullOrWhiteSpace(ValueField) && _allDiscoveredFields.Contains(ValueField))
        {
            _valueFieldsActive.Add(new PivotValueConfig
                { Field = ValueField, Aggregation = Aggregation, Format = ValueFormat });
        }

        RebuildFilters();
    }

    private void OnDragStart(string field, PivotArea source)
    {
        _dragField = field;
        _dragSourceArea = source;
    }

    private void OnDragEnd()
    {
        _dragField = null;
        _dragOverArea = null;
    }

    private void OnDragOverZone(PivotArea area)
    {
        _dragOverArea = area;
    }

    private void OnDragLeaveZone()
    {
        _dragOverArea = null;
    }

    private void OnDropZone(PivotArea area)
    {
        if (string.IsNullOrEmpty(_dragField)) return;
        var field = _dragField;
        _dragField = null;
        _dragOverArea = null;

        if (_dragSourceArea != PivotArea.None)
            RemoveFieldSilent(field, _dragSourceArea);

        switch (area)
        {
            case PivotArea.Row:
                if (!_rowFieldsActive.Contains(field)) _rowFieldsActive.Add(field);
                break;
            case PivotArea.Column:
                if (!_columnFieldsActive.Contains(field)) _columnFieldsActive.Add(field);
                break;
            case PivotArea.Value:
                if (!_valueFieldsActive.Any(v => v.Field == field))
                    _valueFieldsActive.Add(new PivotValueConfig { Field = field, Format = ValueFormat });
                break;
            case PivotArea.Filter:
                if (!_filterFieldsActive.Contains(field)) _filterFieldsActive.Add(field);
                break;
        }

        RebuildFilters();
        BuildPivot();
    }

    private void RemoveField(string field, PivotArea area)
    {
        RemoveFieldSilent(field, area);
        RebuildFilters();
        BuildPivot();
    }

    private void RemoveValueField(PivotValueConfig vc)
    {
        _valueFieldsActive.Remove(vc);
        BuildPivot();
    }

    private void RemoveFieldSilent(string field, PivotArea area)
    {
        switch (area)
        {
            case PivotArea.Row: _rowFieldsActive.Remove(field); break;
            case PivotArea.Column: _columnFieldsActive.Remove(field); break;
            case PivotArea.Value: _valueFieldsActive.RemoveAll(v => v.Field == field); break;
            case PivotArea.Filter:
                _filterFieldsActive.Remove(field);
                _activeFilters.RemoveAll(f => f.Field == field);
                break;
        }
    }

    private void ChangeAggregation(PivotValueConfig vc, ChangeEventArgs e)
    {
        if (Enum.TryParse<AggregateType>(e.Value?.ToString(), out var agg))
        {
            vc.Aggregation = agg;
            vc.Label = null; // reset to auto-label
            BuildPivot();
        }
    }

    private void ChangeValueFormat(PivotValueConfig vc, ChangeEventArgs e)
    {
        vc.Format = e.Value?.ToString() ?? string.Empty;
        BuildPivot();
    }

    private void RebuildFilters()
    {
        var rows = DataSource.ToList();
        var existing = _activeFilters.ToDictionary(f => f.Field, f => f.SelectedValue);
        _activeFilters.Clear();

        foreach (var field in _filterFieldsActive)
        {
            var uniqueValues = rows
                .Select(item => Convert.ToString(GetValue(item!, field), CultureInfo.CurrentCulture) ?? "")
                .Where(v => v.Length > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            existing.TryGetValue(field, out var prev);
            _activeFilters.Add(new PivotFilterState
            {
                Field = field,
                UniqueValues = uniqueValues,
                SelectedValue = uniqueValues.Contains(prev ?? "") ? prev : null
            });
        }
    }

    private void OnFilterChanged(string field, ChangeEventArgs e)
    {
        var filter = _activeFilters.FirstOrDefault(f => f.Field == field);
        if (filter != null)
        {
            var val = e.Value?.ToString();
            filter.SelectedValue = string.IsNullOrEmpty(val) ? null : val;
            BuildPivot();
        }
    }

    private IEnumerable<TValue> GetFilteredData()
    {
        IEnumerable<TValue> data = DataSource;
        foreach (var filter in _activeFilters.Where(f => f.SelectedValue != null))
        {
            var field = filter.Field;
            var selected = filter.SelectedValue!;
            data = data.Where(item =>
                string.Equals(
                    Convert.ToString(GetValue(item!, field), CultureInfo.CurrentCulture) ?? "",
                    selected, StringComparison.OrdinalIgnoreCase));
        }
        return data;
    }

    private void BuildPivot()
    {
        _headerLevels.Clear();
        _displayRows.Clear();
        _valueColumns.Clear();

        var effectiveValues = _effectiveValueFields;
        var effectiveRows = _effectiveRowFields;
        var effectiveCols = _effectiveColumnFields;

        var rows = GetFilteredData().ToList();
        if (rows.Count == 0 || effectiveRows.Count == 0 || effectiveValues.Count == 0)
            return;

        var columnKeys = effectiveCols.Count == 0
            ? new List<PivotKey> { new("Value", new[] { "Value" }) }
            : rows.Select(item => BuildKey(item, effectiveCols))
                  .Distinct()
                  .OrderBy(key => key.Display)
                  .ToList();

        foreach (var key in columnKeys)
        {
            foreach (var vc in effectiveValues)
            {
                var suffix = effectiveValues.Count > 1 ? $" ({vc.ShortLabel})" : "";
                var header = effectiveCols.Count == 0 ? vc.DisplayLabel : $"{key.Display}{suffix}";
                var fieldName = MakeFieldName($"{key.Display}_{vc.Field}_{vc.Aggregation}");
                _valueColumns.Add(new PivotValueColumn(fieldName, header, key, vc));
            }
        }

        if (ShowGrandTotals)
        {
            foreach (var vc in effectiveValues)
            {
                var label = effectiveValues.Count > 1 ? $"Total ({vc.ShortLabel})" : "Total";
                _valueColumns.Add(new PivotValueColumn($"__Total_{vc.Field}_{vc.Aggregation}", label,
                    PivotKey.Empty, vc, true));
            }
        }

        BuildHeaders();

        var rowGroups = rows
            .GroupBy(item => BuildKey(item, effectiveRows))
            .OrderBy(group => group.Key.Display)
            .ToList();

        if (effectiveRows.Count > 1)
            BuildGroupedRows(rows, rowGroups);
        else
            BuildFlatRows(rows, rowGroups);
    }

    private void BuildHeaders()
    {
        var rowFieldCount = _effectiveRowFields.Count;
        var effectiveCols = _effectiveColumnFields;
        var effectiveValues = _effectiveValueFields;
        var hasMultiColumnLevels = effectiveCols.Count > 1;
        var hasMultiValueFields = effectiveValues.Count > 1;
        var totalHeaderRows = 1 + (hasMultiColumnLevels ? 1 : 0) + (hasMultiValueFields && effectiveCols.Count > 0 ? 1 : 0);

        if (effectiveCols.Count > 1 && hasMultiValueFields)
        {
            BuildThreeLevelHeaders(rowFieldCount);
        }
        else if (effectiveCols.Count > 1)
        {
            BuildTwoLevelHeaders(rowFieldCount);
        }
        else if (hasMultiValueFields && effectiveCols.Count == 1)
        {
            BuildColValueHeaders(rowFieldCount);
        }
        else
        {
            BuildSingleLevelHeaders(rowFieldCount);
        }
    }

    private void BuildSingleLevelHeaders(int rowFieldCount)
    {
        var level = new HeaderLevel();
        for (var i = 0; i < rowFieldCount; i++)
            level.Add(new HeaderCell(GetFieldLabel(_effectiveRowFields[i]),
                cssClass: "fx-pivot-hdr-row", sortField: _effectiveRowFields[i], isSortable: AllowSorting));

        foreach (var col in _valueColumns)
            level.Add(new HeaderCell(col.Header,
                cssClass: col.IsTotal ? "fx-pivot-hdr-total" : "fx-pivot-hdr-value",
                sortField: col.Field, isSortable: AllowSorting));

        _headerLevels.Add(level);
    }

    private void BuildTwoLevelHeaders(int rowFieldCount)
    {
        var topLevel = new HeaderLevel();
        var bottomLevel = new HeaderLevel();

        for (var i = 0; i < rowFieldCount; i++)
            topLevel.Add(new HeaderCell(GetFieldLabel(_effectiveRowFields[i]),
                rowspan: 2, cssClass: "fx-pivot-hdr-row"));

        var groups = _valueColumns.Where(c => !c.IsTotal)
            .GroupBy(c => c.Key.Parts.Count > 0 ? c.Key.Parts[0] : "")
            .OrderBy(g => g.Key).ToList();

        foreach (var group in groups)
        {
            var items = group.ToList();
            topLevel.Add(new HeaderCell(group.Key, colspan: items.Count, cssClass: "fx-pivot-hdr-colgroup"));
            foreach (var item in items)
            {
                var leafLabel = item.Key.Parts.Count > 1
                    ? string.Join(" / ", item.Key.Parts.Skip(1))
                    : item.Header;
                bottomLevel.Add(new HeaderCell(leafLabel,
                    cssClass: "fx-pivot-hdr-value", sortField: item.Field, isSortable: AllowSorting));
            }
        }

        var totalCols = _valueColumns.Where(c => c.IsTotal).ToList();
        if (totalCols.Count > 0)
            topLevel.Add(new HeaderCell("Total", rowspan: 2, colspan: totalCols.Count,
                cssClass: "fx-pivot-hdr-total"));

        _headerLevels.Add(topLevel);
        _headerLevels.Add(bottomLevel);
    }

    private void BuildColValueHeaders(int rowFieldCount)
    {
        var topLevel = new HeaderLevel();
        var bottomLevel = new HeaderLevel();

        for (var i = 0; i < rowFieldCount; i++)
            topLevel.Add(new HeaderCell(GetFieldLabel(_effectiveRowFields[i]),
                rowspan: 2, cssClass: "fx-pivot-hdr-row"));

        var groups = _valueColumns.Where(c => !c.IsTotal)
            .GroupBy(c => c.Key.Display)
            .OrderBy(g => g.Key).ToList();

        foreach (var group in groups)
        {
            var items = group.ToList();
            topLevel.Add(new HeaderCell(group.Key, colspan: items.Count, cssClass: "fx-pivot-hdr-colgroup"));
            foreach (var item in items)
                bottomLevel.Add(new HeaderCell(item.ValueConfig.ShortLabel,
                    cssClass: "fx-pivot-hdr-value", sortField: item.Field, isSortable: AllowSorting));
        }

        var totalCols = _valueColumns.Where(c => c.IsTotal).ToList();
        if (totalCols.Count > 0)
        {
            topLevel.Add(new HeaderCell("Total", colspan: totalCols.Count, rowspan: totalCols.Count > 1 ? 1 : 2,
                cssClass: "fx-pivot-hdr-total"));
            if (totalCols.Count > 1)
                foreach (var tc in totalCols)
                    bottomLevel.Add(new HeaderCell(tc.ValueConfig.ShortLabel,
                        cssClass: "fx-pivot-hdr-total"));
        }

        _headerLevels.Add(topLevel);
        _headerLevels.Add(bottomLevel);
    }

    private void BuildThreeLevelHeaders(int rowFieldCount)
    {
        var top = new HeaderLevel();
        var mid = new HeaderLevel();
        var bot = new HeaderLevel();

        for (var i = 0; i < rowFieldCount; i++)
            top.Add(new HeaderCell(GetFieldLabel(_effectiveRowFields[i]),
                rowspan: 3, cssClass: "fx-pivot-hdr-row"));

        var topGroups = _valueColumns.Where(c => !c.IsTotal)
            .GroupBy(c => c.Key.Parts.Count > 0 ? c.Key.Parts[0] : "")
            .OrderBy(g => g.Key).ToList();

        foreach (var tg in topGroups)
        {
            var topItems = tg.ToList();
            top.Add(new HeaderCell(tg.Key, colspan: topItems.Count, cssClass: "fx-pivot-hdr-colgroup"));

            var midGroups = topItems
                .GroupBy(c => c.Key.Parts.Count > 1 ? c.Key.Parts[1] : "")
                .OrderBy(g => g.Key).ToList();

            foreach (var mg in midGroups)
            {
                var midItems = mg.ToList();
                mid.Add(new HeaderCell(mg.Key, colspan: midItems.Count, cssClass: "fx-pivot-hdr-colgroup2"));
                foreach (var item in midItems)
                    bot.Add(new HeaderCell(item.ValueConfig.ShortLabel,
                        cssClass: "fx-pivot-hdr-value", sortField: item.Field, isSortable: AllowSorting));
            }
        }

        var totalCols = _valueColumns.Where(c => c.IsTotal).ToList();
        if (totalCols.Count > 0)
        {
            top.Add(new HeaderCell("Total", rowspan: totalCols.Count > 1 ? 2 : 3,
                colspan: totalCols.Count, cssClass: "fx-pivot-hdr-total"));
            if (totalCols.Count > 1)
                foreach (var tc in totalCols)
                    bot.Add(new HeaderCell(tc.ValueConfig.ShortLabel, cssClass: "fx-pivot-hdr-total"));
        }

        _headerLevels.Add(top);
        if (mid.Count > 0) _headerLevels.Add(mid);
        _headerLevels.Add(bot);
    }

    private void BuildGroupedRows(List<TValue> allRows, List<IGrouping<PivotKey, TValue>> rowGroups)
    {
        var firstFieldGroups = rowGroups
            .GroupBy(rg => rg.Key.Parts.Count > 0 ? rg.Key.Parts[0] : "")
            .OrderBy(g => g.Key).ToList();

        foreach (var group in firstFieldGroups)
        {
            var groupRows = group.ToList();
            var isFirstInGroup = true;

            foreach (var rowGroup in groupRows)
            {
                var row = new PivotDisplayRow("fx-pivot-data-row");
                var cells = new List<PivotDisplayCell>();

                if (isFirstInGroup)
                {
                    var span = ShowSubTotals ? groupRows.Count + 1 : groupRows.Count;
                    cells.Add(new PivotDisplayCell(group.Key, cssClass: "fx-pivot-cell-rowgroup", rowspan: span));
                    isFirstInGroup = false;
                }
                else
                {
                    cells.Add(new PivotDisplayCell("", rowspan: 0));
                }

                for (var i = 1; i < _effectiveRowFields.Count; i++)
                {
                    var part = i < rowGroup.Key.Parts.Count ? rowGroup.Key.Parts[i] : "";
                    cells.Add(new PivotDisplayCell(part, cssClass: "fx-pivot-cell-rowfield"));
                }

                AddValueCells(cells, rowGroup, false);
                row.Cells = cells;
                _displayRows.Add(row);
            }

            if (ShowSubTotals)
            {
                var subtotalRow = new PivotDisplayRow("fx-pivot-subtotal-row");
                var subtotalCells = new List<PivotDisplayCell>();
                subtotalCells.Add(new PivotDisplayCell("", rowspan: 0));
                subtotalCells.Add(new PivotDisplayCell($"{group.Key} Total",
                    cssClass: "fx-pivot-cell-subtotal-label", colspan: _effectiveRowFields.Count - 1));

                var groupItems = groupRows.SelectMany(rg => rg).ToList();
                AddValueCells(subtotalCells, groupItems, true);
                subtotalRow.Cells = subtotalCells;
                _displayRows.Add(subtotalRow);
            }
        }

        if (ShowGrandTotals)
            AddGrandTotalRow(allRows);
    }

    private void BuildFlatRows(List<TValue> allRows, List<IGrouping<PivotKey, TValue>> rowGroups)
    {
        foreach (var rowGroup in rowGroups)
        {
            var row = new PivotDisplayRow("fx-pivot-data-row");
            var cells = new List<PivotDisplayCell>();
            for (var i = 0; i < _effectiveRowFields.Count; i++)
            {
                var part = i < rowGroup.Key.Parts.Count ? rowGroup.Key.Parts[i] : "";
                cells.Add(new PivotDisplayCell(part, cssClass: "fx-pivot-cell-rowfield"));
            }
            AddValueCells(cells, rowGroup, false);
            row.Cells = cells;
            _displayRows.Add(row);
        }

        if (ShowGrandTotals)
            AddGrandTotalRow(allRows);
    }

    private void AddValueCells(List<PivotDisplayCell> cells, IEnumerable<TValue> items, bool isSubtotal)
    {
        var itemList = items.ToList();
        var effectiveCols = _effectiveColumnFields;

        foreach (var column in _valueColumns.Where(c => !c.IsTotal))
        {
            var matching = effectiveCols.Count == 0
                ? itemList
                : itemList.Where(item => BuildKey(item, effectiveCols).Equals(column.Key)).ToList();
            var val = Aggregate(matching, column.ValueConfig);
            var css = isSubtotal ? "fx-pivot-cell-subtotal" : "fx-pivot-cell-value";
            cells.Add(new PivotDisplayCell(FormatValue(val, column.ValueConfig), cssClass: css, rawValue: val));
        }

        foreach (var column in _valueColumns.Where(c => c.IsTotal))
        {
            var val = Aggregate(itemList, column.ValueConfig);
            var css = isSubtotal ? "fx-pivot-cell-subtotal-grand" : "fx-pivot-cell-total";
            cells.Add(new PivotDisplayCell(FormatValue(val, column.ValueConfig), cssClass: css, rawValue: val));
        }
    }

    private void AddGrandTotalRow(List<TValue> allRows)
    {
        var totalRow = new PivotDisplayRow("fx-pivot-grand-total-row");
        var cells = new List<PivotDisplayCell>();
        cells.Add(new PivotDisplayCell("Grand Total", cssClass: "fx-pivot-cell-grand-label",
            colspan: _effectiveRowFields.Count));

        var effectiveCols = _effectiveColumnFields;
        foreach (var column in _valueColumns.Where(c => !c.IsTotal))
        {
            var matching = effectiveCols.Count == 0
                ? allRows
                : allRows.Where(item => BuildKey(item, effectiveCols).Equals(column.Key)).ToList();
            var val = Aggregate(matching, column.ValueConfig);
            cells.Add(new PivotDisplayCell(FormatValue(val, column.ValueConfig),
                cssClass: "fx-pivot-cell-grand-value", rawValue: val));
        }

        foreach (var column in _valueColumns.Where(c => c.IsTotal))
        {
            var val = Aggregate(allRows, column.ValueConfig);
            cells.Add(new PivotDisplayCell(FormatValue(val, column.ValueConfig),
                cssClass: "fx-pivot-cell-grand-total", rawValue: val));
        }

        totalRow.Cells = cells;
        _displayRows.Add(totalRow);
    }

    private void HandleHeaderClick(HeaderCell cell)
    {
        if (!AllowSorting || !cell.IsSortable || string.IsNullOrEmpty(cell.SortField)) return;
        _sortAscending = _sortField == cell.SortField ? !_sortAscending : true;
        _sortField = cell.SortField;
        BuildPivot();
    }

    private object Aggregate(IEnumerable<TValue> items, PivotValueConfig cfg)
    {
        var itemList = items.ToList();
        if (cfg.Aggregation == AggregateType.Count)
            return itemList.Count;

        var values = itemList
            .Select(item => ToDoubleOrNaN(GetValue(item!, cfg.Field)))
            .Where(v => !double.IsNaN(v)).ToList();

        return cfg.Aggregation switch
        {
            AggregateType.Average => values.Count == 0 ? (object)"" : values.Average(),
            AggregateType.Min => values.Count == 0 ? (object)"" : values.Min(),
            AggregateType.Max => values.Count == 0 ? (object)"" : values.Max(),
            _ => values.Sum()
        };
    }

    private static string FormatValue(object? val, PivotValueConfig cfg)
    {
        if (val == null)
            return "";

        if (val is IFormattable formattable && !string.IsNullOrWhiteSpace(cfg.Format))
            return formattable.ToString(cfg.Format, CultureInfo.CurrentCulture);

        return IsNumericValue(val)
            ? FormatPlainNumber(val)
            : val.ToString() ?? "";
    }

    private static bool IsNumericValue(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

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

        return value.ToString() ?? "";
    }

    private string GetFieldLabel(string field) =>
        FieldLabels != null && FieldLabels.TryGetValue(field, out var label) ? label : field;

    private PivotKey BuildKey(TValue item, IReadOnlyList<string> fields)
    {
        var parts = fields
            .Select(f => Convert.ToString(GetValue(item!, f), CultureInfo.CurrentCulture) ?? "")
            .ToArray();
        return new PivotKey(string.Join(" / ", parts), parts);
    }

    private static object GetValue(object item, string field)
    {
        if (item == null || string.IsNullOrWhiteSpace(field)) return "";
        if (item is IDictionary<string, object> dict)
        {
            if (dict.TryGetValue(field, out var value)) return value;
            var match = dict.FirstOrDefault(kvp =>
                string.Equals(kvp.Key, field, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(match.Key) ? "" : match.Value;
        }
        return item.GetType()
            .GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(item) ?? "";
    }

    private static double ToDoubleOrNaN(object value)
    {
        if (value == null || value == DBNull.Value) return double.NaN;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return double.NaN; }
    }

    private static string MakeFieldName(string label)
    {
        var text = new string(label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(text) ? "Value" : $"P_{text}";
    }

    internal enum PivotArea { None, Row, Column, Value, Filter }

    private sealed record PivotValueColumn(string Field, string Header, PivotKey Key,
        PivotValueConfig ValueConfig, bool IsTotal = false);

    private sealed class PivotKey
    {
        public PivotKey(string display, IReadOnlyList<string> parts)
        { Display = display; Parts = parts; }
        public static readonly PivotKey Empty = new("", Array.Empty<string>());
        public string Display { get; }
        public IReadOnlyList<string> Parts { get; }
        public override bool Equals(object? obj) =>
            obj is PivotKey other && Parts.SequenceEqual(other.Parts, StringComparer.Ordinal);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var p in Parts) hash.Add(p, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class HeaderCell
    {
        public HeaderCell(string text, int colspan = 1, int rowspan = 1, string cssClass = "",
            string sortField = "", bool isSortable = false)
        { Text = text; Colspan = colspan; Rowspan = rowspan; CssClass = cssClass;
          SortField = sortField; IsSortable = isSortable; }
        public string Text { get; }
        public int Colspan { get; }
        public int Rowspan { get; }
        public string CssClass { get; }
        public string SortField { get; }
        public bool IsSortable { get; }
    }

    private sealed class HeaderLevel : List<HeaderCell> { }

    private sealed class PivotDisplayRow
    {
        public PivotDisplayRow(string cssClass) => CssClass = cssClass;
        public string CssClass { get; }
        public List<PivotDisplayCell> Cells { get; set; } = new();
    }

    private sealed class PivotDisplayCell
    {
        public PivotDisplayCell(string displayValue, string cssClass = "", int rowspan = 1,
            int colspan = 1, object? rawValue = null)
        { DisplayValue = displayValue; CssClass = cssClass; Rowspan = rowspan;
          Colspan = colspan; RawValue = rawValue; }
        public string DisplayValue { get; }
        public string CssClass { get; }
        public int Rowspan { get; }
        public int Colspan { get; }
        public object? RawValue { get; }
    }

    private sealed class PivotFilterState
    {
        public string Field { get; set; } = "";
        public List<string> UniqueValues { get; set; } = new();
        public string? SelectedValue { get; set; }
    }
}

public class PivotValueConfig
{
    public string Field { get; set; } = "";
    public string? Label { get; set; }
    public AggregateType Aggregation { get; set; } = AggregateType.Sum;
    public string Format { get; set; } = "";

    public string DisplayLabel => Label ?? $"{Aggregation} of {Field}";
    public string ShortLabel => Label ?? $"{Aggregation}";
}
