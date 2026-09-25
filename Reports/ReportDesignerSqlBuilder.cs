using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

internal static class ReportDesignerSqlBuilder
{
    internal sealed record Query(string Sql, Dictionary<string, object> Parameters);

    public static string BuildSql(
        IReadOnlyList<ReportDesignerDataTable> tables,
        IReadOnlyList<ReportDesignerField> displayFields,
        IReadOnlyList<ReportDesignerDataLink> links,
        IReadOnlyList<ReportDesignerFilter> filters,
        int topRows = 0) => BuildQuery(tables, displayFields, links, filters, topRows).Sql;

    public static Query BuildQuery(
        IReadOnlyList<ReportDesignerDataTable> tables,
        IReadOnlyList<ReportDesignerField> displayFields,
        IReadOnlyList<ReportDesignerDataLink> links,
        IReadOnlyList<ReportDesignerFilter> filters,
        int topRows = 0)
    {
        var selectedTables = tables
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .GroupBy(TableAlias, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selectedTables.Count == 0)
            return new Query("", new());

        var fields = displayFields.Count > 0
            ? displayFields
            : selectedTables.SelectMany(table => table.Fields).Take(12).ToList();

        var selectFields = fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .Select(field => BuildSelectField(field, selectedTables))
            .Where(sql => !string.IsNullOrWhiteSpace(sql))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectFields.Count == 0)
            selectFields.Add("*");

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        if (topRows > 0)
            sb.Append("TOP ").Append(topRows.ToString(CultureInfo.InvariantCulture)).Append(' ');
        sb.AppendLine(string.Join("," + Environment.NewLine + "       ", selectFields));
        sb.AppendLine(BuildFromAndJoins(selectedTables, links));

        var where = BuildFilterQuery(filters, field =>
        {
            var table = ResolveFieldTable(field, selectedTables)
                ?? throw new InvalidDataException($"Filter source not found: {field.Reference}");
            return $"{QuoteIdentifier(TableAlias(table))}.{QuoteIdentifier(field.Name)}";
        });
        if (!string.IsNullOrWhiteSpace(where.Sql))
            sb.AppendLine("WHERE " + where.Sql);

        return new Query(sb.ToString().TrimEnd(), where.Parameters);
    }

    public static IReadOnlyList<string> GetFieldNamesForTable(
        string tableName,
        IReadOnlyList<ReportDesignerDataTable> tables,
        IReadOnlyList<ReportDesignerField> fallbackFields)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return Array.Empty<string>();

        var table = tables.FirstOrDefault(candidate => TableMatches(candidate, tableName));
        var fields = table?.Fields
            .Select(field => field.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fields is { Count: > 0 })
            return fields;

        return fallbackFields
            .Where(field => string.Equals(field.Table, tableName, StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildSelectField(ReportDesignerField field, IReadOnlyList<ReportDesignerDataTable> tables)
    {
        if (field.IsFormula)
            return "";

        var table = ResolveFieldTable(field, tables);
        if (table == null)
            return "";

        var alias = TableAlias(table);
        return $"{QuoteIdentifier(alias)}.{QuoteIdentifier(field.Name)} AS {QuoteIdentifier($"{alias}_{field.Name}")}";
    }

    private static string BuildFromAndJoins(
        IReadOnlyList<ReportDesignerDataTable> tables,
        IReadOnlyList<ReportDesignerDataLink> links)
    {
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TableAlias(tables[0]) };
        var sb = new StringBuilder();
        sb.Append("FROM ").Append(QualifiedTable(tables[0])).Append(" AS ").Append(QuoteIdentifier(TableAlias(tables[0])));

        var remaining = tables.Skip(1).ToList();
        while (remaining.Count > 0)
        {
            var table = remaining.OrderBy(candidate => links.ToList().FindIndex(link =>
                    string.Equals(link.LeftTable, candidate.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(link.RightTable, candidate.Name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(candidate => links.Any(link => ConnectsJoinedTable(link, TableAlias(candidate), joined)))
                ?? remaining[0];
            var tableAlias = TableAlias(table);
            var connecting = links.Where(candidate => ConnectsJoinedTable(candidate, tableAlias, joined)).ToList();
            if (connecting.Count == 0)
            {
                sb.AppendLine();
                sb.Append("CROSS JOIN ").Append(QualifiedTable(table)).Append(" AS ").Append(QuoteIdentifier(tableAlias));
                joined.Add(tableAlias);
                remaining.Remove(table);
                continue;
            }

            var joinKinds = connecting.Select(link => OrientedJoin(link, tableAlias)).Distinct().ToList();
            var requiredMatch = joinKinds.Contains("INNER JOIN");
            if (joinKinds.Count != 1 && !requiredMatch)
                throw new NotSupportedException($"Mixed join directions for '{tableAlias}' require an explicit SQL command.");
            sb.AppendLine();
            sb.Append(requiredMatch ? "INNER JOIN" : joinKinds[0])
                .Append(' ')
                .Append(QualifiedTable(table))
                .Append(" AS ")
                .Append(QuoteIdentifier(tableAlias))
                .Append(" ON ")
                .Append(string.Join(" AND ", connecting.Select(JoinCondition).Distinct(StringComparer.OrdinalIgnoreCase)));
            joined.Add(tableAlias);
            remaining.Remove(table);
        }

        return sb.ToString();
    }

    private static bool ConnectsJoinedTable(ReportDesignerDataLink link, string tableAlias, HashSet<string> joined)
    {
        return (string.Equals(link.LeftTable, tableAlias, StringComparison.OrdinalIgnoreCase) && joined.Contains(link.RightTable))
            || (string.Equals(link.RightTable, tableAlias, StringComparison.OrdinalIgnoreCase) && joined.Contains(link.LeftTable));
    }

    private static string JoinCondition(ReportDesignerDataLink link)
    {
        return $"{QuoteIdentifier(link.LeftTable)}.{QuoteIdentifier(link.LeftField)} = {QuoteIdentifier(link.RightTable)}.{QuoteIdentifier(link.RightField)}";
    }

    private static string OrientedJoin(ReportDesignerDataLink link, string newAlias)
    {
        var keyword = JoinKeyword(link.JoinType);
        if (!string.Equals(link.LeftTable, newAlias, StringComparison.OrdinalIgnoreCase)) return keyword;
        return keyword switch { "LEFT OUTER JOIN" => "RIGHT OUTER JOIN", "RIGHT OUTER JOIN" => "LEFT OUTER JOIN", _ => keyword };
    }

    internal static Query BuildFilterQuery(IReadOnlyList<ReportDesignerFilter> filters, Func<ReportDesignerField, string> resolve)
    {
        var parts = new List<string>();
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            var lhs = resolve(filter.Field);
            var reference = Regex.Match(filter.Value, @"^\{\?([^}]+)\}$");
            string value;
            if (reference.Success)
                value = "@" + Regex.Replace(reference.Groups[1].Value, @"[^A-Za-z0-9_]", "");
            else
            {
                var name = "__fxFilter" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                value = "@" + name;
                parameters[name] = TypedFilterValue(filter.Value, filter.Field.Type);
            }
            parts.Add(filter.Operator switch
            {
                "is not equal to" => $"{lhs} <> {value}",
                "is less than" => $"{lhs} < {value}",
                "is less than or equal to" => $"{lhs} <= {value}",
                "is greater than" => $"{lhs} > {value}",
                "is greater than or equal to" => $"{lhs} >= {value}",
                "contains" => $"{lhs} LIKE '%' + {value} + '%'",
                "does not contain" => $"{lhs} NOT LIKE '%' + {value} + '%'",
                "begins with" => $"{lhs} LIKE {value} + '%'",
                "ends with" => $"{lhs} LIKE '%' + {value}",
                "is equal to" => $"{lhs} = {value}",
                _ => throw new NotSupportedException($"Unsupported selection operator: {filter.Operator}")
            });
        }

        return new Query(string.Join(" AND ", parts), parameters);
    }

    private static ReportDesignerDataTable? ResolveFieldTable(
        ReportDesignerField field,
        IReadOnlyList<ReportDesignerDataTable> tables)
    {
        if (string.IsNullOrWhiteSpace(field.Table))
            return tables.FirstOrDefault(table => table.Fields.Any(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase)));

        return tables.FirstOrDefault(table => TableMatches(table, field.Table));
    }

    private static bool TableMatches(ReportDesignerDataTable table, string value)
    {
        return string.Equals(TableAlias(table), value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(table.DisplayName, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(table.Name, value, StringComparison.OrdinalIgnoreCase);
    }

    private static string TableAlias(ReportDesignerDataTable table) => table.Name;

    private static string QualifiedTable(ReportDesignerDataTable table)
    {
        if (!string.IsNullOrWhiteSpace(table.CommandText))
            return "(" + table.CommandText.Trim().TrimEnd(';') + ")";
        var sourceName = string.IsNullOrWhiteSpace(table.SourceName) ? table.Name : table.SourceName;
        return string.IsNullOrWhiteSpace(table.Schema)
            ? QuoteIdentifier(sourceName)
            : $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(sourceName)}";
    }

    private static string JoinKeyword(string? joinType)
    {
        return joinType?.Trim().ToLowerInvariant() switch
        {
            "left outer" or "leftouter" => "LEFT OUTER JOIN",
            "right outer" or "rightouter" => "RIGHT OUTER JOIN",
            "full outer" or "fullouter" => "FULL OUTER JOIN",
            _ => "INNER JOIN"
        };
    }

    private static string QuoteIdentifier(string value)
    {
        return "[" + (value ?? "").Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    internal static object TypedFilterValue(string value, string type)
    {
        var kind = type.ToLowerInvariant();
        if (kind.Contains("number") || kind.Contains("decimal") || kind.Contains("currency") || kind.Contains("int") || kind.Contains("long") || kind.Contains("short") || kind.Contains("byte"))
            return decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (kind.Contains("boolean") || kind == "bool") return bool.Parse(value);
        if (kind.Contains("date")) return DateTime.Parse(value, CultureInfo.InvariantCulture);
        if (kind.Contains("time")) return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        return value;
    }
}
