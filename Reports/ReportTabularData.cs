using System.Globalization;
using System.Text;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed record ReportTabularColumn(string Caption, int WidthTwips, bool IsRowHeader = false);
public sealed record ReportTabularRow(IReadOnlyList<string> Cells, bool IsTotal = false);
public sealed record ReportTableFragment(IReadOnlyList<ReportTabularColumn> Columns, IReadOnlyList<string> Cells,
    int Pane, int Row, int Line, bool IsHeader, bool IsTotal, int HeightTwips)
{
    public string RegionId { get; init; } = "";
    public IReadOnlyList<ReportTableCellFragment>? LayoutCells { get; init; }
    public IReadOnlyList<ReportTableFragment> ContinuationHeaders { get; init; } = [];
    public bool KeepWithNext { get; init; }
}
public sealed record ReportTableCellFragment(int Column, int ColumnSpan, string Text, ReportTableFragment? Nested,
    bool First, bool Last, int SourceRow, int SourceColumn);
public sealed record ReportTablePane(IReadOnlyList<ReportTableFragment> Headers, IReadOnlyList<ReportTableFragment> Rows);

/// <summary>Evaluated table cells. Pagination never re-queries or recomputes partial-page totals.</summary>
public sealed partial record ReportTabularData(IReadOnlyList<ReportTabularColumn> Columns, IReadOnlyList<ReportTabularRow> Rows, int RepeatedColumns)
{
    private readonly Dictionary<(int Width, decimal Font, int Height), IReadOnlyList<ReportTablePane>> _panes = new();
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
        var rows = snapshot.Rows.Select(row => new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase)).ToList();
        var columns = rows.Select(row => System.Text.Json.JsonSerializer.Serialize(definition.ColumnFields.Select(field => row.GetValueOrDefault(field)))).Distinct().Take(257).Count();
        if (columns > 256) throw new InvalidDataException("Cross-tab exceeds 256 column groups.");
        var groups = rows.Select(row => System.Text.Json.JsonSerializer.Serialize(definition.RowFields.Select(field => row.GetValueOrDefault(field)))).Distinct().Count();
        if ((long)Math.Max(1, groups) * (columns + 1) * definition.Measures.Count > 2000000)
            throw new InvalidDataException("Cross-tab exceeds two million cells.");
        var measures = new List<PivotValueConfig>();
        for (var index = 0; index < definition.Measures.Count; index++)
        {
            var measure = definition.Measures[index];
            var field = measure.Aggregate == ReportAggregateType.Count ? $"__fx_count_{index}" : measure.Field;
            if (measure.Aggregate == ReportAggregateType.Count)
                foreach (var row in rows) row[field] = row.GetValueOrDefault(measure.Field) is null or DBNull ? 0 : 1;
            measures.Add(new() { Field = field, Label = measure.Caption.Length == 0 ? measure.Field : measure.Caption,
                ValueAggregator = measure.Aggregate is ReportAggregateType.DistinctCount or ReportAggregateType.Median
                    ? values => ReportValueFormatting.ExtendedAggregate(values, measure.Aggregate) : null,
                Aggregation = measure.Aggregate switch { ReportAggregateType.Average => AggregateType.Average,
                    ReportAggregateType.Min => AggregateType.Min, ReportAggregateType.Max => AggregateType.Max, _ => AggregateType.Sum }, Format = measure.Format });
        }
#pragma warning disable BL0005
        var pivot = new PivotControl<Dictionary<string, object>> { DataSource = rows, RowFields = definition.RowFields,
            ColumnFields = definition.ColumnFields, ValueFields = measures, ShowGrandTotals = definition.ShowTotals,
            ShowSubTotals = definition.ShowTotals, Interactive = false, AllowSorting = false,
            SortDescriptors = definition.DescendingFields.Select(field => new PivotSortDescriptor(field, SortDirection.Descending)).ToArray() };
#pragma warning restore BL0005
        var table = pivot.CreateExportTable(definition.Title, repeatRowLabels: true);
        var matrix = new ReportTabularData(table.Columns.Select((c, i) => new ReportTabularColumn(i < definition.RowFields.Count ? FieldLabel(c.Header) : c.Header,
                i < definition.RowFields.Count ? definition.RowHeaderWidthTwips : definition.ValueColumnWidthTwips, i < definition.RowFields.Count)).ToArray(),
            table.Rows.Select(row => new ReportTabularRow(row.Values.Select(value => Format(value, "")).ToArray(), row.IsBold)).ToArray(), definition.RowFields.Count);
        return matrix.WithCells(element, snapshot, cell => pivot.ExportCellRows(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan));
    }

    /// <summary>Narrowest pane <see cref="PaginateColumns"/> accepts (0.25 inch).</summary>
    internal const int MinimumPaneWidthTwips = 360;

    public IReadOnlyList<ReportTablePane> PaginateColumns(int widthTwips, decimal fontSize, int rowHeightTwips)
    {
        if (_panes.TryGetValue((widthTwips, fontSize, rowHeightTwips), out var cached)) return cached;
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
                var width = Math.Min(Columns[start].WidthTwips, widthTwips - widths.Take(repeated).Sum());
                if (width > room && indexes.Count > repeated) break;
                indexes.Add(start++); widths.Add(width); room -= width;
            }
            if (indexes.Count == 0) break;
            var selected = indexes.Select((column, index) => Columns[column] with { WidthTwips = widths[index] }).ToArray();
            var lineHeight = Math.Max(Math.Max(rowHeightTwips, MinimumLineHeight), (int)Math.Ceiling(fontSize * 28m) + 90);
            var headers = Lines(indexes.Select(i => Columns[i].Caption).ToArray(), -1, true, false);
            var body = _cells.Count == 0
                ? Rows.SelectMany((row, index) => Lines(indexes.Select(i => i < row.Cells.Count ? row.Cells[i] : "").ToArray(), index, false, row.IsTotal)).ToArray()
                : LayoutCells(indexes, selected, panes.Count, fontSize, lineHeight);
            panes.Add(new(headers, body));
            if (panes.Count > 512) throw new InvalidDataException("Table exceeds 512 horizontal panes.");

            ReportTableFragment[] Lines(string[] values, int row, bool header, bool total)
            {
                var lines = values.Select((value, index) => Wrap(value, selected[index].WidthTwips, fontSize)).ToArray();
                var count = lines.Select(value => value.Count).DefaultIfEmpty(1).Max();
                return Enumerable.Range(0, count).Select(line => new ReportTableFragment(selected,
                    lines.Select((value, index) => line < value.Count ? value[line]
                        : selected[index].IsRowHeader && value.Count > 0 ? value[0] : "").ToArray(),
                    panes.Count, row, line, header, total, lineHeight)).ToArray();
            }
        } while (start < Columns.Count);
        return _panes[(widthTwips, fontSize, rowHeightTwips)] = panes;
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
