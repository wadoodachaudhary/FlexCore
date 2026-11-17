using System.Globalization;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

internal static partial class ReportDesignerSqlBuilder
{
    internal static Query BuildDocumentQuery(ReportDesignerDocument document, int topRows = 0)
    {
        if (document.DataSources.Select(table => table.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.DataSources.Count)
            throw new InvalidDataException("Duplicate table aliases cannot be generated as SQL.");
        foreach (var link in document.Links)
        {
            if (link.LeftTable.Equals(link.RightTable, StringComparison.OrdinalIgnoreCase)
                || !document.DataSources.Any(table => table.Name.Equals(link.LeftTable, StringComparison.OrdinalIgnoreCase) && table.Fields.Any(field => field.Name.Equals(link.LeftField, StringComparison.OrdinalIgnoreCase)))
                || !document.DataSources.Any(table => table.Name.Equals(link.RightTable, StringComparison.OrdinalIgnoreCase) && table.Fields.Any(field => field.Name.Equals(link.RightField, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("A join references a missing or ambiguous table/field; no link was omitted.");
        }
        var query = BuildQuery(document.DataSources, document.Fields, document.Links, [], topRows);
        if (query.Sql.Length == 0) return query;
        var parameters = query.Parameters;
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, ReportDesignerParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in document.Parameters)
        {
            var name = Regex.Replace(parameter.Name, @"[^A-Za-z0-9_]", "");
            if (name.Length == 0 || name.StartsWith("__fx", StringComparison.OrdinalIgnoreCase) || !names.TryAdd(name, parameter))
                throw new InvalidDataException("Report parameters have an empty, reserved, or ambiguous SQL name.");
        }
        var formulas = new Dictionary<string, CrystalFormula.DesignerSqlValue>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CrystalFormula.DesignerSqlValue Literal(object value)
        {
            // These are parsed authored constants, not prompt values. Keep them
            // self-contained when a user edits/saves the generated text as custom
            // SQL; only editable Filters have persisted fixed-parameter metadata.
            return value switch
            {
                string text => new("N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'", new(CrystalBaseType.String)),
                bool flag => new(flag ? "1" : "0", new(CrystalBaseType.Boolean)),
                decimal number => new(number.ToString(CultureInfo.InvariantCulture), new(CrystalBaseType.Number)),
                _ => throw new NotSupportedException("This authored constant requires the native report engine.")
            };
        }
        CrystalFormula.DesignerSqlValue Resolve(string reference)
        {
            if (reference.StartsWith("{?", StringComparison.Ordinal) && reference.EndsWith('}'))
            {
                var raw = reference[2..^1];
                var name = Regex.Replace(raw, @"[^A-Za-z0-9_]", "");
                if (!names.TryGetValue(name, out var parameter) || !parameter.Name.Equals(raw, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Selection parameter is missing or ambiguous: " + reference);
                var type = CrystalValueType.FromXml(parameter.Type, parameter.AllowMultiple, parameter.AllowRange)
                    ?? throw new NotSupportedException("Selection parameter type is unknown: " + reference);
                if (type.IsRange) throw new NotSupportedException("Range parameters require the native report engine: " + reference);
                required.Add(name);
                return new("@" + name, type);
            }
            if (reference.StartsWith("{@", StringComparison.Ordinal))
            {
                if (formulas.TryGetValue(reference, out var cached)) return cached;
                if (visiting.Count >= 32 || !visiting.Add(reference)) throw new NotSupportedException("Recursive formula cannot be generated as SQL: " + reference);
                try
                {
                    var matches = document.Fields.Where(field => field.IsFormula && field.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("Formula is missing or ambiguous: " + reference);
                    return formulas[reference] = CrystalFormula.Compile(matches[0].Expression, matches[0].Syntax).DesignerSql(Resolve, Literal);
                }
                finally { visiting.Remove(reference); }
            }
            var matchesField = document.DataSources.SelectMany(table => table.Fields.Select(field => (Table: table, Field: field)))
                .Where(item => ("{" + item.Table.Name + "." + item.Field.Name + "}").Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchesField.Length != 1) throw new InvalidDataException("Selection or sort field is missing or ambiguous: " + reference);
            var match = matchesField[0];
            var fieldType = CrystalValueType.FromXml(match.Field.Type) ?? throw new NotSupportedException("Field type is unknown: " + reference);
            return new(QuoteIdentifier(match.Table.Name) + "." + QuoteIdentifier(match.Field.Name), fieldType);
        }
        var where = new List<string>();
        var selection = CrystalFormula.Compile(document.RecordSelectionFormula);
        if (!selection.IsEmpty) where.Add(CrystalFormula.DesignerSqlPredicate(selection.DesignerSql(Resolve, Literal)));
        var filters = BuildFilterQuery(document.Filters, field => Resolve(field.Reference).Sql);
        foreach (var filter in document.Filters)
            if (Regex.IsMatch(filter.Value, @"^\{\?[^}]+\}$")) Resolve(filter.Value);
        foreach (var pair in filters.Parameters) parameters.Add(pair.Key, pair.Value);
        if (filters.Sql.Length > 0) where.Add("(" + filters.Sql + ")");

        var ordered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        void Sort(string reference, string direction)
        {
            var value = CrystalFormula.Compile(reference).DesignerSql(Resolve, Literal);
            if (value.Type.IsArray || value.Type.IsRange || value.Predicate) throw new NotSupportedException("This sort requires the native report engine: " + reference);
            if (ordered.Add(value.Sql)) order.Add(value.Sql + (direction.StartsWith("Descending", StringComparison.OrdinalIgnoreCase) ? " DESC" : " ASC"));
        }
        foreach (var group in document.Groups)
        {
            if (group.IsHierarchical) throw new NotSupportedException("Hierarchical sorting requires the native report engine.");
            Sort(group.Condition, group.SortDirection);
        }
        // GroupSortField entries describe the groups already emitted above.
        // Their display-field names may collide with formula names; use the
        // authoritative group Condition, just as the report loader does.
        foreach (var sort in document.Sorts)
        {
            if (sort.SortType == "RecordSortField") Sort(sort.Field.Reference, sort.Direction);
            else if (sort.SortType != "GroupSortField" || document.Groups.Count == 0)
                throw new NotSupportedException("This sort requires the native report engine: " + sort.Field.Reference);
        }
        var sql = query.Sql + (where.Count == 0 ? "" : Environment.NewLine + "WHERE " + string.Join(" AND ", where))
            + (order.Count == 0 ? "" : Environment.NewLine + "ORDER BY " + string.Join(", ", order));
        return new(sql, parameters)
        {
            RequiredParameters = required.ToArray(),
            Notice = "Generated SQL includes the supported authored selection and ordering. Prompt values are not supplied here; multiple-value prompts expand at report execution. The native report engine retains its full selection, null/culture rules and sorting."
        };
    }
}
