using System.Globalization;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>
/// The SQL a record-selection term sends to the server. Each clause returns a superset of the rows the complete selection
/// formula keeps, whether database NULLs convert to defaults or not; the formula still decides every returned row.
/// </summary>
internal static class ReportSelectionSql
{
    private const string NonAscii = "COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%'";

    // SQL NULL is not equal to a Crystal type default. Keep it for the full runtime predicate.
    internal static string IncludeNullCandidate(string clause, string column, bool convertNulls)
        => convertNulls ? $"({clause} OR {column} IS NULL)" : clause;

    /// <summary>
    /// The Unicode type the guarded selection compares in. nvarchar(max) made every comparison a LOB operation and
    /// collapsed the optimizer's estimates to one row (the guards then ran inside nested loops: one sample report
    /// spent 2.5 of its 4.1 query seconds there); nvarchar(4000) keeps the code-page guard with the same result for
    /// every column that fits, and a wider column keeps max.
    /// </summary>
    internal static string TextType(XElement? field)
    {
        var bounded = false;
        foreach (var name in new[] { "Length", "NumberOfBytes" })
        {
            if (field?.Attribute(name) is not { } attribute) continue;
            if (!int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size <= 0 || size > 4000)
                return "nvarchar(max)";
            bounded = true;
        }
        return bounded ? "nvarchar(4000)" : "nvarchar(max)";
    }

    // Saved report widths can lag a widened database column. Preserve oversized
    // candidates for runtime evaluation rather than comparing truncated text.
    internal static string LengthEscape(XElement? field, string column)
        => TextType(field) == "nvarchar(4000)" ? $"DATALENGTH(CONVERT(nvarchar(max), {column})) > 8000 OR " : "";

    /// <summary>
    /// Whether a text selector may compare the column directly at execution. Only a bounded character width can be trusted to
    /// be a character column: the converter declares uniqueidentifier columns as 74-byte strings and memo, xml and text columns
    /// as unbounded ones, and a direct comparison against those converts the parameter to the column's type instead. Anything
    /// else keeps the CONVERT guard, which compares as text whatever the column is.
    /// </summary>
    internal static bool DirectTextComparable(XElement? field)
    {
        if (TextType(field) != "nvarchar(4000)") return false;
        foreach (var name in new[] { "Length", "NumberOfBytes" })
            if (int.TryParse((string?)field?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size == 74)
                return false;
        return true;
    }

    /// <summary>A single-value text prompt compared with a column: both sides as text in a case-insensitive collation; either
    /// operand outside printable ASCII is left to the runtime.</summary>
    internal static string PromptEquality(string column, XElement field, string argument, bool convertNulls)
    {
        var type = TextType(field);
        var text = $"CONVERT({type}, {column})";
        var value = $"CONVERT({type}, {argument})";
        return IncludeNullCandidate($"({text} COLLATE Latin1_General_100_CI_AS = {value} COLLATE Latin1_General_100_CI_AS"
            + $" OR {text} {NonAscii} OR {value} {NonAscii})", column, convertNulls);
    }

    /// <summary>A text expression of a column compared for equality with a bound value (a prompt, a list member or a literal) in a
    /// case-insensitive collation. A column value outside printable ASCII, or a value longer than the converted width, is left to
    /// the runtime; so is every row when <paramref name="guard"/> is bound to 1 (an uncertain prompt value).</summary>
    internal static string StringEquality(string column, XElement? field, string text, string? guard, string value, bool convertNulls)
        => IncludeNullCandidate($"({LengthEscape(field, column)}{(guard is null ? "" : $"@{guard} = 1 OR ")}{text} {NonAscii} OR {text} COLLATE Latin1_General_100_CI_AS = @{value})",
            column, convertNulls);

    /// <summary>
    /// <see cref="StringEquality"/> for a list the request reads back from one value. A membership test inside an OR runs once per
    /// row against the whole list; here every row the guards keep compares as one key the list also holds (a control character),
    /// so the server tests membership with one join. It returns the same rows as the guarded form, and any row whose text equals
    /// that key under the collation as well.
    /// </summary>
    internal static string StringMembership(string column, XElement? field, string text, string? guard, string value, bool convertNulls)
        => $"(CASE WHEN {LengthEscape(field, column)}{(guard is null ? "" : $"@{guard} = 1 OR ")}{text} {NonAscii}{(convertNulls ? $" OR {column} IS NULL" : "")}"
            + $" THEN {MembershipKey} ELSE {text} END COLLATE Latin1_General_100_CI_AS = @{value})";

    /// <summary>The key <see cref="StringMembership"/> gives the rows its guards keep; the list's rowset adds it.</summary>
    internal const string MembershipKey = "NCHAR(31)";

    /// <summary>The direct comparison that replaces <see cref="StringEquality"/> at execution when every bound value is printable
    /// ASCII and the executor compares text columns case-insensitively.</summary>
    internal static string DirectEquality(string column, string value, bool convertNulls) => IncludeNullCandidate($"{column} = @{value}", column, convertNulls);

    /// <summary>A text column ordered against a literal; null when the ordering cannot be sent as a proven superset.
    /// Greater than the empty string drops only NULL and '', which fail in either NULL mode; DATALENGTH keeps blank-only values,
    /// which SQL's own "&gt; ''" would drop.</summary>
    internal static string? StringOrder(string column, string op, string literal)
        => op == ">" && literal.Length == 0 ? $"DATALENGTH({column}) > 0" : null;

    /// <summary>An integral column compared with an integral literal.</summary>
    internal static string? Integral(string column, string op, decimal literal, bool convertNulls)
        => op == "=" && literal == decimal.Truncate(literal) ? IncludeNullCandidate($"{column} = {literal.ToString("0", CultureInfo.InvariantCulture)}", column, convertNulls) : null;

    /// <summary>A Yes/No column compared with true or false.</summary>
    internal static string Boolean(string column, bool value, bool convertNulls) => IncludeNullCandidate($"{column} = {(value ? 1 : 0)}", column, convertNulls);

    /// <summary>The date part of a date or date-time column on or after a typed date prompt.</summary>
    internal static string DateDay(string column, string op, string bound, bool convertNulls)
        => op == ">=" ? IncludeNullCandidate($"CAST({column} AS date) >= @{bound}", column, convertNulls) : throw new ArgumentOutOfRangeException(nameof(op));
}
