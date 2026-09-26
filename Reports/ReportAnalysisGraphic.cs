using System.Data;
using System.Globalization;
using System.Text;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

/// <summary>Series aggregation and cross-tab HTML for the positioned page. Chart pixels come from <see cref="ChartSvg"/>.</summary>
internal static class ReportAnalysisGraphic
{

    internal static List<ChartSeries> Series(ReportAnalysisSnapshot snapshot)
    {
        var definition = snapshot.Definition;
        var series = new List<ChartSeries>();
        if (definition.SummariesAsCategories)
        {
            var points = new List<ChartDataPoint>();
            foreach (var measure in definition.Measures)
            {
                var table = new DataTable(); table.Columns.Add("value", typeof(object));
                foreach (var row in snapshot.Rows) table.Rows.Add(Value(row, measure.Field) ?? DBNull.Value);
                var formatted = ReportValueFormatting.Aggregate(table.Rows.Cast<DataRow>(),
                    new() { Field = "value", AggregateType = measure.Aggregate, Format = "G29" });
                if (!decimal.TryParse(formatted, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var number)) continue;
                ValidateChartValue(number, definition.ChartType);
                var caption = string.IsNullOrWhiteSpace(measure.Caption) ? measure.Field : measure.Caption;
                points.Add(new(caption, (double)number));
            }
            if (points.Count == 0) return series;
            var name = definition.Title.Length == 0 ? "Summary" : definition.Title;
            series.Add(new(name, definition.ChartType, points));
            return series;
        }
        if (definition.EachRecord)
        {
            if (snapshot.Rows.Count > 5000) throw new InvalidDataException("Each-record chart exceeds 5,000 points.");
            foreach (var measure in definition.Measures)
            {
                var points = snapshot.Rows.Select((row, index) =>
                {
                    var label = definition.CategoryField.Length == 0 ? (index + 1).ToString(CultureInfo.CurrentCulture) : Label(Value(row, definition.CategoryField));
                    var value = Value(row, measure.Field);
                    var valid = decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    if (valid) ValidateChartValue(number, definition.ChartType);
                    return new ChartDataPoint(label, valid ? (double)number : 0) { IsEmpty = !valid };
                }).ToList();
                series.Add(new(measure.Caption.Length == 0 ? measure.Field : measure.Caption, definition.ChartType, points));
            }
            return series;
        }
        var categories = snapshot.Rows.Select(row => Label(Value(row, definition.CategoryField))).Distinct(StringComparer.Ordinal).ToArray();
        var descending = definition.DescendingFields.Contains(definition.CategoryField, StringComparer.OrdinalIgnoreCase);
        if (definition.SortCategories || descending)
        {
            var values = snapshot.Rows.GroupBy(row => Label(Value(row, definition.CategoryField)), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Value(group.First(), definition.CategoryField));
            Array.Sort(categories, (a, b) => descending ? CompareCategory(values[b], values[a]) : CompareCategory(values[a], values[b]));
        }
        if (categories.Length > 5000) throw new InvalidDataException("Chart exceeds 5,000 categories.");
        IEnumerable<IGrouping<string, Dictionary<string, object>>> partitions = snapshot.Rows.GroupBy(row => Label(Value(row, definition.SeriesField)));
        if (definition.SeriesField.Length > 0 && !definition.OriginalOrderFields.Contains(definition.SeriesField, StringComparer.OrdinalIgnoreCase)
            && (definition.SortCategories || definition.DescendingFields.Contains(definition.SeriesField, StringComparer.OrdinalIgnoreCase)))
        {
            var comparer = Comparer<object?>.Create(CompareCategory);
            partitions = definition.DescendingFields.Contains(definition.SeriesField, StringComparer.OrdinalIgnoreCase)
                ? partitions.OrderByDescending(group => Value(group.First(), definition.SeriesField), comparer)
                : partitions.OrderBy(group => Value(group.First(), definition.SeriesField), comparer);
        }
        foreach (var partition in partitions)
        foreach (var measure in definition.Measures)
        {
            var table = new DataTable(); table.Columns.Add("value", typeof(object));
            var points = new List<ChartDataPoint>();
            var categoryRows = partition.ToLookup(row => Label(Value(row, definition.CategoryField)), StringComparer.Ordinal);
            foreach (var category in categories)
            {
                table.Clear();
                foreach (var row in categoryRows[category]) table.Rows.Add(Value(row, measure.Field) ?? DBNull.Value);
                var formatted = ReportValueFormatting.Aggregate(table.Rows.Cast<DataRow>(),
                    new() { Field = "value", AggregateType = measure.Aggregate, Format = "G29" });
                if (decimal.TryParse(formatted, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var number))
                {
                    ValidateChartValue(number, definition.ChartType);
                    points.Add(new(category, (double)number));
                }
                else if (definition.ChartType is not (ChartType.Pie or ChartType.Donut)) points.Add(new(category, 0) { IsEmpty = true });
            }
            var caption = string.IsNullOrWhiteSpace(measure.Caption) ? measure.Field : measure.Caption;
            series.Add(new(partition.Key.Length == 0 ? caption : partition.Key + " / " + caption, definition.ChartType, points));
            if (series.Count > 64) throw new InvalidDataException("Chart exceeds 64 series.");
        }
        return series;
    }

    internal static string ChartMarkup(ReportDesignerElement element, ReportAnalysisSnapshot snapshot)
    {
        var height = Math.Max(60, element.HeightTwips / 15d).ToString("0", CultureInfo.InvariantCulture) + "px";
        return ChartSvg.Markup(snapshot.Definition.ChartType, Series(snapshot), snapshot.Definition.Title, snapshot.Definition.ShowLegend, "100%", height);
    }

    internal static string TableMarkup(ReportTableFragment fragment, string title)
    {
        var width = Math.Max(1, fragment.Columns.Sum(column => column.WidthTwips));
        var html = new StringBuilder();
        html.Append("<table class=\"fx-report-table-fragment fx-pivot-table\" data-region=\"").Append(Encode(fragment.RegionId))
            .Append("\" data-pane=\"").Append(fragment.Pane).Append("\" data-row=\"").Append(fragment.Row).Append("\" data-line=\"").Append(fragment.Line)
            .Append("\" aria-label=\"").Append(Encode(title))
            .Append("\" style=\"border-collapse:collapse;table-layout:fixed;width:").Append(Px(width))
            .Append("px;height:").Append(Px(fragment.HeightTwips)).Append("px;font:inherit;line-height:1.15;margin:0;\">");
        html.Append("<colgroup>");
        foreach (var column in fragment.Columns)
            html.Append("<col style=\"width:").Append(N(column.WidthTwips * 100d / width)).Append("%;\"/>");
        html.Append("</colgroup><tbody><tr style=\"height:").Append(Px(Math.Max(1, fragment.HeightTwips - 30))).Append("px;\">");
        if (fragment.LayoutCells is { } layout)
        {
            foreach (var cell in layout)
            {
                html.Append("<td colspan=\"").Append(cell.ColumnSpan).Append("\" style=\"").Append(CellStyle(fragment))
                    .Append(cell.First ? "" : "border-top:0;").Append(cell.Last ? "" : "border-bottom:0;")
                    .Append(cell.Nested is null ? "" : "padding:0;").Append("\">");
                if (cell.Nested is { } nested)
                    html.Append(TableMarkup(nested with { RegionId = fragment.RegionId + "/r" + cell.SourceRow + "c" + cell.SourceColumn, HeightTwips = Math.Max(1, fragment.HeightTwips - 30) }, title));
                else html.Append(Encode(cell.Text));
                html.Append("</td>");
            }
        }
        else
        {
            for (var index = 0; index < fragment.Columns.Count; index++)
            {
                var cell = index < fragment.Cells.Count ? fragment.Cells[index] : "";
                var header = fragment.IsHeader || fragment.Columns[index].IsRowHeader;
                html.Append(header ? "<th scope=\"" + (fragment.IsHeader ? "col" : "row") + "\" style=\"" : "<td style=\"")
                    .Append(CellStyle(fragment)).Append("\">").Append(Encode(cell)).Append(header ? "</th>" : "</td>");
            }
        }
        html.Append("</tr></tbody></table>");
        return html.ToString();
    }

    private static string CellStyle(ReportTableFragment fragment) =>
        "box-sizing:border-box;border:1px solid #8aa;padding:1px 3px;white-space:pre;overflow:hidden;text-align:left;vertical-align:middle;"
        + (fragment.IsHeader ? "background:#e8efed;font-weight:600;" : fragment.IsTotal ? "background:#f1f4f3;font-weight:600;" : "background:#fff;font-weight:normal;");

    private static void ValidateChartValue(decimal value, ChartType type)
    {
        if (value < 0 && type is ChartType.Pie or ChartType.Donut)
            throw new InvalidDataException("Pie charts cannot represent negative values. Use a bar or line chart.");
        if (value < 0 && type == ChartType.StackedArea)
            throw new InvalidDataException("Negative values are not supported by this stacked area adapter. Use an ordinary bar or line chart.");
        if (value < 0 && type is ChartType.StackedBar or ChartType.StackedBar100)
            throw new InvalidDataException("Negative values are not supported by this chart adapter. Use an ordinary bar or line chart.");
    }

    private static int CompareCategory(object? left, object? right)
    {
        if (left is DBNull) left = null;
        if (right is DBNull) right = null;
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (left.GetType() == right.GetType() && left is IComparable comparable) return comparable.CompareTo(right);
        return StringComparer.CurrentCulture.Compare(Label(left), Label(right));
    }

    private static object? Value(Dictionary<string, object> row, string field) => row.GetValueOrDefault(field);
    private static string Label(object? value) => value is null or DBNull ? "" : value is DateTime date ? date.ToString("d", CultureInfo.CurrentCulture) : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
    private static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Px(int twips) => N(twips / 15d);
}
