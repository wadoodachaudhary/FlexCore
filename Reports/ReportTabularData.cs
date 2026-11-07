using System.Globalization;
using System.Text;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed record ReportTabularColumn(string Caption, int WidthTwips, bool IsRowHeader = false, bool IsGrandTotal = false);
public sealed record ReportTabularRow(IReadOnlyList<string> Cells, bool IsTotal = false);
public sealed record ReportTableFragment(IReadOnlyList<ReportTabularColumn> Columns, IReadOnlyList<string> Cells,
    int Pane, int Row, int Line, bool IsHeader, bool IsTotal, int HeightTwips)
{
    public string RegionId { get; init; } = "";
    public ReportNativeCrossTabLayout? NativeCrossTab { get; init; }
    public IReadOnlyList<ReportTableCellFragment>? LayoutCells { get; init; }
    public IReadOnlyList<ReportTableFragment> ContinuationHeaders { get; init; } = [];
    public bool KeepWithNext { get; init; }
    public IReadOnlyList<IReadOnlyList<string>>? StackFills { get; init; }
    public IReadOnlyList<ReportNativeCrossTabCellStyle?>? CellStyles { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsGrandTotalRow { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLastRow { get; init; }
}
public sealed record ReportTableCellFragment(int Column, int ColumnSpan, string Text, ReportTableFragment? Nested,
    bool First, bool Last, int SourceRow, int SourceColumn);
public sealed record ReportTablePane(IReadOnlyList<ReportTableFragment> Headers, IReadOnlyList<ReportTableFragment> Rows);

/// <summary>Evaluated table cells. Pagination never re-queries or recomputes partial-page totals.</summary>
public sealed partial record ReportTabularData(IReadOnlyList<ReportTabularColumn> Columns, IReadOnlyList<ReportTabularRow> Rows, int RepeatedColumns)
{
    public ReportNativeCrossTabLayout? NativeCrossTab { get; init; }
    private IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>>? StackBackgrounds { get; init; }
    private IReadOnlyList<string>? ParentHeadings { get; init; }
    private IReadOnlyList<IReadOnlyList<ReportNativeCrossTabCellStyle?>>? NativeCellStyles { get; init; }
    private IReadOnlyList<ReportNativeCrossTabCellStyle?>? NativeHeaderStyles { get; init; }
    private IReadOnlyList<int>? NativeRowHeights { get; init; }
    private IReadOnlyList<bool>? NativeGrandTotalRows { get; init; }
    private readonly Dictionary<(int Width, decimal Font, int Height, bool KeepColumns), IReadOnlyList<ReportTablePane>> _panes = new();
    internal static ReportTabularData Create(ReportDesignerElement element, ReportAnalysisSnapshot snapshot)
    {
        var definition = snapshot.Definition;
        if (element.Kind == "Table")
        {
            if ((long)snapshot.Rows.Count * definition.TableColumns.Count > 2000000) throw new InvalidDataException("Table exceeds two million cells.");
            var result = new ReportTabularData(definition.TableColumns.Select(c => new ReportTabularColumn(c.Caption.Length == 0 ? FieldLabel(c.Field) : c.Caption, c.WidthTwips)).ToArray(),
                snapshot.Rows.Select(row => new ReportTabularRow(definition.TableColumns.Select(c =>
                    Format(row.GetValueOrDefault(c.Field), c.Format)).ToArray())).ToArray(), 0);
            return result.WithCells(element, snapshot, cell => snapshot.Rows.Skip(cell.Row).Take(cell.RowSpan).ToArray());
        }
        if (definition.NativeCrossTab is { ColumnSumFormat.Length: > 0 } grid)
        {
            var field = definition.ColumnFields.Single();
            var measure = definition.Measures.Single();
            var columnGroups = snapshot.Rows.GroupBy(record => record[field]).OrderBy(group => group.Key).ToList();
            if (definition.DescendingFields.Contains(field, StringComparer.OrdinalIgnoreCase)) columnGroups.Reverse();
            var headings = new List<ReportTabularColumn>();
            var values = new List<string>();
            if (grid.RowTotalLabelStyle?.Suppressed != true)
            {
                headings.Add(new("", definition.RowHeaderWidthTwips, true));
                values.Add(grid.Appearance!.RowTotalCaption);
            }
            string Summary(IEnumerable<Dictionary<string, object>> records, string format)
            {
                var present = records.Select(record => record.GetValueOrDefault(measure.Field))
                    .Where(value => value is not null and not DBNull).ToArray();
                if (measure.Aggregate == ReportAggregateType.Count) return FormatSummary(present.Length, format);
                if (measure.Aggregate == ReportAggregateType.Median)
                    return Format(ReportValueFormatting.ExtendedAggregate(present, measure.Aggregate), format);
                var sum = present.Sum(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                if (measure.Aggregate == ReportAggregateType.Average && present.Length == 0) return "";
                return FormatSummary(measure.Aggregate == ReportAggregateType.Average ? sum / present.Length : sum, format);
            }
            foreach (var group in columnGroups)
            {
                headings.Add(new(Format(group.Key, group.Key is DateTime ? definition.GroupKinds.GetValueOrDefault(field) == 4 ? "M/yyyy" : "d" : ""), definition.ValueColumnWidthTwips));
                values.Add(Summary(group, grid.ColumnSumFormat));
            }
            if (!grid.SuppressRowGrandTotals)
            {
                headings.Add(new(grid.Appearance!.ColumnTotalCaption, grid.TotalWidth, false, true));
                values.Add(Summary(snapshot.Rows, grid.TotalSumFormat));
            }
            return new(headings, [new(values, true)], 0) { NativeCrossTab = grid };
        }
        var grouping = new ReportAnalysisGrouping(definition, snapshot.Rows.Select(row => new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase)));
        var rows = grouping.Rows;
        var columns = rows.Select(row => System.Text.Json.JsonSerializer.Serialize(definition.ColumnFields.Select(field => grouping.Value(row, field)))).Distinct().Take(257).Count();
        if (columns > 256) throw new InvalidDataException("Cross-tab exceeds 256 column groups.");
        var groups = rows.Select(row => System.Text.Json.JsonSerializer.Serialize(definition.RowFields.Select(field => grouping.Value(row, field)))).Distinct().Count();
        if ((long)Math.Max(1, groups) * (columns + 1) * definition.Measures.Count > 2000000)
            throw new InvalidDataException("Cross-tab exceeds two million cells.");
        if (definition.RowRanking is { } rank)
            return RankedRows(definition, grouping, rank);
        var measures = new List<PivotValueConfig>();
        for (var index = 0; index < definition.Measures.Count; index++)
        {
            var measure = definition.Measures[index];
            var field = measure.Aggregate == ReportAggregateType.Count ? $"__fx_count_{index}" : measure.Field;
            if (measure.Aggregate == ReportAggregateType.Count)
                foreach (var row in rows) row[field] = row.GetValueOrDefault(measure.Field) is null or DBNull ? 0 : 1;
            if (measure.Aggregate == ReportAggregateType.WeightedAverage)
            {
                field = $"__fx_weighted_{index}";
                foreach (var row in rows)
                    row[field] = row.GetValueOrDefault(measure.Field) is { } value && value is not DBNull
                        && row.GetValueOrDefault(measure.WeightField) is { } weight && weight is not DBNull
                        ? (Convert.ToDecimal(value), Convert.ToDecimal(weight)) : DBNull.Value;
            }
            measures.Add(new() { Field = field, Label = measure.Caption.Length == 0 ? measure.Field : measure.Caption,
                ValueAggregator = measure.Aggregate == ReportAggregateType.WeightedAverage ? Weighted : measure.Aggregate is ReportAggregateType.DistinctCount or ReportAggregateType.Median or ReportAggregateType.Min or ReportAggregateType.Max
                    ? values => ReportValueFormatting.ExtendedAggregate(values, measure.Aggregate) : null,
                Aggregation = measure.Aggregate switch { ReportAggregateType.Average => AggregateType.Average,
                    ReportAggregateType.Min => AggregateType.Min, ReportAggregateType.Max => AggregateType.Max, _ => AggregateType.Sum }, Format = measure.Format });
        }
#pragma warning disable BL0005
        var pivot = new PivotControl<Dictionary<string, object>> { DataSource = rows, RowFields = definition.RowFields,
            GroupValueSelector = grouping.Value,
            ColumnFields = definition.ColumnFields, ValueFields = measures, ShowGrandTotals = definition.ShowTotals,
            ShowSubTotals = definition.ShowTotals, Interactive = false, AllowSorting = false,
            SortDescriptors = definition.DescendingFields.Select(field => new PivotSortDescriptor(field, SortDirection.Descending)).ToArray() };
#pragma warning restore BL0005
        var table = pivot.CreateExportTable(definition.Title, repeatRowLabels: true);
        if (definition.NativeCrossTab is { RowLabelsOnly: true } labels)
        {
            var visible = table.Rows.Where(row => !row.IsBold).Select(row => Format(row.Values[0], ""))
                .Where(label => label.Length > 0).Select(label => new ReportTabularRow([label])).ToArray();
            return new([new("", definition.RowHeaderWidthTwips, true)], visible, 0) { NativeCrossTab = labels };
        }
        if (definition.CalculatedMembers.Count > 0 || definition.NativeCrossTab is { Templates.Count: > 0, StackFormats.Length: 0, ParentHeaderHeight: 0 })
            return CreateCalculatedCrossTab(element, snapshot, pivot, table);
        if (definition.NativeCrossTab is { StackFormats.Length: > 0 } stacked)
            return CreateStackedCrossTab(definition, stacked, pivot, table);
        var appearance = definition.NativeCrossTab?.Appearance;
        if (appearance != null && definition.ColumnFields.Count == 0 && definition.RowFields.Count == 1 && definition.Measures.Count == 1 && definition.Cells.Count == 0)
            return new([new("", definition.RowHeaderWidthTwips, true), new(appearance.ColumnTotalCaption, definition.ValueColumnWidthTwips, false, true)],
                table.Rows.Select(row => new ReportTabularRow([row.IsBold ? appearance.RowTotalCaption : Format(row.Values[0], ""), Format(row.Values[1], "")], row.IsBold)).ToArray(), 1) { NativeCrossTab = definition.NativeCrossTab };
        var matrix = new ReportTabularData(table.Columns.Select((c, i) => new ReportTabularColumn(
                appearance is not null && i < definition.RowFields.Count ? "" : appearance is not null && definition.ShowTotals && i == table.Columns.Count - 1
                    ? appearance.ColumnTotalCaption : i < definition.RowFields.Count ? FieldLabel(c.Header) : c.Header,
                i < definition.RowFields.Count ? definition.RowHeaderWidthTwips : definition.ValueColumnWidthTwips, i < definition.RowFields.Count, definition.ShowTotals && i == table.Columns.Count - 1)).ToArray(),
            table.Rows.Select(row => new ReportTabularRow(row.Values.Select((value, i) => appearance is not null && row.IsBold && i == 0 ? appearance.RowTotalCaption : Format(value, "")).ToArray(), row.IsBold)).ToArray(), definition.RowFields.Count) { NativeCrossTab = definition.NativeCrossTab };
        return matrix.WithCells(element, snapshot, cell => pivot.ExportCellRows(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan)).WithoutSuppressedNativeTotals();
    }

    private static object Weighted(IEnumerable<object?> values)
    {
        decimal sum = 0, weight = 0;
        foreach (var (v,w) in values.OfType<(decimal,decimal)>()) { sum += v*w; weight += w; }
        return weight == 0 ? DBNull.Value : sum / weight;
    }

    private static ReportTabularData RankedRows(ReportAnalysisDefinition definition, ReportAnalysisGrouping grouping, ReportRowRanking rank)
    {
        var native = definition.NativeCrossTab!;
        var captions = native.Appearance!;
        var field = definition.RowFields.Single();
        var measure = definition.Measures.Single();
        var totals = grouping.Rows.GroupBy(row => grouping.Value(row, field))
            .Select(group => (Key: group.Key, Amount: group.Sum(row => row[measure.Field] is null or DBNull
                ? 0m : Convert.ToDecimal(row[measure.Field], CultureInfo.InvariantCulture))))
            .ToArray();
        var ordered = (rank.Bottom ? totals.OrderBy(pair => pair.Amount) : totals.OrderByDescending(pair => pair.Amount))
            .ThenBy(pair => Format(pair.Key, ""), StringComparer.CurrentCulture).ToArray();
        var count = Math.Min(rank.Count, ordered.Length);
        var rows = ordered.TakeWhile((pair, index) => index < count || rank.WithTies && count > 0 && pair.Amount == ordered[count - 1].Amount)
            .Select(pair => new ReportTabularRow([Format(pair.Key, ""), Format(pair.Amount, measure.Format)])).ToList();
        if (definition.ShowTotals && !native.SuppressColumnGrandTotals)
            rows.Add(new([captions.RowTotalCaption, Format(totals.Sum(pair => pair.Amount), measure.Format)], true));
        return new([new("", definition.RowHeaderWidthTwips, true), new(captions.ColumnTotalCaption, definition.ValueColumnWidthTwips, false, true)],
            rows, 1) { NativeCrossTab = native };
    }

    private ReportTabularData WithoutSuppressedNativeTotals()
    {
        if (NativeCrossTab is not { } native || !native.SuppressColumnGrandTotals && !native.SuppressRowGrandTotals) return this;
        var columns = Enumerable.Range(0, Columns.Count).Where(i => !native.SuppressRowGrandTotals || !Columns[i].IsGrandTotal).ToArray();
        return new(columns.Select(i => Columns[i]).ToArray(), Rows.Where(row => !native.SuppressColumnGrandTotals || !row.IsTotal)
            .Select(row => row with { Cells = columns.Select(i => row.Cells[i]).ToArray() }).ToArray(), RepeatedColumns) { NativeCrossTab = native };
    }

    /// <summary>Narrowest pane <see cref="PaginateColumns"/> accepts (0.25 inch).</summary>
    internal const int MinimumPaneWidthTwips = 360;

    public IReadOnlyList<ReportTablePane> PaginateColumns(int widthTwips, decimal fontSize, int rowHeightTwips, bool keepColumnsTogether = false)
    {
        if (_panes.TryGetValue((widthTwips, fontSize, rowHeightTwips, keepColumnsTogether), out var cached)) return cached;
        if (widthTwips < MinimumPaneWidthTwips || fontSize is <= 0 or > 200) throw new InvalidDataException("The table has no usable printable width or font size.");
        var panes = new List<ReportTablePane>();
        var repeated = Math.Min(RepeatedColumns, Columns.Count);
        var keys = Enumerable.Range(0, repeated).ToList();
        var keyWidth = keys.Sum(i => Columns[i].WidthTwips);
        var keyScale = keyWidth == 0 ? 1d : Math.Min(1d, widthTwips / 2d / keyWidth);
        var start = repeated;
        do
        {
            var indexes = new List<int>(keys);
            var widths = keys.Select(i => Math.Max(1, (int)(Columns[i].WidthTwips * keyScale))).ToList();
            var room = widthTwips - widths.Sum();
            while (start < Columns.Count)
            {
                var natural = Columns[start].WidthTwips;
                var paneLimit = Math.Max(1, widthTwips - widths.Take(repeated).Sum());
                var width = keepColumnsTogether ? natural : Math.Min(natural, paneLimit);
                // Keep Columns Together moves a column that does not fit onto the next pane instead of narrowing it.
                if (width > room && indexes.Count > repeated) break;
                if (keepColumnsTogether && natural > paneLimit && indexes.Count == repeated)
                {
                    indexes.Add(start++); widths.Add(natural); break;
                }
                var placed = Math.Min(width, paneLimit);
                indexes.Add(start++); widths.Add(placed); room -= placed;
            }
            if (indexes.Count == 0) break;
            var selected = indexes.Select((column, index) => Columns[column] with { WidthTwips = widths[index] }).ToArray();
            var lineHeight = NativeCrossTab is null ? Math.Max(Math.Max(rowHeightTwips, MinimumLineHeight), (int)Math.Ceiling(fontSize * 28m) + 90) : rowHeightTwips;
            var headers = NativeCrossTab?.RowLabelsOnly == true ? [] : Lines(indexes.Select(i => Columns[i].Caption).ToArray(), -1, true, false);
            if (ParentHeadings is not null && NativeCrossTab is { } native)
                headers = [ParentHeader(indexes, selected, panes.Count, native), .. headers];
            var body = _cells.Count == 0
                ? Rows.SelectMany((row, index) => Lines(indexes.Select(i => i < row.Cells.Count ? row.Cells[i] : "").ToArray(), index, false, row.IsTotal)).ToArray()
                : LayoutCells(indexes, selected, panes.Count, fontSize, lineHeight);
            panes.Add(new(headers, body));
            if (panes.Count > 512) throw new InvalidDataException("Table exceeds 512 horizontal panes.");

            ReportTableFragment[] Lines(string[] values, int row, bool header, bool total)
            {
                var lines = values.Select((value, index) => NativeCrossTab is null ? Wrap(value, selected[index].WidthTwips, fontSize) : (IReadOnlyList<string>)new[] { value }).ToArray();
                var count = lines.Select(value => value.Count).DefaultIfEmpty(1).Max();
                return Enumerable.Range(0, count).Select(line => new ReportTableFragment(selected,
                    lines.Select((value, index) => line < value.Count ? value[line]
                        : selected[index].IsRowHeader && value.Count > 0 ? value[0] : "").ToArray(),
                    panes.Count, row, line, header, total, header && NativeCrossTab is { HeaderHeight: > 0 } native ? native.HeaderHeight
                        : !header && NativeRowHeights?.ElementAtOrDefault(row) is > 0 and var nativeHeight ? nativeHeight : lineHeight)
                    { NativeCrossTab = NativeCrossTab, IsGrandTotalRow = !header && (NativeGrandTotalRows?.ElementAtOrDefault(row) ?? total),
                        IsLastRow = !header && row == Rows.Count - 1,
                        StackFills = !header && StackBackgrounds is not null ? indexes.Select(i => StackBackgrounds[row][i]).ToArray() : null,
                        CellStyles = (header ? NativeHeaderStyles : NativeCellStyles?.ElementAtOrDefault(row)) is { } styles
                        ? indexes.Select(i => styles.ElementAtOrDefault(i)).ToArray() : null }).ToArray();
            }
        } while (start < Columns.Count);
        return _panes[(widthTwips, fontSize, rowHeightTwips, keepColumnsTogether)] = panes;
    }

    private static string Format(object? value, string format) => value switch
    {
        null or DBNull => "", IFormattable formattable => formattable.ToString(format, CultureInfo.CurrentCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static string FieldLabel(string field)
    {
        var name = field.Trim('{', '}');
        return name[(name.LastIndexOf('.') + 1)..].TrimStart('@', '?');
    }

    // Conservative, deterministic cells use explicit lines in both viewer and export. This avoids
    // browser-dependent extra wrapping after pagination; measured proportional text can be added later.
    private static IReadOnlyList<string> Wrap(string value, int width, decimal fontSize)
    {
        var capacity = Math.Max(1m, (width - 120) / (fontSize * 20m));
        var result = new List<string>();
        foreach (var paragraph in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var current = new StringBuilder(); var used = 0m;
            foreach (var rune in paragraph.EnumerateRunes())
            {
                var length = rune.Value > 127 || rune.Value is 'W' or 'M' or 'w' or 'm' or '@' ? 1.1m
                    : rune.Value is 'i' or 'l' or 'I' or '!' or '.' or ',' or ':' or ';' or ' ' ? 0.4m : 0.75m;
                if (used + length > capacity && current.Length > 0) { result.Add(current.ToString()); current.Clear(); used = 0; }
                current.Append(rune); used += length;
            }
            result.Add(current.ToString());
        }
        return result;
    }
}
