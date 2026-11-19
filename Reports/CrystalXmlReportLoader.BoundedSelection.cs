using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    private static string PositionedBoundedSelectionSql(XElement report, ReportDefinition definition, string? schemaPrefix)
    {
        definition.PositionedSqlSelection = null;
        var layout = definition.PositionedLayout!;
        var scalar = PositionedSelectionSql(report, definition, schemaPrefix);
        if (scalar.Length > 0 || layout.RecordSelection is null
            || !layout.RecordSelection.TryGetBoundedSelections(out var selections, out var residuals)) return scalar;

        var definitions = report.Element("DataDefinition")?.Element("ParameterFieldDefinitions")?.Elements("ParameterFieldDefinition").ToArray() ?? [];
        var tables = report.Element("Database")?.Element("Tables")?.Elements("Table").ToArray() ?? [];
        var formulaDefinitions = report.Element("DataDefinition")?.Element("FormulaFieldDefinitions")?.Elements("FormulaFieldDefinition").ToArray() ?? [];
        if (definitions.Any(parameter => ((string?)parameter.Attribute("Name") ?? "").StartsWith(ReportSqlSelectionPlan.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            || definition.Parameters.Any(parameter => parameter.Name.StartsWith(ReportSqlSelectionPlan.ReservedPrefix, StringComparison.OrdinalIgnoreCase))) return "";
        var plan = new ReportSqlSelectionPlan();
        plan.DeclaredNames.UnionWith(definition.Parameters.Select(parameter => parameter.Name));
        var clauses = new List<string>();
        var integers = new List<ReportSqlIntegerSelectionPlan.Binding>();
        var texts = new List<ReportSqlTextSelectionPlan.Binding>();

        (string Sql, CrystalBaseType Type, string TypeName, XElement Metadata, ReportTextColumn? Column)? Field(string reference)
        {
            if (ParseCrystalFieldRef(reference) is not { } parsed || !SqlSelectionIdentifier(parsed.Table) || !SqlSelectionIdentifier(parsed.Field)) return null;
            var matchedTables = tables.Where(table => string.Equals((string?)table.Attribute("Alias") ?? (string?)table.Attribute("Name"), parsed.Table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchedTables.Length != 1 || matchedTables[0].Element("Command") is not null) return null;
            var fields = matchedTables[0].Element("Fields")?.Elements("Field").Where(field => string.Equals((string?)field.Attribute("Name"), parsed.Field, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (fields.Length != 1 || CrystalValueType.FromXml((string?)fields[0].Attribute("Type") ?? (string?)fields[0].Attribute("ValueType")) is not { } type) return null;
            return ($"[{(string?)matchedTables[0].Attribute("Alias") ?? (string?)matchedTables[0].Attribute("Name")}].[{(string?)fields[0].Attribute("Name")}]", type.BaseType, (string?)fields[0].Attribute("Type") ?? (string?)fields[0].Attribute("ValueType") ?? "", fields[0], TextColumn(matchedTables[0], fields[0], schemaPrefix));
        }

        // A conjunction A AND R can safely be narrowed by A while the full
        // runtime formula still evaluates R. Accept only field/literal shapes
        // whose pure read-time behavior and metadata are explicit; the typed
        // terms among them join the SQL below, after the prompt terms.
        foreach (var residual in residuals)
        {
            if (Field(residual.Field) is not { } field) return "";
            if (residual.Operator == ">")
            {
                if (field.Type != CrystalBaseType.String || residual.Literal is not string { Length: 0 }) return "";
                continue;
            }
            if (residual.Operator != "=") return "";
            if (field.Type == CrystalBaseType.String && residual.Literal is string
                || field.Type == CrystalBaseType.Boolean && residual.Literal is bool) continue;
            if (field.Type == CrystalBaseType.Number && IntegralSelectionField(field.TypeName)
                && residual.Literal is decimal number && number == decimal.Truncate(number)
                && IntegralSelectionRange(field.TypeName, field.Metadata) is { } range
                && number >= range.Minimum && number <= range.Maximum) continue;
            return "";
        }

        foreach (var selection in selections)
        {
            if (!selection.Parameter.EndsWith('}')) return "";
            var name = selection.Parameter[2..^1];
            if (!SqlSelectionIdentifier(name)) return "";
            var matches = definitions.Where(parameter => string.Equals((string?)parameter.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var exposed = definition.Parameters.Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || exposed.Length != 1
                || definitions.Count(parameter => SanitizeParamName((string?)parameter.Attribute("Name") ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)) != 1) return "";
            var parameter = matches[0];
            if ((string?)parameter.Attribute("ParameterType") != "ReportParameter" || exposed[0].AllowRange
                || (string?)parameter.Attribute("DiscreteOrRangeKind") is not (null or "DiscreteValue")) return "";
            name = exposed[0].Name;
            var nativeType = CrystalValueType.FromXml((string?)parameter.Attribute("ParameterValueKind"))?.BaseType;
            var declaredType = CrystalValueType.FromXml((string?)parameter.Attribute("ValueType"))?.BaseType;
            if (selection.Date)
            {
                if (nativeType != CrystalBaseType.Date || declaredType != CrystalBaseType.Date || exposed[0].AllowMultiple
                    || Field(selection.Field) is not { Type: CrystalBaseType.Date or CrystalBaseType.DateTime } field) return "";
                plan.Dates.Add(name);
                clauses.Add(ReportSelectionSql.DateDay(field.Sql, ">=", name, layout.ConvertDatabaseNullToDefault));
                continue;
            }
            if (nativeType == CrystalBaseType.Number && declaredType == CrystalBaseType.Number)
            {
                if (exposed[0].AllowMultiple
                    || string.Equals((string?)parameter.Attribute("EnableAllowMultipleValue"), "true", StringComparison.OrdinalIgnoreCase)
                    || Field(selection.Field) is not { Type: CrystalBaseType.Number } field
                    || !IntegralSelectionField(field.TypeName)) return "";
                var binding = IntegralSelectionBinding(field.Sql, name, field.TypeName, field.Metadata, layout.ConvertDatabaseNullToDefault);
                clauses.Add(binding.GuardedClause);
                integers.Add(binding);
                plan.Integers.Add(name);
                continue;
            }
            if (nativeType != CrystalBaseType.String || declaredType != CrystalBaseType.String) return "";
            var fieldReference = selection.Field;
            string? replacement = null;
            if (fieldReference.StartsWith("{@", StringComparison.Ordinal))
            {
                var formulas = formulaDefinitions.Where(formula => string.Equals((string?)formula.Attribute("FormulaName"), fieldReference, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (formulas.Length != 1 || !layout.Formulas.TryGetValue(fieldReference, out var formula)
                    || !formula.TryGetNullOrEmptyStringReplacement(out fieldReference, out replacement)) return "";
            }
            if (Field(fieldReference) is not { Type: CrystalBaseType.String } textField) return "";
            var text = $"CONVERT({ReportSelectionSql.TextType(textField.Metadata)}, {textField.Sql})";
            var index = plan.Strings.Count;
            var guard = ReportSqlSelectionPlan.ReservedPrefix + "Guard" + index;
            var constant = replacement is null ? null : ReportSqlSelectionPlan.ReservedPrefix + "Value" + index;
            if (constant is not null)
                text = $"(CASE WHEN {textField.Sql} IS NULL OR {text} COLLATE Latin1_General_100_BIN2 = N'' THEN @{constant} ELSE {text} END)";
            var guarded = ReportSelectionSql.StringEquality(textField.Sql, textField.Metadata, text, guard, name, layout.ConvertDatabaseNullToDefault);
            clauses.Add(guarded);
            // Printable-ASCII values compare the whole column directly at execution (no conversion, no truncation, an index can serve it)
            // when the column is a bounded character column; the null-or-empty replacement compares a CASE expression and stays guarded.
            if (constant is null && ReportSelectionSql.DirectTextComparable(textField.Metadata))
                texts.Add(new([name], guarded, ReportSelectionSql.DirectEquality(textField.Sql, name, layout.ConvertDatabaseNullToDefault)) { Column = textField.Column });
            plan.Strings.Add(new(name, guard, constant, replacement, exposed[0].AllowMultiple, guarded,
                ReportSelectionSql.StringMembership(textField.Sql, textField.Metadata, text, guard, name, layout.ConvertDatabaseNullToDefault)));
        }
        // Crystal writes the record selection's typed literal terms into its own
        // query, so they narrow the SQL here too; the full formula still decides
        // every returned row. "Greater than the empty string" is sent as a superset:
        // it drops only NULL and '', which fail in either null mode, and DATALENGTH
        // keeps blank-only values, which SQL's own "> ''" would drop.
        foreach (var residual in residuals)
        {
            if (Field(residual.Field) is not { } field) continue;
            if (residual.Operator == ">" && field.Type == CrystalBaseType.String && residual.Literal is string { Length: 0 } empty)
            {
                clauses.Add(ReportSelectionSql.StringOrder(field.Sql, ">", empty)!);
                continue;
            }
            if (residual.Operator != "=") continue;
            if (residual.Literal is string text)
            {
                // A literal outside printable ASCII is as uncertain as a guarded prompt value: the runtime compares it.
                if (text.Any(character => character is < ' ' or > '~')) continue;
                var column = $"CONVERT({ReportSelectionSql.TextType(field.Metadata)}, {field.Sql})";
                var literal = ReportSqlSelectionPlan.ReservedPrefix + "Literal" + plan.Literals.Count;
                plan.Literals.Add((literal, text));
                var guarded = ReportSelectionSql.StringEquality(field.Sql, field.Metadata, column, null, literal, layout.ConvertDatabaseNullToDefault);
                clauses.Add(guarded);
                if (ReportSelectionSql.DirectTextComparable(field.Metadata))
                    texts.Add(new([literal], guarded, ReportSelectionSql.DirectEquality(field.Sql, literal, layout.ConvertDatabaseNullToDefault)) { Column = field.Column });
            }
            else if (residual.Literal is bool flag)
                clauses.Add(ReportSelectionSql.Boolean(field.Sql, flag, layout.ConvertDatabaseNullToDefault));
            else if (residual.Literal is decimal number)
                clauses.Add(ReportSelectionSql.Integral(field.Sql, "=", number, layout.ConvertDatabaseNullToDefault)!);
        }
        // Inline literals need no plan; a plan exists only when execution must bind prompt values or reserved names.
        if (plan.Dates.Count + plan.Integers.Count + plan.Strings.Count + plan.Literals.Count > 0) definition.PositionedSqlSelection = plan;
        if (texts.Count > 0) definition.PositionedTextSelection = new(texts, "");
        if (integers.Count > 0)
            definition.PositionedIntegerSelection = new(integers.Distinct().ToArray(), "");
        return string.Join(" AND ", clauses.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// The server-side ordering the original engine asks for: the leading group and record-sort keys that are plain database
    /// fields, stopping at the first key the engine computes itself (a formula, a summary, a specified order). The layout sorts
    /// the rows again, so the clause only decides the order of rows with equal keys, which is what it does for Crystal too.
    /// </summary>
    private static string PositionedOrderBySql(XElement report, ReportDefinition definition)
    {
        var layout = definition.PositionedLayout!;
        var tables = report.Element("Database")?.Element("Tables")?.Elements("Table").ToArray() ?? [];
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = layout.Document.Groups.Select(group => (Reference: group.Condition, Direction: group.SortDirection))
            .Concat(layout.Document.Sorts.Where(sort => sort.SortType == "RecordSortField").Select(sort => (Reference: sort.Field.Reference, Direction: sort.Direction)));
        foreach (var (reference, direction) in keys)
        {
            var descending = direction.StartsWith("Descending", StringComparison.OrdinalIgnoreCase);
            if (!descending && !direction.StartsWith("Ascending", StringComparison.OrdinalIgnoreCase)) break;
            if (ParseCrystalFieldRef(reference) is not { } field || !SqlSelectionIdentifier(field.Table) || !SqlSelectionIdentifier(field.Field)) break;
            var matchedTables = tables.Where(table => string.Equals((string?)table.Attribute("Alias") ?? (string?)table.Attribute("Name"), field.Table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchedTables.Length != 1 || matchedTables[0].Element("Command") is not null) break;
            var columns = matchedTables[0].Element("Fields")?.Elements("Field").Where(column => string.Equals((string?)column.Attribute("Name"), field.Field, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (columns.Length != 1) break;
            var column = $"[{(string?)matchedTables[0].Attribute("Alias") ?? (string?)matchedTables[0].Attribute("Name")}].[{(string?)columns[0].Attribute("Name")}]";
            if (seen.Add(column)) parts.Add(column + (descending ? " DESC" : ""));
        }
        return parts.Count == 0 ? "" : " ORDER BY " + string.Join(", ", parts);
    }
}
