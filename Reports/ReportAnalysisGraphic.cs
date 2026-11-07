using System.Data;
using System.Globalization;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

/// <summary>Chart series for <see cref="ReportAnalysisControl"/>: categories, series partitions and aggregated measure values.</summary>
internal static class ReportAnalysisGraphic
{
    internal static readonly string[] CrystalBarPalette = ["#3a6598", "#efa252", "#009470", "#dd581f", "#a22d62", "#fece60"];

    internal static List<ChartSeries> Series(ReportAnalysisSnapshot snapshot)
    {
        var definition = snapshot.Definition;
        var series = new List<ChartSeries>();
        if (definition.SummariesAsCategories)
        {
            var points = new List<ChartDataPoint>();
            foreach (var measure in definition.Measures)
            {
                var formatted = Aggregate(snapshot.Rows, measure);
                var caption = string.IsNullOrWhiteSpace(measure.Caption) ? measure.Field : measure.Caption;
                if (!decimal.TryParse(formatted, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var number))
                {
                    if (definition.ChartType is not (ChartType.Pie or ChartType.Donut)) points.Add(new(caption, 0) { IsEmpty = true });
                    continue;
                }
                ValidateChartValue(number, definition.ChartType);
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
                    var label = definition.CategoryField.Length == 0 ? (index + 1).ToString(CultureInfo.CurrentCulture)
                        : CategoryCaption(definition, Label(definition, definition.CategoryField, Value(row, definition.CategoryField)), Value(row, definition.CategoryField));
                    var value = Value(row, measure.Field);
                    var valid = decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    if (valid) ValidateChartValue(number, definition.ChartType);
                    return new ChartDataPoint(label, valid ? (double)number : 0) { IsEmpty = !valid };
                }).ToList();
                series.Add(new(measure.Caption.Length == 0 ? measure.Field : measure.Caption, definition.ChartType, points));
            }
            return CoordinateSeries(definition, series);
        }
        var categoryName = definition.Groups.FirstOrDefault(group => group.Field.Equals(definition.CategoryField, StringComparison.OrdinalIgnoreCase))?.NameSelector();
        var seriesName = definition.Groups.FirstOrDefault(group => group.Field.Equals(definition.SeriesField, StringComparison.OrdinalIgnoreCase))?.NameSelector();
        var categoryRowsByKey = snapshot.Rows.GroupBy(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField)), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        object? CategoryValue(string key) => Value(categoryRowsByKey[key], definition.CategoryField);
        string Caption(string key) => categoryName?.Invoke(categoryRowsByKey[key]) ?? CategoryCaption(definition, key, CategoryValue(key));
        var categories = categoryRowsByKey.Keys.ToArray();
        var descending = definition.DescendingFields.Contains(definition.CategoryField, StringComparer.OrdinalIgnoreCase);
        if (definition.SortCategories || descending)
        {
            Array.Sort(categories, (a, b) => descending ? CompareCategory(CategoryValue(b), CategoryValue(a)) : CompareCategory(CategoryValue(a), CategoryValue(b)));
        }
        if (categories.Length > 5000) throw new InvalidDataException("Chart exceeds 5,000 categories.");
        if (definition.RankedCategoryCount > 0)
        {
            var rankedRows = snapshot.Rows.ToLookup(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField)));
            var measure = definition.Measures[0];
            categories = categories.OrderByDescending(key => decimal.TryParse(Aggregate(rankedRows[key], measure),
                NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var value) ? value : decimal.MinValue)
                .Take(definition.RankedCategoryCount).ToArray();
        }
        IEnumerable<IGrouping<string, Dictionary<string, object>>> partitions = snapshot.Rows.GroupBy(row => Label(definition, definition.SeriesField, Value(row, definition.SeriesField)));
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
            var points = new List<ChartDataPoint>();
            var categoryRows = partition.ToLookup(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField)), StringComparer.Ordinal);
            foreach (var category in categories)
            {
                if (definition.CrossTabSeries && measure.Aggregate == ReportAggregateType.Sum && !categoryRows.Contains(category))
                {
                    points.Add(new(Caption(category), 0));
                    continue;
                }
                // A missing category/series intersection has no bar. A real zero
                // still draws a zero-height face in a perspective chart.
                if ((definition.ChartThreeDimensional || definition.SpatialStyle?.Family is 1 or 4 or 5) && !definition.CrossTabSeries && definition.SeriesField.Length > 0 && !categoryRows.Contains(category))
                {
                    points.Add(new(Caption(category), 0) { IsEmpty = true });
                    continue;
                }
                var formatted = Aggregate(categoryRows[category], measure);
                if (decimal.TryParse(formatted, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var number))
                {
                    ValidateChartValue(number, definition.ChartType);
                    points.Add(new(Caption(category), (double)number));
                }
                else if (definition.ChartType is not (ChartType.Pie or ChartType.Donut))
                    points.Add(new(Caption(category), 0) { IsEmpty = true });
            }
            var caption = string.IsNullOrWhiteSpace(measure.Caption) ? measure.Field : measure.Caption;
            if (definition.ChartType is ChartType.Gauge or ChartType.Line or ChartType.Area or ChartType.Ohlc or ChartType.StackedBar100 && string.IsNullOrWhiteSpace(measure.Caption))
            {
                var field = measure.Field.Trim('{', '}').Split('.').Last();
                caption = (measure.Aggregate == ReportAggregateType.Average ? "Avg" : measure.Aggregate.ToString()) + " of " + field;
            }
            var seriesLabel = seriesName?.Invoke(partition.First()) ?? partition.Key;
            series.Add(new(partition.Key.Length == 0 ? caption : definition.Measures.Count == 1 ? seriesLabel : seriesLabel + " / " + caption, definition.ChartType, points));
            if (series.Count > 64) throw new InvalidDataException("Chart exceeds 64 series.");
        }
        return CoordinateSeries(definition, series);
    }

    private static string Aggregate(IEnumerable<Dictionary<string, object>> rows, ReportAnalysisMeasure measure)
    {
        if (measure.DoNotSummarize)
        {
            decimal? value = null;
            foreach (var row in rows)
            {
                var candidate = Value(row, measure.Field);
                if (candidate is null or DBNull) continue;
                if (!decimal.TryParse(Convert.ToString(candidate, CultureInfo.CurrentCulture),
                    NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out var number))
                    throw new InvalidDataException("An unsummarized chart formula must supply a numeric value.");
                if (value is not null && value != number)
                    throw new InvalidDataException("An unsummarized chart formula must be constant within each category.");
                value = number;
            }
            return value?.ToString("G29", CultureInfo.CurrentCulture) ?? "";
        }
        var table = new DataTable(); table.Columns.Add("value", typeof(object));
        foreach (var row in rows) table.Rows.Add(Value(row, measure.Field) ?? DBNull.Value);
        return ReportValueFormatting.Aggregate(table.Rows.Cast<DataRow>(),
            new() { Field = "value", AggregateType = measure.Aggregate, Format = "G29" });
    }

    private static List<ChartSeries> CoordinateSeries(ReportAnalysisDefinition definition, List<ChartSeries> series)
    {
        if (definition.ChartType is not (ChartType.Scatter or ChartType.Bubble)) return series;
        var result = new List<ChartSeries>();
        foreach (var measures in series.Chunk(definition.Measures.Count))
        {
            for (var i = 0; i < measures[0].DataPoints.Count; i++)
            {
                if (measures.Any(measure => measure.DataPoints[i].IsEmpty)) continue;
                var x = measures[0].DataPoints[i];
                var name = x.Label;
                if (decimal.TryParse(name, NumberStyles.Number, CultureInfo.CurrentCulture, out var category))
                    name = category.ToString("F2", CultureInfo.CurrentCulture);
                if (definition.SeriesField.Length > 0) name = measures[0].Name + " / " + name;
                var point = new ChartDataPoint(x.Value.ToString("R", CultureInfo.InvariantCulture), measures[1].DataPoints[i].Value)
                    { Value2 = measures.Length == 3 ? measures[2].DataPoints[i].Value : null };
                result.Add(new(name, definition.ChartType, [point]));
            }
        }
        return result;
    }

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
    private static string Label(object? value) => value is DateTime date ? date.ToString("d", CultureInfo.CurrentCulture)
        : value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
    /// <summary>An axis grouped by a Crystal date period (GroupKinds 1-7) holds period starts, labelled as short dates.</summary>
    private static string Label(ReportAnalysisDefinition definition, string field, object? value) =>
        value is DateTime date && definition.GroupKinds.TryGetValue(field, out var kind) && kind is >= 1 and <= 7
            ? date.ToString(kind == 4 ? "M/yyyy" : "d", CultureInfo.CurrentCulture) : Label(value);
    // Caption formatting must never merge distinct category keys or change their aggregate values.
    private static string CategoryCaption(ReportAnalysisDefinition definition, string key, object? value) =>
        definition.CategoryFormat is { Length: > 0 } format && value is IFormattable formatted
            ? formatted.ToString(format, CultureInfo.CurrentCulture) ?? "" : key;
}
