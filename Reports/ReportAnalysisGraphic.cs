using System.Data;
using System.Globalization;
using System.Text;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

/// <summary>Chart SVG and cross-tab HTML for the positioned page string. The component viewer draws the same series.</summary>
internal static class ReportAnalysisGraphic
{
    private static readonly string[] Palette = ["#2f6f9f", "#d17a22", "#3d8b6e", "#8a4f9e", "#c04b4b", "#6b7c3a", "#3a6ea5", "#b08900"];

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
        var series = Series(snapshot);
        var width = Math.Max(80, element.WidthTwips / 15d);
        var height = Math.Max(60, element.HeightTwips / 15d);
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"").Append(Encode(element.Name))
            .Append("\" width=\"100%\" height=\"100%\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height))
            .Append("\" style=\"display:block;background:#fff;font-family:Arial,sans-serif;\">");
        var title = snapshot.Definition.Title;
        var top = 8d;
        if (title.Length > 0)
        {
            svg.Append("<text x=\"").Append(N(width / 2)).Append("\" y=\"16\" text-anchor=\"middle\" font-size=\"12\" fill=\"#222\">")
                .Append(Encode(title)).Append("</text>");
            top = 24;
        }
        if (series.Count == 0 || series.All(item => item.DataPoints.Count == 0))
        {
            svg.Append("<text x=\"").Append(N(width / 2)).Append("\" y=\"").Append(N(height / 2)).Append("\" text-anchor=\"middle\" font-size=\"11\" fill=\"#666\">No data</text></svg>");
            return svg.ToString();
        }
        var showLegend = snapshot.Definition.ShowLegend && series.Count > 1;
        var legend = showLegend ? Math.Min(120, width * 0.28) : 0;
        var plot = (Left: 36d, Top: top, Right: width - 8 - legend, Bottom: height - (series[0].DataPoints.Count > 1 ? 28 : 16));
        if (plot.Right - plot.Left < 20 || plot.Bottom - plot.Top < 20)
            plot = (4, top, width - 4, height - 4);
        if (snapshot.Definition.ChartType is ChartType.Pie or ChartType.Donut)
            Pie(svg, series[0], plot.Left, plot.Top, plot.Right, plot.Bottom, snapshot.Definition.ChartType == ChartType.Donut);
        else if (snapshot.Definition.ChartType == ChartType.HorizontalBar)
            HorizontalBars(svg, series, plot.Left, plot.Top, plot.Right, plot.Bottom);
        else
            Cartesian(svg, series, snapshot.Definition.ChartType, plot.Left, plot.Top, plot.Right, plot.Bottom);
        if (showLegend)
        {
            var y = plot.Top;
            for (var index = 0; index < series.Count && y < plot.Bottom; index++)
            {
                var color = Palette[index % Palette.Length];
                svg.Append("<rect x=\"").Append(N(plot.Right + 8)).Append("\" y=\"").Append(N(y)).Append("\" width=\"10\" height=\"10\" fill=\"").Append(color).Append("\"/>");
                svg.Append("<text x=\"").Append(N(plot.Right + 22)).Append("\" y=\"").Append(N(y + 9)).Append("\" font-size=\"9\" fill=\"#222\">")
                    .Append(Encode(Trim(series[index].Name, 18))).Append("</text>");
                y += 14;
            }
        }
        svg.Append("</svg>");
        return svg.ToString();
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

    private static void Cartesian(StringBuilder svg, List<ChartSeries> series, ChartType type, double left, double top, double right, double bottom)
    {
        var labels = series[0].DataPoints.Select(point => point.Label).ToArray();
        var count = Math.Max(1, labels.Length);
        var stacked = type is ChartType.StackedBar or ChartType.StackedBar100 or ChartType.StackedArea;
        var values = new double[series.Count, count];
        double min = 0, max = 0;
        for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
        {
            var points = series[seriesIndex].DataPoints;
            for (var index = 0; index < count; index++)
            {
                var point = index < points.Count ? points[index] : null;
                values[seriesIndex, index] = point is null || point.IsEmpty ? 0 : point.Value;
            }
        }
        if (type == ChartType.StackedBar100)
        {
            for (var index = 0; index < count; index++)
            {
                var total = 0d;
                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++) total += Math.Abs(values[seriesIndex, index]);
                if (total == 0) continue;
                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++) values[seriesIndex, index] = values[seriesIndex, index] / total * 100;
            }
            max = 100;
        }
        else if (stacked)
        {
            for (var index = 0; index < count; index++)
            {
                var sum = 0d;
                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++) sum += values[seriesIndex, index];
                max = Math.Max(max, sum); min = Math.Min(min, sum);
            }
        }
        else
        {
            foreach (var value in values) { max = Math.Max(max, value); min = Math.Min(min, value); }
        }
        if (max == min) max = min + 1;
        double Y(double value) => bottom - (value - min) / (max - min) * (bottom - top);
        svg.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(top)).Append("\" x2=\"").Append(N(left)).Append("\" y2=\"").Append(N(bottom)).Append("\" stroke=\"#888\" stroke-width=\"1\"/>");
        svg.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(Y(0))).Append("\" x2=\"").Append(N(right)).Append("\" y2=\"").Append(N(Y(0))).Append("\" stroke=\"#888\" stroke-width=\"1\"/>");
        var slot = (right - left) / count;
        if (type is ChartType.Line or ChartType.Area or ChartType.StackedArea)
        {
            var baseline = new double[count];
            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var color = Palette[seriesIndex % Palette.Length];
                var line = new StringBuilder();
                var area = new StringBuilder();
                for (var index = 0; index < count; index++)
                {
                    var x = left + slot * index + slot / 2;
                    var y = Y(baseline[index] + values[seriesIndex, index]);
                    line.Append(index == 0 ? "M" : "L").Append(N(x)).Append(' ').Append(N(y));
                    area.Append(index == 0 ? "M" : "L").Append(N(x)).Append(' ').Append(N(y));
                }
                if (type is ChartType.Area or ChartType.StackedArea)
                {
                    for (var index = count - 1; index >= 0; index--)
                        area.Append("L").Append(N(left + slot * index + slot / 2)).Append(' ').Append(N(Y(type == ChartType.StackedArea ? baseline[index] : 0)));
                    area.Append('Z');
                    svg.Append("<path d=\"").Append(area).Append("\" fill=\"").Append(color).Append("\" fill-opacity=\"0.35\" stroke=\"").Append(color).Append("\" stroke-width=\"1.5\"/>");
                }
                else svg.Append("<path d=\"").Append(line).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"1.75\"/>");
                if (stacked)
                    for (var index = 0; index < count; index++) baseline[index] += values[seriesIndex, index];
            }
        }
        else
        {
            var group = Math.Max(1, series.Count);
            var gap = Math.Min(6, slot * 0.15);
            var bar = stacked ? Math.Max(1, slot - gap) : Math.Max(1, (slot - gap) / group);
            for (var index = 0; index < count; index++)
            {
                var origin = left + slot * index + (stacked ? gap / 2 : gap / 2);
                var baseline = 0d;
                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    var value = values[seriesIndex, index];
                    var x = stacked ? origin : origin + seriesIndex * bar;
                    var y0 = Y(stacked ? baseline : 0);
                    var y1 = Y(stacked ? baseline + value : value);
                    var barTop = Math.Min(y0, y1);
                    var barHeight = Math.Max(0.5, Math.Abs(y1 - y0));
                    svg.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(barTop)).Append("\" width=\"").Append(N(Math.Max(1, bar - (stacked ? 0 : 1))))
                        .Append("\" height=\"").Append(N(barHeight)).Append("\" fill=\"").Append(Palette[seriesIndex % Palette.Length]).Append("\"/>");
                    if (stacked) baseline += value;
                }
            }
        }
        for (var index = 0; index < count; index++)
        {
            if (count > 12 && index % (int)Math.Ceiling(count / 8d) != 0) continue;
            svg.Append("<text x=\"").Append(N(left + slot * index + slot / 2)).Append("\" y=\"").Append(N(bottom + 12))
                .Append("\" text-anchor=\"middle\" font-size=\"8\" fill=\"#333\">").Append(Encode(Trim(labels[index], 10))).Append("</text>");
        }
    }

    private static void HorizontalBars(StringBuilder svg, List<ChartSeries> series, double left, double top, double right, double bottom)
    {
        var labels = series[0].DataPoints.Select(point => point.Label).ToArray();
        var count = Math.Max(1, labels.Length);
        var max = series.SelectMany(item => item.DataPoints).Select(point => point.IsEmpty ? 0 : point.Value).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;
        var slot = (bottom - top) / count;
        var group = Math.Max(1, series.Count);
        for (var index = 0; index < count; index++)
        {
            svg.Append("<text x=\"").Append(N(left - 2)).Append("\" y=\"").Append(N(top + slot * index + slot / 2))
                .Append("\" text-anchor=\"end\" font-size=\"8\" fill=\"#333\">").Append(Encode(Trim(labels[index], 8))).Append("</text>");
            for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
            {
                var point = seriesIndex < series.Count && index < series[seriesIndex].DataPoints.Count ? series[seriesIndex].DataPoints[index] : null;
                var value = point is null || point.IsEmpty ? 0 : Math.Max(0, point.Value);
                var barHeight = Math.Max(1, slot / group - 2);
                var y = top + slot * index + seriesIndex * (slot / group);
                svg.Append("<rect x=\"").Append(N(left)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N((right - left) * value / max))
                    .Append("\" height=\"").Append(N(barHeight)).Append("\" fill=\"").Append(Palette[seriesIndex % Palette.Length]).Append("\"/>");
            }
        }
    }

    private static void Pie(StringBuilder svg, ChartSeries series, double left, double top, double right, double bottom, bool donut)
    {
        var points = series.DataPoints.Where(point => !point.IsEmpty && point.Value > 0).ToList();
        var total = points.Sum(point => point.Value);
        if (total <= 0)
        {
            svg.Append("<text x=\"").Append(N((left + right) / 2)).Append("\" y=\"").Append(N((top + bottom) / 2)).Append("\" text-anchor=\"middle\" font-size=\"11\" fill=\"#666\">No data</text>");
            return;
        }
        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;
        var radius = Math.Max(8, Math.Min(right - left, bottom - top) / 2 - 4);
        var inner = donut ? radius * 0.55 : 0;
        var angle = -Math.PI / 2;
        for (var index = 0; index < points.Count; index++)
        {
            var sweep = points[index].Value / total * Math.PI * 2;
            var color = Palette[index % Palette.Length];
            if (points.Count == 1)
            {
                svg.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(radius)).Append("\" fill=\"").Append(color).Append("\"/>");
                if (donut) svg.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(inner)).Append("\" fill=\"#fff\"/>");
            }
            else
            {
                var start = angle;
                var end = angle + sweep;
                var large = sweep > Math.PI ? 1 : 0;
                var x0 = cx + radius * Math.Cos(start);
                var y0 = cy + radius * Math.Sin(start);
                var x1 = cx + radius * Math.Cos(end);
                var y1 = cy + radius * Math.Sin(end);
                if (!donut)
                    svg.Append("<path d=\"M").Append(N(cx)).Append(' ').Append(N(cy)).Append("L").Append(N(x0)).Append(' ').Append(N(y0))
                        .Append("A").Append(N(radius)).Append(' ').Append(N(radius)).Append(" 0 ").Append(large).Append(" 1 ").Append(N(x1)).Append(' ').Append(N(y1)).Append("Z\" fill=\"").Append(color).Append("\"/>");
                else
                {
                    var ix0 = cx + inner * Math.Cos(start);
                    var iy0 = cy + inner * Math.Sin(start);
                    var ix1 = cx + inner * Math.Cos(end);
                    var iy1 = cy + inner * Math.Sin(end);
                    svg.Append("<path d=\"M").Append(N(x0)).Append(' ').Append(N(y0))
                        .Append("A").Append(N(radius)).Append(' ').Append(N(radius)).Append(" 0 ").Append(large).Append(" 1 ").Append(N(x1)).Append(' ').Append(N(y1))
                        .Append("L").Append(N(ix1)).Append(' ').Append(N(iy1))
                        .Append("A").Append(N(inner)).Append(' ').Append(N(inner)).Append(" 0 ").Append(large).Append(" 0 ").Append(N(ix0)).Append(' ').Append(N(iy0))
                        .Append("Z\" fill=\"").Append(color).Append("\"/>");
                }
            }
            var mid = angle + sweep / 2;
            var labelRadius = donut ? (radius + inner) / 2 : radius * 0.62;
            svg.Append("<text x=\"").Append(N(cx + labelRadius * Math.Cos(mid))).Append("\" y=\"").Append(N(cy + labelRadius * Math.Sin(mid)))
                .Append("\" text-anchor=\"middle\" font-size=\"8\" fill=\"#fff\">").Append(Encode(Trim(points[index].Label, 12))).Append("</text>");
            angle += sweep;
        }
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
    private static string Trim(string value, int length) => value.Length <= length ? value : value[..Math.Max(1, length - 1)] + "…";
}
