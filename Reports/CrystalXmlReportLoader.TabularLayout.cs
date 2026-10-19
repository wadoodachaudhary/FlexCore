using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    private static IEnumerable<(string Name, string Text, string Syntax)> ConditionEntries(IEnumerable<XElement> containers)
    {
        foreach (var container in containers)
        {
            var syntax = (string?)container.Attribute("Syntax") ?? "Crystal";
            foreach (var attribute in container.Attributes().Where(a => a.Name.LocalName != "Syntax"))
                yield return (attribute.Name.LocalName, attribute.Value, syntax);
            foreach (var child in container.Elements())
                yield return ((string?)child.Attribute("ConditionFormulaType") ?? child.Name.LocalName,
                    (string?)child.Attribute("Text") ?? child.Value, (string?)child.Attribute("Syntax") ?? syntax);
        }
    }

    private static double? ReadNoGrowHeight(XElement field, List<string>? diagnostics)
    {
        if (!bool.TryParse((string?)field.Element("ObjectFormat")?.Attribute("EnableCanGrow"), out var grows) || grows) return null;
        var height = ParseInt(field.Attribute("Height"));
        if (height <= 0) return null;
        foreach (var condition in ConditionEntries(field.Descendants().Where(e => e.Name.LocalName.EndsWith("ConditionFormulas", StringComparison.Ordinal)))
            .Where(c => c.Name.Equals("EnableCanGrow", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Text)))
        {
            try { if (CrystalFormula.Compile(condition.Text, condition.Syntax).IsEmpty) continue; }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
            diagnostics?.Add($"{(string?)field.Attribute("Name")}: conditional CanGrow is not applied by the tabular renderer; the field remains uncapped.");
            return null;
        }
        return height / 15d;
    }

    private static void AddTabularLayout(ReportDefinition definition, XElement report, List<string> selectFields,
        List<ReportColumn> columns, List<ReportParameter> parameters, bool usesDistinct, bool customSql)
    {
        var sections = report.Descendants("Area").Where(a => (string?)a.Attribute("Kind") == "Detail")
            .SelectMany(a => a.Elements("Sections").Elements("Section").Where(s => (string?)s.Attribute("Kind") == "Detail")).ToList();
        if (sections.Count == 0) return;
        IEnumerable<XElement> Containers(XElement source, string format) => source.Elements().Where(e => e.Name.LocalName.EndsWith("ConditionFormulas", StringComparison.Ordinal))
            .Concat(source.Elements(format).Descendants().Where(e => e.Name.LocalName.EndsWith("ConditionFormulas", StringComparison.Ordinal)));
        bool HasSuppression(XElement source, string format) => AttrIsTrue(source.Element(format), "EnableSuppress")
            || ConditionEntries(Containers(source, format)).Any(c => c.Name.Equals("EnableSuppress", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Text));
        if (!sections.Any(s => HasSuppression(s, "SectionFormat") || HasSuppression(s.Ancestors("Area").First(), "AreaFormat"))) return;
        if (sections.Count != 1 || usesDistinct || customSql)
        {
            definition.RuntimeDiagnostics.Add("Tabular detail suppression is not applied to multiple detail sections, DISTINCT summary/drill queries or custom SQL; their rows remain visible.");
            return;
        }
        try
        {
            var section = sections[0];
            var area = section.Ancestors("Area").First();
            var formulas = new Dictionary<string, CrystalFormula>(StringComparer.OrdinalIgnoreCase);
            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parameterBindings = new Dictionary<string, ReportParameter>(StringComparer.OrdinalIgnoreCase);
            var types = new Dictionary<string, CrystalValueType?>(StringComparer.OrdinalIgnoreCase);
            var fieldSources = report.Elements("Database").Elements("Tables").Elements("Table")
                .SelectMany(t => t.Elements("Fields").Elements("Field").Select(f => new
                {
                    Table = (string?)t.Attribute("Alias") ?? (string?)t.Attribute("Name") ?? "",
                    Field = (string?)f.Attribute("Name") ?? "", Type = CrystalValueType.FromXml((string?)f.Attribute("Type"))
                })).Where(f => f.Table.Length > 0 && f.Field.Length > 0)
                .ToDictionary(f => "{" + f.Table + "." + f.Field + "}", StringComparer.OrdinalIgnoreCase);
            var data = report.Element("DataDefinition");
            var namedSources = (data?.Elements("FormulaFieldDefinitions").Elements("FormulaFieldDefinition") ?? [])
                .ToDictionary(f => (string?)f.Attribute("FormulaName") ?? "{@" + (string?)f.Attribute("Name") + "}", StringComparer.OrdinalIgnoreCase);
            var parameterSources = (data?.Elements("ParameterFieldDefinitions").Elements("ParameterFieldDefinition") ?? [])
                .Where(p => (string?)p.Attribute("ParameterType") == "ReportParameter")
                .ToDictionary(p => "{?" + (string?)p.Attribute("Name") + "}", StringComparer.OrdinalIgnoreCase);
            var projections = new List<string>();
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aliases = columns.Select(c => c.Field).Concat(selectFields).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
            void Visit(string reference)
            {
                reference = reference.Trim();
                if (active.Contains(reference)) throw new NotSupportedException("A detail-suppression formula has a cyclic dependency.");
                if (!visited.Add(reference)) return;
                if (visited.Count > 2048 || active.Count >= 64) throw new NotSupportedException("Detail-suppression formula dependency limit exceeded.");
                active.Add(reference);
                try
                {
                    if (namedSources.TryGetValue(reference, out var named))
                    {
                        var formula = CrystalFormula.Compile(named.Value, (string?)named.Attribute("Syntax") ?? "Crystal");
                        ValidateTabularCondition(formula);
                        formulas.Add(reference, formula);
                        types[reference] = CrystalValueType.FromXml((string?)named.Attribute("ValueType"));
                        foreach (var dependency in formula.References) Visit(dependency);
                    }
                    else if (parameterSources.TryGetValue(reference, out var parameterSource))
                    {
                        var name = SanitizeParamName((string?)parameterSource.Attribute("Name") ?? "");
                        var parameter = parameters.SingleOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new NotSupportedException($"Detail-suppression parameter '{name}' is not available in the main report.");
                        parameterBindings[reference] = parameter;
                        types[reference] = CrystalValueType.FromXml((string?)parameterSource.Attribute("ParameterValueKind"), parameter.AllowMultiple, parameter.AllowRange);
                    }
                    else if (fieldSources.TryGetValue(reference, out var field))
                    {
                        var alias = field.Table + "_" + field.Field;
                        if (!selectFields.Contains(alias, StringComparer.OrdinalIgnoreCase))
                        {
                            var index = bindings.Count;
                            do { alias = "__fxTabularCondition" + index++; } while (!aliases.Add(alias) || selectFields.Any(s => s.Contains(" AS " + Quote(alias), StringComparison.OrdinalIgnoreCase)));
                            projections.Add(Quote(field.Table) + "." + Quote(field.Field) + " AS " + Quote(alias));
                        }
                        bindings[reference] = alias;
                        types[reference] = field.Type;
                    }
                    else throw new NotSupportedException($"Detail-suppression reference '{reference}' has no main-report field, parameter or formula definition.");
                }
                finally { active.Remove(reference); }
            }
            ReportTabularLayout.Suppression Rule(XElement source, string format)
            {
                var fixedValue = AttrIsTrue(source.Element(format), "EnableSuppress");
                var entry = ConditionEntries(Containers(source, format)).LastOrDefault(c => c.Name.Equals("EnableSuppress", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(entry.Text)) return new(fixedValue, null);
                var formula = CrystalFormula.Compile(entry.Text, entry.Syntax);
                if (formula.IsEmpty) return new(fixedValue, null);
                ValidateTabularCondition(formula);
                foreach (var dependency in formula.References) Visit(dependency);
                return new(fixedValue, formula);
            }
            var areaRule = Rule(area, "AreaFormat");
            var sectionRule = Rule(section, "SectionFormat");
            // Publish only after every dependency is safe: an unsupported rule must not change SQL cardinality or hide rows.
            definition.TabularLayout = new(areaRule, sectionRule, bindings, formulas, parameterBindings, types);
            selectFields.AddRange(projections);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            definition.RuntimeDiagnostics.Add("Tabular detail suppression was not applied: " + error.Message);
        }
    }

    private static void ValidateTabularCondition(CrystalFormula formula)
    {
        if (formula.UsesVariables || formula.UsesAggregates || formula.UsesRecordContext || formula.UsesPageContext
            || formula.UsesEvaluationDirectives || formula.UnknownSymbols.Count > 0 || formula.CustomFunctions.Count > 0)
            throw new NotSupportedException("The detail condition requires variables, aggregates, page/group/record context or unsupported functions; this tabular visibility pass supports row-local formulas only.");
    }
}
