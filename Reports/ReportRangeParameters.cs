using System.Globalization;

namespace Fx.ControlKit.Reports;

/// <summary>Portable range values shared by prompts, persistence and SQL binding.</summary>
public sealed record ReportRangeValue(string? Start, string? End, bool IncludeStart = true, bool IncludeEnd = true)
{
    public static ReportRangeValue From(CrystalRange range) => new(Format(range.Start), Format(range.End), range.IncludeStart, range.IncludeEnd);
    public CrystalRange ToRange(ReportParameterType type)
    {
        var start = Parse(Start, type);
        var end = Parse(End, type);
        if (start is not null && end is not null)
        {
            var comparison = start is string a && end is string b ? StringComparer.Ordinal.Compare(a, b) : ((IComparable)start).CompareTo(end);
            if (comparison > 0 || (comparison == 0 && (!IncludeStart || !IncludeEnd)))
                throw new InvalidDataException("The range end must follow its start; an equal bound must be inclusive.");
        }
        return new(start, end, IncludeStart, IncludeEnd);
    }
    public static string? Format(object? value) => value switch
    {
        null => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
    private static object? Parse(string? value, ReportParameterType type)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 16000) throw new InvalidDataException("The range bound is too long.");
        return type switch
        {
            ReportParameterType.Integer => long.Parse(value, CultureInfo.InvariantCulture),
            ReportParameterType.Decimal => decimal.Parse(value, CultureInfo.InvariantCulture),
            ReportParameterType.Date => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ReportParameterType.Boolean => bool.Parse(value),
            _ => value
        };
    }
}

public static class ReportRangeParameters
{
    public static CrystalRange[] Ranges(object? value) => value switch
    {
        CrystalRange range => [range],
        IEnumerable<CrystalRange> ranges => ranges.ToArray(),
        _ => []
    };

    /// <summary>SQL uses @Name_Start/End and @Name_IncludeStart/End. Null bounds are unbounded.
    /// Formula callers retain the original value bag, not this scalar-only copy.</summary>
    public static Dictionary<string, object> BindSql(IReadOnlyDictionary<string, object> values)
    {
        var result = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            var ranges = Ranges(value);
            if (value is not CrystalRange && value is not IEnumerable<CrystalRange>) continue;
            if (ranges.Length > 100) throw new InvalidDataException("At most 100 ranges are supported.");
            result.Remove(name);
            void Add(string key, object item)
            {
                if (!result.TryAdd(key, item)) throw new InvalidDataException($"Range binding collides with parameter '{key}'.");
            }
            Add(name + "_Count", ranges.Length);
            for (var i = 0; i < ranges.Length; i++)
            {
                var prefix = value is CrystalRange ? name : $"{name}_{i}";
                Add(prefix + "_Start", ranges[i].Start ?? DBNull.Value);
                Add(prefix + "_End", ranges[i].End ?? DBNull.Value);
                Add(prefix + "_IncludeStart", ranges[i].IncludeStart);
                Add(prefix + "_IncludeEnd", ranges[i].IncludeEnd);
            }
        }
        return result;
    }
}
