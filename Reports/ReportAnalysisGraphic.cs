using System.Data;
using System.Globalization;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

/// <summary>Chart series for <see cref="ReportAnalysisControl"/>: categories, series partitions and aggregated measure values.</summary>
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
                    var label = definition.CategoryField.Length == 0 ? (index + 1).ToString(CultureInfo.CurrentCulture) : Label(definition, definition.CategoryField, Value(row, definition.CategoryField));
                    var value = Value(row, measure.Field);
                    var valid = decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    if (valid) ValidateChartValue(number, definition.ChartType);
                    return new ChartDataPoint(label, valid ? (double)number : 0) { IsEmpty = !valid };
                }).ToList();
                series.Add(new(measure.Caption.Length == 0 ? measure.Field : measure.Caption, definition.ChartType, points));
            }
            return series;
        }
        var categories = snapshot.Rows.Select(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField))).Distinct(StringComparer.Ordinal).ToArray();
        var descending = definition.DescendingFields.Contains(definition.CategoryField, StringComparer.OrdinalIgnoreCase);
        if (definition.SortCategories || descending)
        {
            var values = snapshot.Rows.GroupBy(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField)), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Value(group.First(), definition.CategoryField));
            Array.Sort(categories, (a, b) => descending ? CompareCategory(values[b], values[a]) : CompareCategory(values[a], values[b]));
        }
        if (categories.Length > 5000) throw new InvalidDataException("Chart exceeds 5,000 categories.");
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
            var table = new DataTable(); table.Columns.Add("value", typeof(object));
            var points = new List<ChartDataPoint>();
            var categoryRows = partition.ToLookup(row => Label(definition, definition.CategoryField, Value(row, definition.CategoryField)), StringComparer.Ordinal);
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
    private static string Label(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
    /// <summary>An axis grouped by a Crystal date period (GroupKinds 1-7) holds period starts, labelled as short dates.</summary>
    private static string Label(ReportAnalysisDefinition definition, string field, object? value) =>
        value is DateTime date && definition.GroupKinds.TryGetValue(field, out var kind) && kind is >= 1 and <= 7
            ? date.ToString("d", CultureInfo.CurrentCulture) : Label(value);
}
