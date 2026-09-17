using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public static partial class ReportDesignerXmlSerializer
{
    private static ReportDesignerDocument SnapshotDesign(ReportDesignerDocument document) =>
        JsonSerializer.Deserialize<ReportDesignerDocument>(JsonSerializer.Serialize(document))!;

    private static XDocument SerializePreservingSource(ReportDesignerDocument document)
    {
        if (document.SourceDocument is null || document.OriginalDesign is null)
        {
            var created = BuildSourceDocument(document);
            ApplySelectionMetadata(created.Root!, document);
            if (!string.IsNullOrWhiteSpace(document.CustomSql))
                EnsureChild(EnsureChild(created.Root!, MetadataElementName), "Sql").SetAttributeValue("Query", document.CustomSql);
            return created;
        }

        var before = document.OriginalDesign;
        var copy = new XDocument(document.SourceDocument);
        var root = copy.Root!;
        var nodes = document.SourceDocument.Descendants().Zip(copy.Descendants())
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var sources = document.SourceItems.Concat(document.SourceSections).Concat(document.SourceObjects)
            .ToDictionary(pair => pair.Key, pair => nodes[pair.Value], StringComparer.Ordinal);

        if (document.Title != before.Title)
            EnsureChild(root, "Summaryinfo").SetAttributeValue("ReportTitle", document.Title);
        PatchChild(root, BuildPrintOptions(before.Page), BuildPrintOptions(document.Page));

        if (document.DataSources.Count > 0 || before.DataSources.Count > 0)
        {
            var tables = EnsureChild(EnsureChild(root, "Database"), "Tables");
            MergeItems(tables, before.DataSources, document.DataSources, item => item.Id, BuildTable, sources,
                (node, oldTable, table) => MergeItems(EnsureChild(node, "Fields"), oldTable?.Fields ?? [], table.Fields,
                    field => field.Id, BuildDatabaseField, sources));
        }
        ApplyLinks(root, before, document, sources);

        var data = Child(root, "DataDefinition");
        MergeData("Groups", before.Groups, document.Groups, BuildGroup);
        MergeData("SortFields", before.Sorts, document.Sorts, BuildSort);
        var summaryContainer = Child(data, "SummaryFields") is not null ? "SummaryFields"
            : Child(data, "SummaryFieldDefinitions") is not null ? "SummaryFieldDefinitions" : "SummaryFields";
        MergeData(summaryContainer, before.Summaries, document.Summaries, BuildSummary);
        MergeData("RunningTotalFieldDefinitions", before.RunningTotals, document.RunningTotals, BuildRunningTotal);
        MergeData("FormulaFieldDefinitions", before.Fields.Where(field => field.IsFormula).ToList(),
            document.Fields.Where(field => field.IsFormula).ToList(), BuildFormula);
        MergeData("ParameterFieldDefinitions", before.Parameters, document.Parameters, BuildParameter);

        if (before.RecordSelectionFormula != document.RecordSelectionFormula
            || !before.Filters.Select(filter => (filter.Field.Reference, filter.Field.Type, filter.Operator, filter.Value))
                .SequenceEqual(document.Filters.Select(filter => (filter.Field.Reference, filter.Field.Type, filter.Operator, filter.Value))))
        {
            EnsureChild(EnsureChild(root, "DataDefinition"), "RecordSelectionFormula").Value = BuildSelectionFormula(document);
            Child(Child(root, "DataDefinition"), "RecordSelection")?.Remove();
            ApplySelectionMetadata(root, document);
        }
        if (document.CustomSql != before.CustomSql)
            EnsureChild(EnsureChild(root, MetadataElementName), "Sql").SetAttributeValue("Query", document.CustomSql);

        ApplyLayout(root, before, document, sources);
        foreach (var summary in document.Summaries)
        {
            var original = before.Summaries.FirstOrDefault(item => item.Id == summary.Id);
            if (original is null || SummaryBinding(original) == SummaryBinding(summary)) continue;
            var sourceBinding = document.SourceItems.TryGetValue(summary.Id, out var sourceSummary)
                ? (string?)sourceSummary.Attribute("FormulaName") ?? SummaryBinding(original) : SummaryBinding(original);
            foreach (var node in (root.Element("ReportDefinition")?.Descendants("FieldObject") ?? [])
                .Where(node => (string?)node.Attribute("DataSource") == sourceBinding))
                node.SetAttributeValue("DataSource", SummaryBinding(summary));
        }
        return copy;

        void MergeData<T>(string name, IReadOnlyList<T> oldItems, IReadOnlyList<T> items, Func<T, XElement> build)
            where T : ReportDesignerSourceItem
        {
            if (oldItems.Count == 0 && items.Count == 0)
                return;
            MergeItems(EnsureChild(EnsureChild(root, "DataDefinition"), name), oldItems, items,
                item => item.Id, build, sources);
        }
    }

    private static void MergeItems<T>(XElement parent, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string> key, Func<T, XElement> build, IReadOnlyDictionary<string, XElement> sources,
        Action<XElement, T?, T>? mergeChildren = null) where T : class
    {
        var originals = before.ToDictionary(key, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var desired = new List<XElement>();
        foreach (var item in after)
        {
            var id = key(item);
            if (!ids.Add(id))
                throw new InvalidDataException("Duplicate designer item identity: " + id);
            originals.TryGetValue(id, out var original);
            var node = original is not null && sources.TryGetValue(id, out var source) ? source : build(item);
            if (original is not null)
                PatchShape(node, build(original), build(item));
            mergeChildren?.Invoke(node, original, item);
            desired.Add(node);
        }

        var managed = before.Select(key).Where(sources.ContainsKey).Select(id => sources[id])
            .Where(node => node.Parent == parent).ToList();
        ReplaceManaged(parent, managed, desired);
    }

    // Only modelled changes are applied. Absent/unknown source attributes and children remain untouched.
    private static void PatchShape(XElement target, XElement before, XElement after)
    {
        foreach (var name in before.Attributes().Select(a => a.Name).Union(after.Attributes().Select(a => a.Name)))
        {
            if ((string?)before.Attribute(name) != (string?)after.Attribute(name))
                target.SetAttributeValue(name, (string?)after.Attribute(name));
        }
        foreach (var name in before.Elements().Select(e => e.Name).Union(after.Elements().Select(e => e.Name)))
        {
            var oldChild = before.Element(name);
            var child = after.Element(name);
            if (XNode.DeepEquals(oldChild, child))
                continue;
            // Ordered text runs are an atomic authoring payload, not named XML properties.
            if (name.LocalName == "FlexKitVisual")
            {
                target.Element(name)?.Remove();
                if (child is not null) target.Add(new XElement(child));
                continue;
            }
            if (child is null)
                target.Element(name)?.Remove();
            else if (oldChild is null || target.Element(name) is null)
                target.Add(new XElement(child));
            else
                PatchShape(target.Element(name)!, oldChild, child);
        }
        if (!before.HasElements && !after.HasElements && before.Value != after.Value)
            target.Value = after.Value;
    }

    private static void PatchChild(XElement parent, XElement before, XElement after)
    {
        if (XNode.DeepEquals(before, after))
            return;
        var target = parent.Element(after.Name);
        if (target is null)
            parent.Add(after);
        else
            PatchShape(target, before, after);
    }

    private static void ReplaceManaged(XElement parent, IReadOnlyList<XElement> managed, IReadOnlyList<XElement> desired)
    {
        if (managed.SequenceEqual(desired))
            return;
        var slots = managed.Select(_ => new XComment("designer-slot")).ToList();
        for (var index = 0; index < managed.Count; index++)
            managed[index].ReplaceWith(slots[index]);
        foreach (var node in desired.Where(node => node.Parent is not null))
            node.Remove();
        for (var index = 0; index < slots.Count; index++)
        {
            if (index < desired.Count)
                slots[index].ReplaceWith(desired[index]);
            else
                slots[index].Remove();
        }
        foreach (var node in desired.Skip(slots.Count))
            parent.Add(node);
    }

    private static void ApplyLayout(XElement root, ReportDesignerDocument before, ReportDesignerDocument after,
        IReadOnlyDictionary<string, XElement> sources)
    {
        var areas = Child(Child(root, "ReportDefinition"), "Areas");
        if (areas is null && after.Sections.Count == 0)
            return;
        areas ??= EnsureChild(EnsureChild(root, "ReportDefinition"), "Areas");
        var originalAreas = before.Sections.Where(section => sources.ContainsKey(section.Id))
            .Select(section => sources[section.Id].Parent!.Parent!).Distinct().ToList();
        var desiredAreas = new List<XElement>();
        var desiredSections = new Dictionary<XElement, List<XElement>>();
        foreach (var section in after.Sections)
        {
            var original = before.Sections.FirstOrDefault(candidate => candidate.Id == section.Id);
            var node = original is not null && sources.TryGetValue(section.Id, out var existing)
                ? existing : SectionWithPrototype(section);
            var area = node.Parent?.Parent;
            if (area is null || (original is not null && (original.AreaName != section.AreaName || original.Kind != section.Kind)))
                area = areas.Elements("Area").Concat(desiredAreas).FirstOrDefault(candidate =>
                    Attribute(candidate, "Name") == section.AreaName && Attribute(candidate, "Kind") == section.Kind)
                    ?? CreateAreaElement(section);
            if (!desiredSections.TryGetValue(area, out var sections))
            {
                desiredSections[area] = sections = [];
                desiredAreas.Add(area);
            }
            sections.Add(node);
            if (original is not null)
                PatchShape(node, CreateSectionElement(original), CreateSectionElement(section));

            var objects = EnsureChild(node, "ReportObjects");
            MergeItems(objects, original?.Elements ?? [], section.Elements, item => item.Id, BuildWithPrototype, sources);
        }
        foreach (var area in desiredAreas)
        {
            var parent = EnsureChild(area, "Sections");
            var managed = before.Sections.Where(section => sources.ContainsKey(section.Id)).Select(section => sources[section.Id])
                .Where(section => section.Parent == parent).ToList();
            ReplaceManaged(parent, managed, desiredSections[area]);
        }
        ReplaceManaged(areas, originalAreas, desiredAreas);

        XElement SectionWithPrototype(ReportDesignerSection section)
        {
            var original = before.Sections.FirstOrDefault(item => item.SourceKey == section.SourceKey && section.SourceKey.Length > 0);
            if (original is null || !after.SourceSections.TryGetValue(section.SourceKey, out var prototype)) return CreateSectionElement(section);
            var copy = new XElement(prototype);
            PatchShape(copy, CreateSectionElement(original), CreateSectionElement(section));
            copy.Element("ReportObjects")?.Remove(); copy.Add(new XElement("ReportObjects"));
            return copy;
        }

        XElement BuildWithPrototype(ReportDesignerElement element)
        {
            var original = before.Elements.FirstOrDefault(item => item.SourceKey == element.SourceKey);
            if (original is not null && after.SourceObjects.TryGetValue(element.SourceKey, out var prototype))
            {
                var copy = new XElement(prototype);
                PatchShape(copy, BuildObject(original), BuildObject(element));
                return copy;
            }
            return BuildObject(element);
        }
    }

    private static void ApplyLinks(XElement root, ReportDesignerDocument before, ReportDesignerDocument after,
        IReadOnlyDictionary<string, XElement> sources)
    {
        if (XNode.DeepEquals(BuildTableLinksElement(before), BuildTableLinksElement(after)))
            return;
        var parent = EnsureChild(EnsureChild(root, "Database"), "TableLinks");
        var managed = before.Links.Where(link => sources.ContainsKey(link.Id)).Select(link => sources[link.Id]).Distinct().ToList();
        var desired = new List<XElement>();
        foreach (var links in after.Links.GroupBy(link => (
            Source: after.SourceItems.GetValueOrDefault(link.Id), link.LeftTable, link.RightTable, link.JoinType)))
        {
            // OriginalDesign is a value snapshot; source XML is owned by the live document.
            var originalXml = links.Select(link => after.SourceItems.GetValueOrDefault(link.Id)).FirstOrDefault(node => node is not null);
            var node = originalXml is null ? new XElement("TableLink") : new XElement(originalXml);
            node.SetAttributeValue("JoinType", ToCrystalJoinType(links.Key.JoinType));
            var sourceFields = new List<XElement>();
            var destinationFields = new List<XElement>();
            foreach (var link in links)
            {
                var oldLink = before.Links.FirstOrDefault(candidate => candidate.Id == link.Id);
                var origin = after.SourceItems.GetValueOrDefault(link.Id);
                var index = origin is null ? -1 : before.Links.Where(candidate => after.SourceItems.GetValueOrDefault(candidate.Id) == origin)
                    .ToList().FindIndex(candidate => candidate.Id == link.Id);
                sourceFields.Add(LinkField("SourceFields", true));
                destinationFields.Add(LinkField("DestinationFields", false));

                XElement LinkField(string container, bool left)
                {
                    var field = index >= 0 ? origin?.Element(container)?.Elements("Field").ElementAtOrDefault(index) : null;
                    var current = BuildLinkField(left ? link.LeftTable : link.RightTable, left ? link.LeftField : link.RightField);
                    if (field is null || oldLink is null)
                        return current;
                    var preserved = new XElement(field);
                    PatchShape(preserved, BuildLinkField(left ? oldLink.LeftTable : oldLink.RightTable, left ? oldLink.LeftField : oldLink.RightField), current);
                    return preserved;
                }
            }
            var source = EnsureChild(node, "SourceFields");
            ReplaceManaged(source, source.Elements("Field").ToList(), sourceFields);
            var destination = EnsureChild(node, "DestinationFields");
            ReplaceManaged(destination, destination.Elements("Field").ToList(), destinationFields);
            desired.Add(node);
        }
        ReplaceManaged(parent, managed, desired);
        Child(root, MetadataElementName)?.Element("Links")?.Remove();
    }

    private static XElement BuildPrintOptions(ReportDesignerPage page)
    {
        var root = new XElement("Report");
        ApplyPrintOptions(root, page);
        return root.Element("PrintOptions")!;
    }

    private static XElement BuildObject(ReportDesignerElement element)
    {
        var node = CreateObjectElement(element);
        ApplyElement(element, node);
        return node;
    }

    private static XElement BuildTable(ReportDesignerDataTable table)
    {
        var name = string.IsNullOrWhiteSpace(table.SourceName) ? table.Name : table.SourceName;
        var node = new XElement("Table", new XAttribute("Name", name), new XAttribute("Alias", table.Name),
            new XAttribute("QualifiedName", string.IsNullOrWhiteSpace(table.Schema) ? name : $"{table.Schema}.{name}"));
        if (!string.IsNullOrWhiteSpace(table.CommandText))
            node.Add(new XElement("Command", table.CommandText));
        return node;
    }

    private static XElement BuildDatabaseField(ReportDesignerField field) => new("Field",
        new XAttribute("Name", field.Name), new XAttribute("ShortName", field.Name),
        new XAttribute("LongName", string.IsNullOrWhiteSpace(field.LongName) ? field.DisplayName : field.LongName),
        new XAttribute("FormulaForm", field.Reference), new XAttribute("Type", field.Type));

    private static XElement BuildFormula(ReportDesignerField field) => new("FormulaFieldDefinition",
        new XAttribute("Name", field.Name), new XAttribute("FormulaName", field.Reference),
        new XAttribute("ValueType", field.Type), new XAttribute("Kind", "FormulaField"),
        new XAttribute("Syntax", field.Syntax), field.Expression);

    private static XElement BuildGroup(ReportDesignerGroup group) => new("Group",
        new XAttribute("Name", group.Name), new XAttribute("ConditionField", group.Condition),
        new XAttribute("SortDirection", group.SortDirection));

    private static XElement BuildSort(ReportDesignerSort sort) => new("SortField",
        new XAttribute("Field", sort.Field.Reference), new XAttribute("SortDirection", sort.Direction.Replace("Order", "", StringComparison.Ordinal) + "Order"),
        new XAttribute("SortType", sort.SortType));

    internal static string SummaryBinding(ReportDesignerSummary summary) =>
        $"{summary.Operation} ({summary.Field.Reference}{(string.IsNullOrWhiteSpace(summary.GroupName) ? "" : ", " + summary.GroupName)})";

    private static XElement BuildSummary(ReportDesignerSummary summary) => new("SummaryFieldDefinition",
        new XAttribute("Name", summary.Field.Name), new XAttribute("Kind", "SummaryField"),
        new XAttribute("FormulaName", SummaryBinding(summary)), new XAttribute("SummarizedField", summary.Field.Reference),
        new XAttribute("Operation", summary.Operation));

    private static XElement BuildParameter(ReportDesignerParameter parameter) => new("ParameterFieldDefinition",
        new XAttribute("Name", parameter.Name), new XAttribute("ParameterFieldName", parameter.Name),
        new XAttribute("FormulaName", $"{{?{parameter.Name}}}"), new XAttribute("PromptText", parameter.Prompt),
        new XAttribute("ParameterType", "ReportParameter"), new XAttribute("Kind", "ParameterField"),
        new XAttribute("ParameterFieldUsage", "InUse"), new XAttribute("ParameterValueKind", ParameterKind(parameter.Type)),
        new XAttribute("IsOptionalPrompt", Lower(!parameter.Required)),
        new XAttribute("EnableAllowMultipleValue", Lower(parameter.AllowMultiple)));

    private static string ParameterKind(string type)
    {
        if (type.EndsWith("Parameter", StringComparison.Ordinal)) return type;
        var lower = type.ToLowerInvariant();
        if (lower.Contains("boolean")) return "BooleanParameter";
        if (lower.Contains("datetime")) return "DateTimeParameter";
        if (lower.Contains("date")) return "DateParameter";
        if (lower.Contains("time")) return "TimeParameter";
        if (lower.Contains("number") || lower.Contains("decimal") || lower.Contains("long") || lower.Contains("int") || lower.Contains("currency")) return "NumberParameter";
        return "StringParameter";
    }

    internal static string BuildSelectionFormula(ReportDesignerDocument document)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(document.RecordSelectionFormula))
            parts.Add(document.RecordSelectionFormula);
        parts.AddRange(document.Filters.Select(filter =>
        {
            var operation = filter.Operator switch
            {
                "is not equal to" => "<>", "is less than" => "<", "is less than or equal to" => "<=",
                "is greater than" => ">", "is greater than or equal to" => ">=",
                "contains" or "does not contain" or "begins with" or "ends with" => "like", "is equal to" => "=",
                _ => throw new NotSupportedException($"Unsupported selection operator: {filter.Operator}")
            };
            var value = filter.Value;
            var parameter = value.StartsWith("{?", StringComparison.Ordinal) && value.EndsWith('}');
            if (!parameter && operation == "like")
                value = (filter.Operator is "contains" or "does not contain" or "ends with" ? "*" : "") + value
                    + (filter.Operator is "contains" or "does not contain" or "begins with" ? "*" : "");
            var literal = parameter ? value : CrystalLiteral(operation == "like" ? value : ReportDesignerSqlBuilder.TypedFilterValue(value, filter.Field.Type));
            if (parameter && operation == "like")
                literal = (filter.Operator is "contains" or "does not contain" or "ends with" ? "\"*\" + " : "") + literal
                    + (filter.Operator is "contains" or "does not contain" or "begins with" ? " + \"*\"" : "");
            var expression = $"{filter.Field.Reference} {operation} {literal}";
            return filter.Operator == "does not contain" ? $"not ({expression})" : expression;
        }));
        return parts.Count switch { 0 => "", 1 => parts[0], _ => string.Join(" and ", parts.Select(part => $"({part})")) };
    }

    private static string CrystalLiteral(object value) => value switch
    {
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        DateTime date => $"DateTime({date.ToString("yyyy, M, d, H, m, s", CultureInfo.InvariantCulture)})",
        TimeSpan time => $"Time({time.Hours}, {time.Minutes}, {time.Seconds})",
        _ => "\"" + value.ToString()!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
    };

    private static void ApplySelectionMetadata(XElement root, ReportDesignerDocument document)
    {
        var metadata = Child(root, MetadataElementName);
        metadata?.Element("Selection")?.Remove();
        if (document.Filters.Count == 0)
            return;
        metadata ??= EnsureChild(root, MetadataElementName);
        metadata.Add(new XElement("Selection", new XAttribute("BaseFormula", document.RecordSelectionFormula),
            document.Filters.Select(filter => new XElement("Filter", new XAttribute("Field", filter.Field.Reference),
                new XAttribute("FieldType", filter.Field.Type), new XAttribute("Operator", filter.Operator), new XAttribute("Value", filter.Value)))));
    }
}
