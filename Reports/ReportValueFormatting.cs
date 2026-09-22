using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Reports;

internal static class ReportValueFormatting
{
    internal static string Aggregate(IEnumerable<DataRow> rows, ReportAggregate aggregate)
    {
        var values = rows.Where(row => row.Table.Columns.Contains(aggregate.Field))
            .Select(row => row[aggregate.Field]).Where(value => value is not null and not DBNull).ToList();
        decimal result;
        if (aggregate.AggregateType is ReportAggregateType.Count or ReportAggregateType.DistinctCount)
            result = aggregate.AggregateType == ReportAggregateType.Count ? values.Count : values.Distinct().Count();
        else
        {
            if (values.Count == 0) return "";
            // Percent needs a denominator the aggregate does not carry; show it blank rather than
            // failing the whole report render.
            if (aggregate.AggregateType is not (ReportAggregateType.Sum or ReportAggregateType.Average
                or ReportAggregateType.Min or ReportAggregateType.Max or ReportAggregateType.Median)) return "";
            var numbers = new List<decimal>(values.Count);
            foreach (var value in values)
            {
                if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return "";
                numbers.Add(number);
            }
            result = aggregate.AggregateType switch
            {
                ReportAggregateType.Sum => numbers.Sum(),
                ReportAggregateType.Average => numbers.Average(),
                ReportAggregateType.Min => numbers.Min(),
                ReportAggregateType.Max => numbers.Max(),
                ReportAggregateType.Median => Median(numbers),
                _ => throw new NotSupportedException($"Aggregate {aggregate.AggregateType} requires a denominator.")
            };
        }
        var format = string.IsNullOrWhiteSpace(aggregate.Format)
            ? aggregate.AggregateType is ReportAggregateType.Count or ReportAggregateType.DistinctCount ? "N0" : "N2" : aggregate.Format;
        return result.ToString(format, CultureInfo.CurrentCulture);
    }

    internal static object ExtendedAggregate(IEnumerable<object?> source, ReportAggregateType operation)
    {
        var values = source.Where(value => value is not null and not DBNull).ToArray();
        if (operation == ReportAggregateType.DistinctCount) return values.Distinct().Count();
        var numbers = new List<decimal>();
        foreach (var value in values)
        {
            if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return "";
            numbers.Add(number);
        }
        return numbers.Count == 0 ? "" : Median(numbers);
    }

    private static decimal Median(List<decimal> numbers)
    {
        numbers.Sort();
        var middle = numbers.Count / 2;
        return numbers.Count % 2 == 1 ? numbers[middle] : numbers[middle - 1] / 2 + numbers[middle] / 2;
    }

    internal static string CssColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        if (value[0] == '#' && value.Length is 4 or 5 or 7 or 9 && value.Skip(1).All(Uri.IsHexDigit)) return value;
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase) || value.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return value;
        // KnownColor is a platform-neutral enum; no graphics runtime is loaded.
        return value.All(char.IsAsciiLetter) && Enum.TryParse<System.Drawing.KnownColor>(value, true, out var known) &&
            known is >= System.Drawing.KnownColor.AliceBlue and <= System.Drawing.KnownColor.YellowGreen ? value : "";
    }

    internal static string GroupAnchor(DataRow row, IReadOnlyList<ReportGroup> groups, int level, string value)
    {
        var path = groups.Take(level + 1).Select((group, index) => new[]
        {
            group.Field, index == level ? value : row.Table.Columns.Contains(group.Field)
                ? row[group.Field]?.ToString()?.TrimEnd() ?? "" : ""
        });
        return $"group-{level}-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(path))));
    }

    internal static string QueryId(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..16];
}
