using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    private ReportDefinition LoadPositionedSource(XElement report, string path, HashSet<string> ancestry, int depth)
    {
        var identity = path + "#" + (string?)report.Attribute("Name");
        if (depth > 8 || !ancestry.Add(identity)) throw new InvalidDataException("Circular or excessively nested subreport definition.");
        try
        {
            var design = ReportDesignerXmlSerializer.FromXml(report.ToString(), Path.GetFileName(path), path);
            var definition = LoadInternalFromReport(new XElement(report), path, null, positionedDesign: design);
            foreach (var element in design.Elements.Where(e => e.Kind == "Subreport"))
            {
                var source = design.SourceObjects.GetValueOrDefault(element.SourceKey);
                if (Flag(source, "EnableOnDemand")) continue;
                var child = report.Elements("SubReports").Elements("Report").FirstOrDefault(r =>
                    string.Equals((string?)r.Attribute("Name"), element.SubreportName, StringComparison.OrdinalIgnoreCase));
                var childPath = path;
                if (child is null)
                {
                    var hint = (string?)source?.Attribute("SubreportXmlPath") ?? (string?)report.Elements("SubreportReferences").Elements("SubreportReference")
                        .FirstOrDefault(r => (string?)r.Attribute("Name") == element.SubreportName)?.Attribute("FileName") ?? "";
                    childPath = ResolveSubreportXmlPath(path, hint, element.SubreportName);
                    if (childPath.Length > 0)
                    {
                        var directory = Path.GetFullPath(Path.GetDirectoryName(path)!);
                        var relative = Path.GetRelativePath(directory, childPath);
                        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                            throw new InvalidDataException("Subreport path leaves its report package.");
                        var check = childPath;
                        while (!check.Equals(directory, StringComparison.Ordinal))
                        {
                            if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Subreport package cannot contain symbolic links.");
                            check = Path.GetDirectoryName(check)!;
                        }
                        child = XDocument.Load(childPath).Root;
                    }
                }
                if (child is null)
                {
                    definition.RuntimeDiagnostics.Add($"{element.Name}: inline subreport definition '{element.SubreportName}' is missing.");
                    continue;
                }
                var links = child.Elements("SubReportLinks").Elements("SubReportLink").Select(link =>
                {
                    var childReference = (string?)link.Attribute("SubreportFieldName") ?? "";
                    if (ParseCrystalFieldRef(childReference) is { } parsed)
                    {
                        var field = ResolveSubreportLinkField(child, parsed);
                        childReference = "{" + field.Table + "." + field.Field + "}";
                    }
                    return new ReportLayoutParameterLink((string?)link.Attribute("LinkedParameterName") ?? "",
                        (string?)link.Attribute("MainReportFieldName") ?? "", childReference);
                }).ToList();
                var childDefinition = LoadPositionedSource(child, childPath, ancestry, depth + 1);
                definition.PositionedLayout!.Subreports[element.Id] = new(childDefinition, links);
                foreach (var link in links)
                {
                    AddProjection(definition.PositionedLayout, link.MainField);
                    AddProjection(childDefinition.PositionedLayout!, link.ChildField);
                }
                RefreshSql(childDefinition, child);
            }
            RefreshSql(definition, report);
            return definition;
        }
        finally { ancestry.Remove(identity); }
    }

    private void RefreshSql(ReportDefinition definition, XElement report)
    {
        report = new XElement(report);
        report.Elements("SubReports").Remove();
        var tables = ParseTables(report);
        var projections = definition.PositionedLayout!.Projections;
        definition.Sql = tables.Count == 0 ? "SELECT 1 AS [__fxStaticReport]"
            : BuildSql(tables, ParseTableLinks(report), "", "", projections.Count == 0 ? ["1 AS [__fxStaticReport]"] : projections);
    }

    private static ReportPositionedLayout BuildPositionedLayout(ReportDesignerDocument document, XElement report, List<string> diagnostics)
    {
        var layout = new ReportPositionedLayout { Document = document };
        foreach (var field in document.Fields.Where(f => f.IsFormula))
        {
            try { layout.Formulas[field.Reference] = CrystalFormula.Compile(field.Expression, field.Syntax, field.Type.Contains("string", StringComparison.OrdinalIgnoreCase) ? "" : 0m); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            { layout.FormulaErrors[field.Reference] = ex.Message; }
        }
        var data = report.Element("DataDefinition");
        if (data?.Element("RecordSelectionFormula")?.Value is { Length: > 0 } recordSelection) layout.RecordSelection = CrystalFormula.Compile(recordSelection);
        if (data?.Element("GroupSelectionFormula")?.Value is { Length: > 0 } groupSelection) layout.GroupSelection = CrystalFormula.Compile(groupSelection);
        foreach (var summary in data?.Descendants("SummaryFieldDefinition") ?? [])
        {
            var name = (string?)summary.Attribute("FormulaName") ?? "";
            var refs = Regex.Matches(name, @"\{[^}]+\}");
            layout.Summaries[name] = new((string?)summary.Attribute("Operation") ?? "Sum", (string?)summary.Attribute("SummarizedField") ?? "", refs.Count > 1 ? refs[1].Value : "");
        }
        foreach (var running in data?.Descendants("RunningTotalFieldDefinition") ?? [])
        {
            if ((string?)running.Attribute("EvaluationConditionType") != "NoCondition" || (string?)running.Attribute("ResetConditionType") != "NoCondition")
            { diagnostics.Add($"{(string?)running.Attribute("Name")}: conditional running-total evaluation/reset is not implemented."); continue; }
            layout.RunningTotals[(string?)running.Attribute("FormulaName") ?? ""] = new((string?)running.Attribute("Operation") ?? "Sum", (string?)running.Attribute("SummarizedField") ?? "", "");
        }
        foreach (var section in document.Sections)
        {
            if (!document.SourceSections.TryGetValue(section.SourceKey, out var xml)) continue;
            var area = xml.Parent?.Parent;
            var format = area?.Element("AreaFormat");
            var repeat = area?.Element("GroupAreaFormat") ?? format?.Element("GroupAreaFormat");
            var sections = xml.Parent?.Elements("Section").ToList() ?? [];
            layout.Areas[section.Id] = new(Flag(format, "EnableSuppress"), Flag(repeat, "EnableRepeatGroupHeader"),
                ReferenceEquals(sections.FirstOrDefault(), xml) && Flag(format, "EnableNewPageBefore"), ReferenceEquals(sections.LastOrDefault(), xml) && Flag(format, "EnableNewPageAfter"), Flag(format, "EnableHideForDrillDown"));
            layout.SectionConditions[section.Id] = ReadConditions(xml.Elements().Where(e => e.Name.LocalName.Contains("ConditionFormulas"))
                .Concat(xml.Elements("SectionFormat").Descendants().Where(e => e.Name.LocalName.Contains("ConditionFormulas"))), diagnostics);
            layout.AreaConditions[section.Id] = ReadConditions((area?.Elements().Where(e => e.Name.LocalName.Contains("ConditionFormulas")) ?? [])
                .Concat(format?.Descendants().Where(e => e.Name.LocalName.Contains("ConditionFormulas")) ?? []), diagnostics);
            foreach (var element in section.Elements)
                if (document.SourceObjects.TryGetValue(element.SourceKey, out var objectXml))
                    layout.ObjectConditions[element.Id] = ReadConditions(objectXml.Descendants().Where(e => e.Name.LocalName.EndsWith("ConditionFormulas")), diagnostics);
            if (section.ResetPageNumberAfter) diagnostics.Add($"{section.Name}: page-number resets are not yet evaluated.");
        }
        var references = document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "").Append(e.HighlightRule.FieldName))
            .Concat(document.Groups.Select(g => g.Condition)).Concat(document.Sorts.Select(s => s.Field.Reference))
            .Concat(layout.Summaries.Values.Concat(layout.RunningTotals.Values).Select(s => s.Field))
            .Concat(layout.RecordSelection?.References ?? []).Concat(layout.GroupSelection?.References ?? [])
            .Concat(layout.SectionConditions.Values.Concat(layout.AreaConditions.Values).Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values).SelectMany(f => f.References))
            .Concat(report.Descendants("SubReportLink").Select(l => (string?)l.Attribute("MainReportFieldName") ?? ""))
            .Concat(report.Elements("SubReportLinks").Elements("SubReportLink").Select(l => (string?)l.Attribute("SubreportFieldName") ?? ""));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(references);
        while (pending.TryDequeue(out var reference))
        {
            reference = reference.Trim();
            if (reference.Length == 0 || !seen.Add(reference)) continue;
            if (layout.Formulas.TryGetValue(reference, out var formula))
            {
                if (formula.UsesEvaluationDirectives) diagnostics.Add($"{reference}: Crystal's complete read/print-pass scheduling is not implemented; native evaluation uses dependency and band order.");
                foreach (var dependency in formula.References) pending.Enqueue(dependency);
                continue;
            }
            if (layout.FormulaErrors.TryGetValue(reference, out var error)) { diagnostics.Add($"{reference}: {error}"); continue; }
            var groupName = Regex.Match(reference, @"^GroupName\s*\(\s*(\{[^}]+\})\s*\)$", RegexOptions.IgnoreCase);
            if (groupName.Success) { layout.GroupNames[reference] = groupName.Groups[1].Value; pending.Enqueue(groupName.Groups[1].Value); continue; }
            AddProjection(layout, reference);
        }
        return layout;
    }
    private static Dictionary<string, CrystalFormula> ReadConditions(IEnumerable<XElement> containers, List<string> diagnostics)
    {
        var conditions = new Dictionary<string, CrystalFormula>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var entries = container.Attributes().Select(a => (Name: a.Name.LocalName, Text: a.Value))
                .Concat(container.Elements().Select(e => (Name: (string?)e.Attribute("ConditionFormulaType") ?? e.Name.LocalName, Text: (string?)e.Attribute("Text") ?? e.Value)));
            foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)))
            {
                if (entry.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !entry.Text.Contains('\n'))
                    diagnostics.Add($"{container.Name}/{entry.Name}: the XML contains only a line comment; reconvert the RPT to retain formula line breaks.");
                var formula = CrystalFormula.Compile(entry.Text);
                if (formula.UsesEvaluationDirectives) diagnostics.Add($"{container.Name}/{entry.Name}: Crystal's complete read/print-pass scheduling is not implemented.");
                conditions[entry.Name] = formula;
            }
        }
        return conditions;
    }
    private static void AddProjection(ReportPositionedLayout layout, string reference, HashSet<string>? seen = null)
    {
        seen ??= new(StringComparer.OrdinalIgnoreCase);
        if (!seen.Add(reference)) return;
        if (seen.Count > 2048) throw new InvalidDataException("Formula dependency limit exceeded.");
        if (layout.Formulas.TryGetValue(reference, out var formula))
        { foreach (var dependency in formula.References) AddProjection(layout, dependency, seen); return; }
        if (layout.Bindings.ContainsKey(reference) || ParseCrystalFieldRef(reference) is not { } field) return;
        var alias = field.Table + "_" + field.Field;
        var suffix = layout.Bindings.Count;
        while (layout.Bindings.Values.Contains(alias, StringComparer.OrdinalIgnoreCase)) alias = "__fxLayout" + suffix++;
        layout.Bindings[reference] = alias;
        layout.Projections.Add($"[{field.Table.Replace("]", "]]", StringComparison.Ordinal)}].[{field.Field.Replace("]", "]]", StringComparison.Ordinal)}] AS [{alias.Replace("]", "]]", StringComparison.Ordinal)}]");
    }
    private static bool Flag(XElement? element, string attribute) => string.Equals((string?)element?.Attribute(attribute), "true", StringComparison.OrdinalIgnoreCase);
}
