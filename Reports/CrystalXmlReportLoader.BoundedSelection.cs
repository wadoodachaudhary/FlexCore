using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    private static string PositionedBoundedSelectionSql(XElement report, ReportDefinition definition)
    {
        definition.PositionedSqlSelection = null;
        var layout = definition.PositionedLayout!;
        var scalar = PositionedSelectionSql(report, definition);
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

        (string Sql, CrystalBaseType Type, string TypeName, XElement Metadata)? Field(string reference)
        {
            if (ParseCrystalFieldRef(reference) is not { } parsed || !SqlSelectionIdentifier(parsed.Table) || !SqlSelectionIdentifier(parsed.Field)) return null;
            var matchedTables = tables.Where(table => string.Equals((string?)table.Attribute("Alias") ?? (string?)table.Attribute("Name"), parsed.Table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchedTables.Length != 1 || matchedTables[0].Element("Command") is not null) return null;
            var fields = matchedTables[0].Element("Fields")?.Elements("Field").Where(field => string.Equals((string?)field.Attribute("Name"), parsed.Field, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (fields.Length != 1 || CrystalValueType.FromXml((string?)fields[0].Attribute("Type") ?? (string?)fields[0].Attribute("ValueType")) is not { } type) return null;
            return ($"[{(string?)matchedTables[0].Attribute("Alias") ?? (string?)matchedTables[0].Attribute("Name")}].[{(string?)fields[0].Attribute("Name")}]", type.BaseType, (string?)fields[0].Attribute("Type") ?? (string?)fields[0].Attribute("ValueType") ?? "", fields[0]);
        }

        // A conjunction A AND R can safely be narrowed by A while the full
        // runtime formula still evaluates R. Accept only field/literal shapes
        // whose pure read-time behavior and metadata are explicit. These terms
        // are deliberately not translated into SQL (including null handling).
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
                clauses.Add($"CAST({field.Sql} AS date) >= @{name}");
                continue;
            }
            if (nativeType == CrystalBaseType.Number && declaredType == CrystalBaseType.Number)
            {
                if (exposed[0].AllowMultiple
                    || string.Equals((string?)parameter.Attribute("EnableAllowMultipleValue"), "true", StringComparison.OrdinalIgnoreCase)
                    || Field(selection.Field) is not { Type: CrystalBaseType.Number } field
                    || !IntegralSelectionField(field.TypeName)) return "";
                var binding = IntegralSelectionBinding(field.Sql, name, field.TypeName, field.Metadata);
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
            var text = $"CONVERT(nvarchar(max), {textField.Sql})";
            var index = plan.Strings.Count;
            var guard = ReportSqlSelectionPlan.ReservedPrefix + "Guard" + index;
            var constant = replacement is null ? null : ReportSqlSelectionPlan.ReservedPrefix + "Value" + index;
            if (constant is not null)
                text = $"(CASE WHEN {textField.Sql} IS NULL OR {text} COLLATE Latin1_General_100_BIN2 = N'' THEN @{constant} ELSE {text} END)";
            clauses.Add($"(@{guard} = 1 OR {text} COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%' OR {text} COLLATE Latin1_General_100_CI_AS = @{name})");
            plan.Strings.Add(new(name, guard, constant, replacement, exposed[0].AllowMultiple));
        }
        definition.PositionedSqlSelection = plan;
        if (integers.Count > 0)
            definition.PositionedIntegerSelection = new(integers.Distinct().ToArray(), "");
        return string.Join(" AND ", clauses.Distinct(StringComparer.Ordinal));
    }
}
