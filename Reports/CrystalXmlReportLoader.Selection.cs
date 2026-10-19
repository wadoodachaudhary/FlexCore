using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    /// <summary>
    /// Narrows SQL to a proven superset of positioned record selection. The full
    /// Crystal AST remains on the layout and is always evaluated on returned rows.
    /// </summary>
    private static string PositionedSelectionSql(XElement report, ReportDefinition definition)
    {
        var layout = definition.PositionedLayout!;
        var parameters = definition.Parameters;
        definition.PositionedIntegerSelection = null;
        layout.SqlStringParameters = [];
        if (layout.RecordSelection is null || !layout.RecordSelection.TryGetParameterEqualities(out var equalities)) return "";
        var definitions = report.Element("DataDefinition")?.Element("ParameterFieldDefinitions")?.Elements("ParameterFieldDefinition").ToArray() ?? [];
        var tables = report.Element("Database")?.Element("Tables")?.Elements("Table").ToArray() ?? [];
        var clauses = new List<string>();
        var stringParameters = new List<string>();
        var integerBindings = new List<ReportSqlIntegerSelectionPlan.Binding>();
        foreach (var (fieldReference, parameterReference) in equalities)
        {
            if (ParseCrystalFieldRef(fieldReference) is not { } field || !parameterReference.EndsWith('}')) return "";
            var parameterName = parameterReference[2..^1];
            // Simple identifiers prevent SQL-name ambiguity, including sanitized
            // parameter collisions. Unsupported names keep the complete runtime path.
            if (!SqlSelectionIdentifier(field.Table) || !SqlSelectionIdentifier(field.Field) || !SqlSelectionIdentifier(parameterName)) return "";
            var matches = definitions.Where(p => string.Equals((string?)p.Attribute("Name"), parameterName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || definitions.Count(p => SanitizeParamName((string?)p.Attribute("Name") ?? "").Equals(parameterName, StringComparison.OrdinalIgnoreCase)) != 1) return "";
            var parameter = matches[0];
            var exposed = parameters.Where(p => p.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exposed.Length != 1 || exposed[0].AllowMultiple || exposed[0].AllowRange
                || (string?)parameter.Attribute("ParameterType") != "ReportParameter"
                || string.Equals((string?)parameter.Attribute("EnableAllowMultipleValue"), "true", StringComparison.OrdinalIgnoreCase)
                || (string?)parameter.Attribute("DiscreteOrRangeKind") is not (null or "DiscreteValue")) return "";
            var matchedTables = tables.Where(t => string.Equals((string?)t.Attribute("Alias") ?? (string?)t.Attribute("Name"), field.Table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchedTables.Length != 1 || matchedTables[0].Element("Command") is not null) return "";
            var matchedFields = matchedTables[0].Element("Fields")?.Elements("Field")
                .Where(f => string.Equals((string?)f.Attribute("Name"), field.Field, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (matchedFields.Length != 1) return "";
            var fieldTypeName = (string?)matchedFields[0].Attribute("Type") ?? (string?)matchedFields[0].Attribute("ValueType");
            var fieldType = CrystalValueType.FromXml(fieldTypeName);
            var parameterType = CrystalValueType.FromXml((string?)parameter.Attribute("ParameterValueKind"));
            var declaredType = CrystalValueType.FromXml((string?)parameter.Attribute("ValueType"));
            if (fieldType is null || parameterType is null || declaredType is null
                || fieldType.BaseType != parameterType.BaseType || declaredType.BaseType != parameterType.BaseType) return "";
            // Resolve with Crystal's case-insensitive references, emit the exact
            // authored names so SQL aliases also work under a case-sensitive collation.
            var tableAlias = (string?)matchedTables[0].Attribute("Alias") ?? (string?)matchedTables[0].Attribute("Name");
            var fieldName = (string?)matchedFields[0].Attribute("Name");
            var column = $"[{tableAlias}].[{fieldName}]";
            var argument = "@" + exposed[0].Name;
            if (fieldType.BaseType == CrystalBaseType.Number && IntegralSelectionField(fieldTypeName!))
            {
                var binding = IntegralSelectionBinding(column, exposed[0].Name, fieldTypeName!, matchedFields[0]);
                clauses.Add(binding.GuardedClause);
                integerBindings.Add(binding);
            }
            else if (fieldType.BaseType == CrystalBaseType.String)
            {
                stringParameters.Add(exposed[0].Name);
                // Crystal compares strings OrdinalIgnoreCase (ignoring trailing spaces).
                // Unknown SQL default collations cannot be assumed equivalent. Explicit
                // Latin case-insensitive equality suffices for printable ASCII selectors;
                // either non-ASCII operand deliberately passes through for runtime filtering.
                // Convert before COLLATE so a varchar code-page change cannot erase that guard.
                // This bounds returned ASCII identifiers; no index/performance claim is made.
                var textColumn = $"CONVERT(nvarchar(max), {column})";
                var textArgument = $"CONVERT(nvarchar(max), {argument})";
                clauses.Add($"({textColumn} COLLATE Latin1_General_100_CI_AS = {textArgument} COLLATE Latin1_General_100_CI_AS"
                    + $" OR {textColumn} COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%'"
                    + $" OR {textArgument} COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%')");
            }
            else return ""; // Dates/date coercion, floating-point, currency and unknown types are not proven here.
        }
        // A direct equality with a null operand is not true in Crystal's default
        // null mode; numeric SQL fallback may admit it but runtime still rejects it.
        // AST directives changing null mode were rejected above.
        layout.SqlStringParameters = stringParameters.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (integerBindings.Count > 0)
            definition.PositionedIntegerSelection = new(integerBindings.Distinct().ToArray(), "");
        return string.Join(" AND ", clauses.Distinct(StringComparer.Ordinal));
    }

    // Both the strict scalar path and the complete mixed date/string/list path
    // use the same conservative integer guard and execution-local eligibility.
    private static ReportSqlIntegerSelectionPlan.Binding IntegralSelectionBinding(
        string column, string parameterName, string fieldTypeName, XElement field)
    {
        // Preview seeds can be culture-sensitive strings. Only nonnegative
        // ASCII integer text is narrowed here; uncertain values pass through to
        // the complete runtime AST. Typed values specialize only at execution.
        var argument = "@" + parameterName;
        var numericText = $"CONVERT(nvarchar(max), {argument})";
        var integer = $"TRY_CONVERT(decimal(38,0), {numericText})";
        var guarded = $"({numericText} COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9]%' OR {integer} IS NULL OR {column} = {integer})";
        var range = IntegralSelectionRange(fieldTypeName, field)
            ?? throw new InvalidDataException("Integral selector range was not proven.");
        return new(parameterName, guarded, $"{column} = {argument}", range.Minimum, range.Maximum);
    }

    private static bool SqlSelectionIdentifier(string value) => Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_]*\z");

    private static bool IntegralSelectionField(string name)
        => IntegralSelectionType(name) is "long" or "short" or "byte" or "int8s" or "int8u" or "int16s" or "int16u" or "int32s" or "int32u" or "int64s";

    private static string IntegralSelectionType(string name)
    {
        name = name.ToLowerInvariant();
        if (name.StartsWith("crfieldvaluetype", StringComparison.Ordinal)) name = name["crfieldvaluetype".Length..];
        if (name.StartsWith("xsd:", StringComparison.Ordinal)) name = name[4..];
        if (name.EndsWith("field", StringComparison.Ordinal)) name = name[..^5];
        return name;
    }

    private static (decimal Minimum, decimal Maximum)? IntegralSelectionRange(string name, XElement field)
        => IntegralSelectionType(name) switch
        {
            "byte" or "int8s" => (sbyte.MinValue, sbyte.MaxValue),
            "int8u" => (byte.MinValue, byte.MaxValue),
            "short" or "int16s" => (short.MinValue, short.MaxValue),
            "int16u" => (ushort.MinValue, ushort.MaxValue),
            // Crystal folds native Int64 into Xsd:longField but retains its byte length.
            "long" when (string?)field.Attribute("Length") == "8" || (string?)field.Attribute("NumberOfBytes") == "8" => (long.MinValue, long.MaxValue),
            "long" or "int32s" => (int.MinValue, int.MaxValue),
            "int32u" => (uint.MinValue, uint.MaxValue),
            "int64s" => (long.MinValue, long.MaxValue),
            _ => null
        };
}
