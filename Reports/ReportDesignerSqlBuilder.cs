using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Reports;

internal static class ReportDesignerSqlBuilder
{
    public static string BuildSql(
        IReadOnlyList<ReportDesignerDataTable> tables,
        IReadOnlyList<ReportDesignerField> displayFields,
        IReadOnlyList<ReportDesignerDataLink> links,
        IReadOnlyList<ReportDesignerFilter> filters,
        int topRows = 100)
    {
        var selectedTables = tables
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .GroupBy(TableAlias, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selectedTables.Count == 0)
            return "";

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

        var where = BuildWhere(filters, selectedTables);
        if (!string.IsNullOrWhiteSpace(where))
            sb.AppendLine("WHERE " + where);

        return sb.ToString().TrimEnd();
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

        foreach (var table in tables.Skip(1))
        {
            var tableAlias = TableAlias(table);
            var link = links.FirstOrDefault(candidate => ConnectsJoinedTable(candidate, tableAlias, joined));
            if (link == null)
            {
                sb.AppendLine();
                sb.Append("CROSS JOIN ").Append(QualifiedTable(table)).Append(" AS ").Append(QuoteIdentifier(tableAlias));
                joined.Add(tableAlias);
                continue;
            }

            sb.AppendLine();
            sb.Append(JoinKeyword(link.JoinType))
                .Append(' ')
                .Append(QualifiedTable(table))
                .Append(" AS ")
                .Append(QuoteIdentifier(tableAlias))
                .Append(" ON ")
                .Append(JoinCondition(link));
            joined.Add(tableAlias);
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

    private static string BuildWhere(IReadOnlyList<ReportDesignerFilter> filters, IReadOnlyList<ReportDesignerDataTable> tables)
    {
        var parts = new List<string>();
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field.Name))
                continue;

            var table = ResolveFieldTable(filter.Field, tables);
            if (table == null)
                continue;

            var lhs = $"{QuoteIdentifier(TableAlias(table))}.{QuoteIdentifier(filter.Field.Name)}";
            var value = FormatFilterValue(filter.Value);
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
                _ => $"{lhs} = {value}"
            });
        }

        return string.Join(" AND ", parts);
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
        return string.IsNullOrWhiteSpace(table.Schema)
            ? QuoteIdentifier(table.Name)
            : $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
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

    private static string FormatFilterValue(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return value;

        return "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
