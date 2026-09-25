using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public partial class CrystalXmlReportLoader
{
    private ReportDefinition LoadPositionedSource(XElement report, string path, HashSet<string> ancestry, int depth)
    {
        // A main report and an inline subreport may share a name; only a nested definition of the same file and name repeats one.
        var identity = path + "#" + (depth == 0 ? "" : (string?)report.Attribute("Name"));
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
        var functions = CompileCustomFunctions(report, diagnostics);
        foreach (var field in document.Fields.Where(f => f.IsFormula))
        {
            try { layout.Formulas[field.Reference] = CrystalFormula.Compile(field.Expression, field.Syntax, field.Type.Contains("string", StringComparison.OrdinalIgnoreCase) ? "" : 0m, functions); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            { layout.FormulaErrors[field.Reference] = ex.Message; }
        }
        var data = report.Element("DataDefinition");
        // An XML without the converter stamp (an older FlexKit conversion or another exporter) may lack group conditions and name group sorts by placeholders.
        var legacy = report.Attribute(Fx.ControlKit.Reports.NativeCrystal.CrystalReportXmlVersion.Attribute) is null;
        var kinds = GroupConditionKinds(document, data, legacy, diagnostics);
        // A selection formula without an expression (blank or comments only) selects every record, as in Crystal.
        if (data?.Element("RecordSelectionFormula")?.Value is { Length: > 0 } recordSelection)
            layout.RecordSelection = CrystalFormula.Compile(recordSelection, customFunctions: functions) is { IsEmpty: false } records ? records : null;
        if (data?.Element("GroupSelectionFormula")?.Value is { Length: > 0 } groupSelection)
            layout.GroupSelection = CrystalFormula.Compile(groupSelection, customFunctions: functions) is { IsEmpty: false } groups ? groups : null;
        foreach (var summary in data?.Descendants("SummaryFieldDefinition") ?? [])
        {
            var name = (string?)summary.Attribute("FormulaName") ?? "";
            var refs = Regex.Matches(name, @"\{[^}]+\}");
            var group = refs.Count > 1 ? refs[1].Value : "";
            layout.Summaries[name] = new((string?)summary.Attribute("Operation") ?? "Sum", (string?)summary.Attribute("SummarizedField") ?? "", group)
            { AcrossHierarchy = (string?)summary.Attribute("HierarchicalSummaryType") == "AcrossHierarchy", GroupLevel = SummaryGroupLevel(document, kinds, name, group, legacy, diagnostics) };
        }
        ReadGroupRuntime(document, layout, data, functions, diagnostics, kinds, legacy);
        foreach (var running in data?.Descendants("RunningTotalFieldDefinition") ?? [])
        {
            ReportRunningTotalCondition ReadCondition(string prefix)
            {
                var metadata = running.Element("FlexKitRunningTotalConditions");
                var formula = (string?)metadata?.Attribute(prefix + "Formula");
                var type = (string?)running.Attribute(prefix + "ConditionType") ?? "NoCondition";
                return new(type.Equals("OnFormula", StringComparison.OrdinalIgnoreCase) ? "UseFormula" : type,
                    (string?)metadata?.Attribute(prefix + "Field") ?? "", (int?)metadata?.Attribute(prefix + "Group") ?? 0,
                    string.IsNullOrWhiteSpace(formula) ? null : CrystalFormula.Compile(formula, (string?)metadata?.Attribute(prefix + "FormulaSyntax") ?? "Crystal", false, functions));
            }
            layout.RunningTotals[(string?)running.Attribute("FormulaName") ?? ""] = new((string?)running.Attribute("Operation") ?? "Sum", (string?)running.Attribute("SummarizedField") ?? "", "")
            { Evaluation = ReadCondition("Evaluation"), Reset = ReadCondition("Reset") };
        }
        foreach (var section in document.Sections)
        {
            if (!document.SourceSections.TryGetValue(section.SourceKey, out var xml)) continue;
            var area = xml.Parent?.Parent;
            var format = area?.Element("AreaFormat");
            var repeat = area?.Element("GroupAreaFormat") ?? format?.Element("GroupAreaFormat");
            var sections = xml.Parent?.Elements("Section").ToList() ?? [];
            // Crystal's formatter ignores an area's page break before the report header, after the report footer and around page furniture
            // (its saved default is often "on"; a subreport does not break its page for it), and page furniture keeps its place whatever its
            // keep-together and print-at-bottom flags say.
            var body = section.Kind is not ("PageHeader" or "PageFooter");
            if (section.Kind == "Detail" && layout.Columns is null && format?.Element("DetailAreaFormat") is { } multiple && Flag(multiple, "EnableMultipleColumnFormatting"))
                layout.Columns = ReadColumns(multiple);
            layout.Areas[section.Id] = new(Flag(format, "EnableSuppress"), Flag(repeat, "EnableRepeatGroupHeader"),
                section.Kind is not ("ReportHeader" or "PageHeader" or "PageFooter") && ReferenceEquals(sections.FirstOrDefault(), xml) && Flag(format, "EnableNewPageBefore"),
                section.Kind is not ("ReportFooter" or "PageHeader" or "PageFooter") && ReferenceEquals(sections.LastOrDefault(), xml) && Flag(format, "EnableNewPageAfter"), Flag(format, "EnableHideForDrillDown"),
                ReferenceEquals(sections.LastOrDefault(), xml) && Flag(format, "EnableResetPageNumberAfter"), Flag(repeat, "EnableKeepGroupTogether"),
                body && Flag(format, "EnableKeepTogether"), body && Flag(format, "EnablePrintAtBottomOfPage"));
            layout.SectionConditions[section.Id] = ReadConditions(xml.Elements().Where(e => e.Name.LocalName.Contains("ConditionFormulas"))
                .Concat(xml.Elements("SectionFormat").Descendants().Where(e => e.Name.LocalName.Contains("ConditionFormulas"))), diagnostics, functions);
            layout.AreaConditions[section.Id] = ReadConditions((area?.Elements().Where(e => e.Name.LocalName.Contains("ConditionFormulas")) ?? [])
                .Concat(format?.Descendants().Where(e => e.Name.LocalName.Contains("ConditionFormulas")) ?? []), diagnostics, functions);
            foreach (var element in section.Elements)
                if (document.SourceObjects.TryGetValue(element.SourceKey, out var objectXml))
                {
                    // Analysis definitions contain their own cell/series formulas. They are opaque;
                    // only the outer object's formatting and suppression can affect its placeholder.
                    var containers = ReportObjectCapabilities.UnsupportedCrystalKind(element.Kind) is null
                        ? objectXml.Descendants()
                        : objectXml.Elements().Concat(objectXml.Elements().Where(e => e.Name.LocalName is "ObjectFormat" or "Border" or "Font").Descendants());
                    layout.ObjectConditions[element.Id] = ReadConditions(containers.Where(e => e.Name.LocalName.EndsWith("ConditionFormulas")), diagnostics, functions);
                }
        }
        var references = document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "").Append(e.HighlightRule.FieldName))
            .Concat(document.Elements.SelectMany(e => e.Analysis?.References ?? []))
            .Concat(document.Groups.SelectMany(g => new[] { g.Condition, g.ParentIdField, g.InstanceIdField })).Concat(document.Sorts.Select(s => s.Field.Reference))
            .Concat(layout.Summaries.Values.Concat(layout.RunningTotals.Values).Select(s => s.Field))
            .Concat(layout.RunningTotals.Values.SelectMany(t => new[] { t.Evaluation, t.Reset }).SelectMany(c => (c.Formula?.References ?? []).Prepend(c.Field)))
            .Concat(layout.RecordSelection?.References ?? []).Concat(layout.GroupSelection?.References ?? [])
            .Concat(layout.GroupOptions.Values.SelectMany(o => o.SpecifiedGroups).SelectMany(g => g.Selection.References))
            .Concat(layout.GroupOptions.Values.SelectMany(o => o.NameFormula?.References ?? []))
            .Concat(layout.GroupSorts.Values.SelectMany(g => (g.CountFormula?.References ?? []).Prepend(g.Summary.Field)))
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
                foreach (var dependency in formula.References) pending.Enqueue(dependency);
                continue;
            }
            if (layout.FormulaErrors.TryGetValue(reference, out var error)) { diagnostics.Add($"{reference}: {error}"); continue; }
            // Crystal's SDK export writes group-name fields as "GroupName ({x})" or "GroupName ({x}) Name"; the group options name the group.
            var groupName = Regex.Match(reference, @"^GroupName\s*\(\s*(\{[^}]+\})\s*\)(?:\s+Name)?$", RegexOptions.IgnoreCase);
            if (groupName.Success) { layout.GroupNames[reference] = groupName.Groups[1].Value; pending.Enqueue(groupName.Groups[1].Value); continue; }
            AddProjection(layout, reference);
        }
        layout.Diagnostics.AddRange(diagnostics);
        return layout;
    }
    // Group options (date/time/Boolean conditions, specified order) and group sorts by summary (Top/Bottom N), by group level.
    private static void ReadGroupRuntime(ReportDesignerDocument document, ReportPositionedLayout layout, XElement? data, IReadOnlyDictionary<string, CrystalCustomFunction>? functions,
        List<string> diagnostics, int[] kinds, bool legacy)
    {
        for (var level = 0; level < document.Groups.Count; level++)
        {
            var group = document.Groups[level];
            var xml = document.SourceItems.GetValueOrDefault(group.Id);
            var kind = kinds[level];
            if (kind is < 0 or > 11) throw new InvalidDataException($"Group {group.Condition} has an invalid condition {kind}.");
            var specified = xml?.Element("SpecifiedGroups");
            var named = new List<(string Name, CrystalFormula Selection)>();
            foreach (var entry in specified?.Elements("SpecifiedGroup") ?? [])
            {
                var name = (string?)entry.Attribute("Name") ?? "";
                try { named.Add((name, CrystalFormula.Compile(entry.Value, customFunctions: functions))); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                { throw new InvalidDataException($"Group {group.Condition}: specified group '{name}' cannot be compiled: {ex.Message}", ex); }
            }
            var unspecified = specified is null ? "" : (string?)specified.Attribute("UnspecifiedValues") ?? "separateValues";
            if (unspecified is not ("" or "mergeValues" or "discardValues" or "separateValues"))
                throw new InvalidDataException($"Group {group.Condition} has an invalid unspecified-values type '{unspecified}'.");
            var lastDate = string.Equals((string?)xml?.Attribute("ShowLastDateInPeriod"), "true", StringComparison.OrdinalIgnoreCase);
            CrystalFormula? nameFormula = null; var nameError = "";
            if (!string.IsNullOrWhiteSpace(group.NameFormula))
                try { nameFormula = CrystalFormula.Compile(group.NameFormula, group.NameFormulaSyntax, "", functions) is { IsEmpty: false } compiled ? compiled : null; }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                { nameError = ex.Message; diagnostics.Add($"Group {group.Condition}: the group-name formula cannot be compiled: {ex.Message}"); }
            if (kind != 0 || lastDate || specified is not null || nameFormula is not null || nameError.Length > 0)
                layout.GroupOptions[level] = new(kind, lastDate)
                {
                    SpecifiedGroups = named, UnspecifiedValues = unspecified,
                    OthersName = (string?)specified?.Attribute("OthersName") is { Length: > 0 } others ? others : "Others",
                    NameFormula = nameFormula, NameFormulaError = nameError
                };
        }
        foreach (var sort in data?.Element("SortFields")?.Elements("SortField") ?? [])
        {
            var field = ((string?)sort.Attribute("Field") ?? "").Trim();
            if ((string?)sort.Attribute("SortType") != "GroupSortField" || field.StartsWith('{')) continue;
            if (legacy && Regex.IsMatch(field, @"^__FLEXKIT_SUMMARY_REF_\d+__$"))
            {
                diagnostics.Add("A group sort by summary was saved by an older FlexKit converter without naming its summary, so the groups print in their own order. Convert the RPT again to sort them.");
                continue;
            }
            var summary = layout.Summaries.GetValueOrDefault(field) ?? (Regex.Match(field, @"^\s*(\w+)\s*\(\s*(\{[^}]+\})\s*,\s*(\{[^}]+\})") is { Success: true } parsed
                ? new ReportLayoutSummary(parsed.Groups[1].Value, parsed.Groups[2].Value, parsed.Groups[3].Value) { GroupLevel = SummaryGroupLevel(document, kinds, field, parsed.Groups[3].Value, legacy, diagnostics) }
                : throw new NotSupportedException($"Group sort by '{field}' is not a group summary this engine can read."));
            var level = summary.GroupLevel >= 0 ? summary.GroupLevel : document.Groups.FindIndex(g => g.Condition.Equals(summary.Group, StringComparison.OrdinalIgnoreCase));
            if (level < 0) throw new InvalidDataException($"Group sort by '{field}' names no group of this report.");
            var topBottom = (string?)sort.Attribute("TopBottomN") ?? "";
            if (topBottom is not ("" or "TopN" or "BottomN" or "TopNPercentage" or "BottomNPercentage"))
                throw new InvalidDataException($"Group sort by '{field}' has an invalid Top/Bottom N kind '{topBottom}'.");
            var countFormula = (string?)sort.Attribute("TopBottomNFormula");
            layout.GroupSorts[level] = new(summary with { GroupLevel = level }, ((string?)sort.Attribute("SortDirection") ?? "").StartsWith("Descending", StringComparison.OrdinalIgnoreCase))
            {
                TopBottomN = topBottom,
                Count = int.TryParse((string?)sort.Attribute("TopBottomNCount"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : 0,
                Percentage = double.TryParse((string?)sort.Attribute("TopBottomNPercentage"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percentage) ? percentage : 0,
                CountFormula = string.IsNullOrWhiteSpace(countFormula) ? null : CrystalFormula.Compile(countFormula, (string?)sort.Attribute("TopBottomNFormulaSyntax") ?? "Crystal", 0m, functions),
                DiscardOthers = string.Equals((string?)sort.Attribute("DiscardOthers"), "true", StringComparison.OrdinalIgnoreCase),
                WithTies = string.Equals((string?)sort.Attribute("WithTies"), "true", StringComparison.OrdinalIgnoreCase),
                OthersName = (string?)sort.Attribute("OthersName") is { Length: > 0 } others ? others : "Others"
            };
        }
    }

    // A summary named "Op ({field}, {group}, "condition")" totals the group on that field whose condition has that name. An XML that records no
    // condition for it (see GroupConditionKinds) totals the first group on the field, and says so.
    private static int SummaryGroupLevel(ReportDesignerDocument document, int[] kinds, string name, string group, bool legacy, List<string> diagnostics)
    {
        if (group.Length == 0 || Regex.Match(name, "\"([^\"]*)\"\\s*\\)\\s*$") is not { Success: true } condition) return -1;
        for (var level = 0; level < document.Groups.Count; level++)
            if (document.Groups[level].Condition.Equals(group, StringComparison.OrdinalIgnoreCase) && ReportLayoutSession.ConditionNamed(kinds[level], condition.Groups[1].Value,
                CrystalValueType.FromXml(document.Fields.FirstOrDefault(f => f.Reference.Equals(group, StringComparison.OrdinalIgnoreCase))?.Type)?.BaseType)) return level;
        if (!legacy) throw new InvalidDataException($"Summary '{name}': no group on {group} has the condition \"{condition.Groups[1].Value}\".");
        diagnostics.Add($"Summary '{name}': the XML records no group on {group} with the condition \"{condition.Groups[1].Value}\", so it totals the first group on {group}. Convert the RPT again to record the condition.");
        return -1;
    }

    // Group conditions by level. An XML without the converter stamp may not record them; its summaries and formulas still name them
    // ("Sum ({x}, {d}, "monthly")", "GroupName ({d}, "weekly")"), so the only group on such a field takes the one condition they name, as Crystal saved it.
    private static int[] GroupConditionKinds(ReportDesignerDocument document, XElement? data, bool legacy, List<string> diagnostics)
    {
        var kinds = document.Groups.Select(g => document.SourceItems.TryGetValue(g.Id, out var xml) ? (int?)xml.Attribute("ConditionKind") ?? 0 : 0).ToArray();
        if (!legacy) return kinds;
        var named = (data?.Descendants("SummaryFieldDefinition").Select(s => (string?)s.Attribute("FormulaName") ?? "") ?? [])
            .Concat(data?.Descendants("FormulaFieldDefinition").Select(f => f.Value) ?? [])
            .SelectMany(text => Regex.Matches(text, "(?:\\{[^}]+\\}\\s*,\\s*|GroupName\\s*\\(\\s*)(\\{[^}]+\\})\\s*,\\s*\"([^\"]*)\"\\s*\\)", RegexOptions.IgnoreCase))
            .Select(m => (Field: m.Groups[1].Value, Condition: m.Groups[2].Value.Trim())).Where(n => ReportLayoutSession.IsConditionName(n.Condition)).ToArray();
        foreach (var field in named.Select(n => n.Field).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var levels = Enumerable.Range(0, kinds.Length).Where(level => document.Groups[level].Condition.Equals(field, StringComparison.OrdinalIgnoreCase)).ToArray();
            var conditions = named.Where(n => n.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).Select(n => n.Condition).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (levels.Length != 1 || conditions.Length != 1 || kinds[levels[0]] != 0
                || document.SourceItems.TryGetValue(document.Groups[levels[0]].Id, out var xml) && xml.Attribute("ConditionKind") is not null) continue;
            var type = CrystalValueType.FromXml(document.Fields.FirstOrDefault(f => f.Reference.Equals(field, StringComparison.OrdinalIgnoreCase))?.Type)?.BaseType;
            if (ReportLayoutSession.ConditionKindNamed(conditions[0], type) is not ( > 0 and var kind)) continue;
            kinds[levels[0]] = kind;
            diagnostics.Add($"Group {field}: the XML records no group condition, so the one its summaries and formulas name (\"{conditions[0]}\") applies. Convert the RPT again to record it.");
        }
        return kinds;
    }

    // Report custom functions: each report (and each subreport) carries its own list; an entry without text cannot be called. A function that
    // cannot be used is reported, and only formulas calling it fail.
    internal static CrystalCustomFunctionLibrary? CompileCustomFunctions(XElement? report, List<string> diagnostics)
    {
        var sources = (report?.Elements("CustomFunctions").Elements("CustomFunction") ?? [])
            .Select(f => new CrystalCustomFunctionSource(((string?)f.Attribute("Name") ?? "").Trim(), (string?)f.Element("Text") ?? "", (string?)f.Attribute("Syntax") ?? "Crystal"))
            .Where(f => f.Name.Length > 0 && !string.IsNullOrWhiteSpace(f.Text)).ToArray();
        if (sources.Length == 0) return null;
        var library = CrystalCustomFunction.CompileAll(sources);
        foreach (var (name, reason) in library.Errors.Take(20)) diagnostics.Add($"Custom function '{name}' cannot be used: {reason}");
        if (library.Errors.Count > 20) diagnostics.Add($"{library.Errors.Count - 20} more custom functions cannot be used.");
        return library;
    }
    private static Dictionary<string, CrystalFormula> ReadConditions(IEnumerable<XElement> containers, List<string> diagnostics, IReadOnlyDictionary<string, CrystalCustomFunction>? functions)
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
                var formula = CrystalFormula.Compile(entry.Text, customFunctions: functions);
                // Crystal ignores a conditional formula without an expression; the fixed property value applies.
                if (formula.IsEmpty) continue;
                if (formula.UsesPersistentVariables && formula.EvaluationTime is CrystalEvaluationTime.BeforeReadingRecords or CrystalEvaluationTime.WhileReadingRecords)
                    diagnostics.Add($"{container.Name}/{entry.Name}: early-pass variable assignments in inline formatting formulas require a named formula field dependency.");
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
    // A zero detail width prints the details in one column, as Crystal does.
    private static ReportLayoutColumns? ReadColumns(XElement format)
    {
        int Twips(string name) => int.TryParse((string?)format.Attribute(name) ?? "0", System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new InvalidDataException($"The details area's multiple-column {name} is not a twip count.");
        var direction = (string?)format.Attribute("DetailPrintDirection") ?? "DownThenAcross";
        if (!direction.Equals("DownThenAcross", StringComparison.OrdinalIgnoreCase) && !direction.Equals("AcrossThenDown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The details area's printing direction '{direction}' is not AcrossThenDown or DownThenAcross.");
        return Twips("DetailWidth") is > 0 and var width ? new(width, Twips("DetailHeight"), Twips("HorizontalGap"), Twips("VerticalGap"),
            direction.Equals("AcrossThenDown", StringComparison.OrdinalIgnoreCase), Flag(format, "EnableFormatGroupWithMultipleColumn")) : null;
    }
    private static bool Flag(XElement? element, string attribute) => string.Equals((string?)element?.Attribute(attribute), "true", StringComparison.OrdinalIgnoreCase);
}
