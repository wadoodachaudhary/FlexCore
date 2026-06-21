using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

public class CrystalXmlReportLoader
{
    private readonly ILogger<CrystalXmlReportLoader> _logger;
    private readonly ReportOptions _options;

    private const int TwipsPerPixel = 15;
    private const string SubreportColumnPrefix = "__Subreport_";

    private static int TwipsToPx(int twips) => twips > 0 ? twips / TwipsPerPixel : 0;

    public CrystalXmlReportLoader(ILogger<CrystalXmlReportLoader> logger, ReportOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ReportOptions();
    }

    public ReportDefinition LoadDrillDown(string xmlFilePath, IReadOnlyList<DrillDownFilter> path)
    {
        return LoadInternal(xmlFilePath, drillPath: path);
    }

    public ReportDefinition LoadWithFieldFilters(string xmlFilePath, IReadOnlyList<ReportFieldFilter> filters)
    {
        return LoadInternal(xmlFilePath, drillPath: null, fieldFilters: filters);
    }

    public ReportDefinition LoadInlineSubreport(
        string xmlFilePath,
        string subreportName,
        IReadOnlyList<ReportFieldFilter>? filters = null)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Crystal Reports XML not found: {xmlFilePath}", xmlFilePath);

        var doc = XDocument.Load(xmlFilePath);
        var report = doc.Root ?? throw new InvalidDataException("Invalid XML: missing root element.");
        var subreport = report.Elements("SubReports")
            .Elements("Report")
            .FirstOrDefault(r => string.Equals(
                ((string?)r.Attribute("Name") ?? "").Trim(),
                subreportName,
                StringComparison.OrdinalIgnoreCase));

        if (subreport == null)
            throw new InvalidDataException($"Inline Crystal subreport not found: {subreportName}");

        return LoadInternalFromReport(
            new XElement(subreport),
            xmlFilePath,
            drillPath: null,
            fieldFilters: filters,
            reportIdOverride: subreportName);
    }

    public ReportDefinition Load(string xmlFilePath)
    {
        return LoadInternal(xmlFilePath, drillPath: null);
    }

    private ReportDefinition LoadInternal(
        string xmlFilePath,
        IReadOnlyList<DrillDownFilter>? drillPath,
        IReadOnlyList<ReportFieldFilter>? fieldFilters = null)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Crystal Reports XML not found: {xmlFilePath}", xmlFilePath);

        var doc = XDocument.Load(xmlFilePath);
        var report = doc.Root ?? throw new InvalidDataException("Invalid XML: missing root element.");
        return LoadInternalFromReport(new XElement(report), xmlFilePath, drillPath, fieldFilters);
    }

    private ReportDefinition LoadInternalFromReport(
        XElement report,
        string xmlFilePath,
        IReadOnlyList<DrillDownFilter>? drillPath,
        IReadOnlyList<ReportFieldFilter>? fieldFilters = null,
        string? reportIdOverride = null)
    {
        var subreportObjects = ExtractSubreportObjects(report, xmlFilePath);
        var flexKitCustomSql = ExtractFlexKitCustomSql(report);

        foreach (var sr in report.Descendants("SubReports").ToList())
            sr.Remove();

        var definition = new ReportDefinition
        {
            ReportId = string.IsNullOrWhiteSpace(reportIdOverride)
                ? Path.GetFileNameWithoutExtension(xmlFilePath)
                : reportIdOverride,
            Title = string.IsNullOrWhiteSpace(reportIdOverride)
                ? Path.GetFileNameWithoutExtension(xmlFilePath).Replace("_", " ")
                : reportIdOverride,
            SourceRptFile = string.IsNullOrWhiteSpace(reportIdOverride)
                ? xmlFilePath
                : $"{xmlFilePath}#{reportIdOverride}",
            SubreportLinks = subreportObjects.Select(ToReportSubreportLink).ToList()
        };

        ExtractReportHeadings(report, definition);

        var printOptions = report.Element("PrintOptions");
        if (printOptions != null)
        {
            var orientation = (string?)printOptions.Attribute("PaperOrientation");
            definition.Orientation = string.Equals(orientation, "Landscape", StringComparison.OrdinalIgnoreCase)
                ? ReportOrientation.Landscape
                : ReportOrientation.Portrait;

            var paperSize = (string?)printOptions.Attribute("PaperSize");
            definition.PaperSize = paperSize switch
            {
                "PaperLegal" => ReportPaperSize.Legal,
                "PaperA4" => ReportPaperSize.A4,
                "PaperTabloid" => ReportPaperSize.Tabloid,
                _ => ReportPaperSize.Letter
            };

            var pageContentHeightTwips = ParseInt(printOptions.Attribute("PageContentHeight"));
            if (pageContentHeightTwips > 0)
            {
                int pageHeightPx = TwipsToPx(pageContentHeightTwips);
                const int kPageChromePx = 80;
                const int kRowHeightPx = 14;

                int visibleRows = Math.Max(10, (pageHeightPx - kPageChromePx) / kRowHeightPx);
                definition.RowsPerPage = visibleRows;
            }
        }

        var tables = ParseTables(report);
        var tableLinks = ParseTableLinks(report);
        var parameters = ParseParameters(report);
        var detailFields = ParseDetailFields(report);
        var headerLabels = ParseHeaderLabels(report);
        var groups = ParseGroups(report);
        var sortFields = ParseSortFields(report);
        var summaryFields = ParseSummaryFields(report);
        var formulaFields = ParseFormulaFields(report);
        var whereSql = ConvertRecordSelectionFormula(report, formulaFields);

        var isDrillTab = drillPath != null && drillPath.Count > 0;
        var detailSuppressedAtTopLevel = IsDetailSuppressedAtTopLevel(report) && !isDrillTab;

        List<ReportColumn> columns;
        List<string> selectFields;
        var isDrillDownReportStyle = IsDetailSuppressedAtTopLevel(report);
        var mergeSummaryWithDetail = isDrillDownReportStyle && isDrillTab;

        if (detailSuppressedAtTopLevel)
        {
            var visibleSection = FindInnermostVisibleDataSection(report);
            var summaryFieldsFromHeader = visibleSection != null
                ? ExtractSectionFields(visibleSection, formulaFields, detectFormulas: true)
                : detailFields;
            columns = BuildColumnsFromSection(summaryFieldsFromHeader, headerLabels, formulaFields, subreportObjects);
            selectFields = BuildSelectFieldsForSummary(columns, groups, formulaFields, detailFields);
        }
        else if (mergeSummaryWithDetail)
        {
            columns = BuildColumnsFromSection(detailFields, headerLabels, formulaFields, subreportObjects);
            selectFields = BuildSelectFieldsForDrillDown(columns, groups, formulaFields);
        }
        else
        {
            columns = BuildColumns(detailFields, headerLabels, tables, formulaFields, subreportObjects);
            selectFields = BuildSelectFields(columns, groups, formulaFields);
        }
        AppendSubreportLinkSelects(selectFields, subreportObjects);
        AppendConditionalStyleSelects(selectFields, columns);

        var firstRecordSort = sortFields
            .FirstOrDefault(s => string.Equals(s.SortType, "RecordSortField", StringComparison.OrdinalIgnoreCase));
        if (firstRecordSort != null)
        {
            var alias = $"{firstRecordSort.Table}_{firstRecordSort.Field}";
            foreach (var col in columns)
            {
                if (string.Equals(col.Field, alias, StringComparison.OrdinalIgnoreCase))
                {
                    col.SuppressRepeats = true;
                    break;
                }
            }
        }

        if (isDrillTab)
        {
            var filterSql = BuildDrillDownWhere(drillPath!, groups, formulaFields);
            if (!string.IsNullOrWhiteSpace(filterSql))
                whereSql = string.IsNullOrWhiteSpace(whereSql)
                    ? filterSql
                    : $"({whereSql}) AND ({filterSql})";
        }
        if (fieldFilters != null && fieldFilters.Count > 0)
        {
            var filterSql = BuildFieldFilterWhere(fieldFilters);
            if (!string.IsNullOrWhiteSpace(filterSql))
                whereSql = string.IsNullOrWhiteSpace(whereSql)
                    ? filterSql
                    : $"({whereSql}) AND ({filterSql})";
        }

        var useDistinct = detailSuppressedAtTopLevel || mergeSummaryWithDetail;
        List<GroupInfo> effectiveGroups;
        if (detailSuppressedAtTopLevel)
            effectiveGroups = groups.Where(g => g.IsVisible).ToList();
        else
            effectiveGroups = groups;
        var orderByClause = BuildOrderByClause(
            useDistinct ? sortFields.Where(s => s.SortType != "RecordSortField").ToList() : sortFields,
            effectiveGroups, formulaFields);
        var sql = BuildSql(tables, tableLinks, whereSql, orderByClause, selectFields,
                            useDistinct: useDistinct);
        if (ShouldUseFlexKitCustomSql(flexKitCustomSql, drillPath, fieldFilters))
            sql = flexKitCustomSql;

        definition.Sql = sql;
        definition.Columns = columns;
        definition.Parameters = parameters;

        if (detailSuppressedAtTopLevel)
        {
            var allGroupSelects = new List<string>();
            var seenTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                if (g.FormulaName != null)
                {
                    if (formulaFields.TryGetValue(g.FormulaName, out var sqlExpr))
                    {
                        var alias = FormulaGroupAlias(g.FormulaName);
                        if (seenTree.Add(alias))
                            allGroupSelects.Add($"({sqlExpr}) AS [{alias}]");
                    }
                }
                else
                {
                    var alias = $"{g.Table}_{g.Field}";
                    if (seenTree.Add(alias))
                        allGroupSelects.Add(alias);
                }
                AppendDescriptionSelect(g, seenTree, allGroupSelects);
            }
            if (allGroupSelects.Count > 0)
            {
                var treeOrderBy = BuildOrderByClause(new List<SortFieldInfo>(), groups, formulaFields);
                definition.TreeSql = BuildSql(tables, tableLinks, whereSql, treeOrderBy,
                    allGroupSelects, useDistinct: true);
            }
        }
        List<GroupInfo> exposedGroups;
        if (detailSuppressedAtTopLevel)
        {
            exposedGroups = groups;
        }
        else if (isDrillTab)
        {
            exposedGroups = groups
                .Where(g => !drillPath!.Any(dp =>
                    string.Equals(dp.Field, $"{g.Table}_{g.Field}", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        else
        {
            exposedGroups = groups;
        }
        definition.Groups = BuildReportGroups(exposedGroups, summaryFields);

        _logger.LogInformation(
            "Parsed Crystal XML report: {File} ({Tables} tables, {Cols} cols, {Groups} groups, {Params} params)",
            Path.GetFileName(xmlFilePath), tables.Count, columns.Count, groups.Count, parameters.Count);

        return definition;
    }

    private static string ExtractFlexKitCustomSql(XElement report)
    {
        return ((string?)report.Element("FlexKitReportDesigner")?.Element("Sql")?.Attribute("Query") ?? "").Trim();
    }

    private static bool ShouldUseFlexKitCustomSql(
        string customSql,
        IReadOnlyList<DrillDownFilter>? drillPath,
        IReadOnlyList<ReportFieldFilter>? fieldFilters)
    {
        return !string.IsNullOrWhiteSpace(customSql)
            && (drillPath == null || drillPath.Count == 0)
            && (fieldFilters == null || fieldFilters.Count == 0);
    }

    private sealed record TableInfo(string Alias, string Name, string? CommandSql = null);
    private sealed record TableLinkInfo(string JoinType, List<(string Table, string Field)> Source, List<(string Table, string Field)> Destination);
    private sealed record DetailFieldInfo(
        string Name, string Table, string Field, int Left, int Width, string Alignment,
        string DataSource, bool IsFormula, string TextColor = "", bool Bold = false,
        ConditionalStyleInfo? ConditionalStyle = null,
        string AreaKind = "", string SectionKind = "", string SectionName = "");
    private sealed record PageHeaderLabel(string Text, int Left, int Width, string? FieldObjectName);
    private sealed record ConditionalStyleInfo(
        string FieldAlias, string Operator, string Value,
        string TrueTextColor = "", string FalseTextColor = "",
        bool? TrueBold = null, bool? FalseBold = null);
    private sealed record GroupInfo(
        string Table, string Field, string? FormulaName,
        bool IsVisible = true, bool PageBreakBefore = false,
        string DescriptionTable = "", string DescriptionField = "");
    private sealed record SortFieldInfo(string Table, string Field, string? FormulaName, string Direction, string SortType);
    private sealed record SummaryFieldInfo(string Operation, string Field, string GroupField);
    private sealed record SubreportParameterLinkInfo(
        string LinkedParameterName,
        string MainReportFieldName,
        string MainReportAlias,
        string SubreportFieldName,
        string SubreportTable,
        string SubreportField);
    private sealed record SubreportObjectInfo(
        string Name, string SubreportName,
        string AreaKind, string SectionKind, string SectionName,
        int Top, int Left, int Width, int Height,
        bool EnableOnDemand, bool IsInline, string XmlPath, string Url,
        IReadOnlyList<SubreportParameterLinkInfo> ParameterLinks);

    private IReadOnlyList<SubreportObjectInfo> ExtractSubreportObjects(XElement report, string xmlFilePath)
    {
        var linkedParameters = ExtractSubreportParameterLinks(report);
        var referenceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in report.Elements("SubreportReferences").Elements("SubreportReference"))
        {
            var name = ((string?)reference.Attribute("Name") ?? "").Trim();
            var fileName = (string?)reference.Attribute("FileName") ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fileName))
                continue;

            referenceMap.TryAdd(name, fileName);
        }
        var inlineSubreportNames = report.Elements("SubReports")
            .Elements("Report")
            .Select(r => ((string?)r.Attribute("Name") ?? "").Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<SubreportObjectInfo>();
        foreach (var obj in report.Descendants("SubreportObject")
            .Where(o => !o.Ancestors("SubReports").Any()))
        {
            if (AttrIsTrue(obj.Element("ObjectFormat"), "EnableSuppress"))
                continue;

            var subreportName = ((string?)obj.Attribute("SubreportName") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(subreportName))
                continue;

            var xmlHint = (string?)obj.Attribute("SubreportXmlPath") ?? "";
            if (string.IsNullOrWhiteSpace(xmlHint) &&
                referenceMap.TryGetValue(subreportName, out var referencedXml))
            {
                xmlHint = referencedXml;
            }

            var xmlPath = ResolveSubreportXmlPath(xmlFilePath, xmlHint, subreportName);
            var isInline = false;
            if (inlineSubreportNames.Contains(subreportName) &&
                (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)))
            {
                xmlPath = Path.GetFullPath(xmlFilePath);
                isInline = true;
            }
            var url = isInline ? "#" : ResolvePublicWebUrl(xmlPath);
            var section = obj.Ancestors("Section").FirstOrDefault();
            var area = obj.Ancestors("Area").FirstOrDefault();
            list.Add(new SubreportObjectInfo(
                Name: (string?)obj.Attribute("Name") ?? "",
                SubreportName: subreportName,
                AreaKind: (string?)area?.Attribute("Kind") ?? "",
                SectionKind: (string?)section?.Attribute("Kind") ?? "",
                SectionName: (string?)section?.Attribute("Name") ?? "",
                Top: ParseInt(obj.Attribute("Top")),
                Left: ParseInt(obj.Attribute("Left")),
                Width: ParseInt(obj.Attribute("Width")),
                Height: ParseInt(obj.Attribute("Height")),
                EnableOnDemand: AttributeIsTrue(obj.Attribute("EnableOnDemand")),
                IsInline: isInline,
                XmlPath: xmlPath,
                Url: url,
                ParameterLinks: linkedParameters.TryGetValue(subreportName, out var links)
                    ? links
                    : Array.Empty<SubreportParameterLinkInfo>()));
        }

        return list;
    }

    private static Dictionary<string, List<SubreportParameterLinkInfo>> ExtractSubreportParameterLinks(XElement report)
    {
        var map = new Dictionary<string, List<SubreportParameterLinkInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subreport in report.Elements("SubReports").Elements("Report"))
        {
            var subreportName = ((string?)subreport.Attribute("Name") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(subreportName))
                continue;

            foreach (var link in subreport.Elements("SubReportLinks").Elements("SubReportLink"))
            {
                var mainFieldName = ((string?)link.Attribute("MainReportFieldName") ?? "").Trim();
                var subreportFieldName = ((string?)link.Attribute("SubreportFieldName") ?? "").Trim();
                var mainField = ParseCrystalFieldRef(mainFieldName);
                var subreportField = ParseCrystalFieldRef(subreportFieldName);
                if (!mainField.HasValue || !subreportField.HasValue)
                    continue;

                if (!map.TryGetValue(subreportName, out var links))
                {
                    links = new List<SubreportParameterLinkInfo>();
                    map[subreportName] = links;
                }

                var resolvedSubreportField = ResolveSubreportLinkField(subreport, subreportField.Value);

                links.Add(new SubreportParameterLinkInfo(
                    LinkedParameterName: ((string?)link.Attribute("LinkedParameterName") ?? "").Trim(),
                    MainReportFieldName: mainFieldName,
                    MainReportAlias: $"{mainField.Value.Table}_{mainField.Value.Field}",
                    SubreportFieldName: subreportFieldName,
                    SubreportTable: resolvedSubreportField.Table,
                    SubreportField: resolvedSubreportField.Field));
            }
        }

        return map;
    }

    private static (string Table, string Field) ResolveSubreportLinkField(
        XElement subreport,
        (string Table, string Field) parsedField)
    {
        var tableFields = ExtractTableFieldIndex(subreport);
        if (tableFields.TryGetValue(parsedField.Table, out var exactFields) &&
            exactFields.Contains(parsedField.Field))
        {
            return parsedField;
        }

        foreach (var kv in tableFields)
        {
            if (kv.Value.Contains(parsedField.Field))
                return (kv.Key, parsedField.Field);
        }

        return parsedField;
    }

    private static ReportSubreportLink ToReportSubreportLink(SubreportObjectInfo info)
        => new()
        {
            Name = info.Name,
            SubreportName = info.SubreportName,
            AreaKind = info.AreaKind,
            SectionName = info.SectionName,
            Left = info.Left,
            Width = info.Width,
            XmlPath = info.XmlPath,
            Url = info.Url,
            EnableOnDemand = info.EnableOnDemand,
            IsInline = info.IsInline,
            ParameterLinks = info.ParameterLinks.Select(link => new ReportSubreportParameterLink
            {
                LinkedParameterName = link.LinkedParameterName,
                MainReportFieldName = link.MainReportFieldName,
                MainReportAlias = link.MainReportAlias,
                SubreportFieldName = link.SubreportFieldName,
                SubreportTable = link.SubreportTable,
                SubreportField = link.SubreportField
            }).ToList()
        };

    private static string ResolveSubreportXmlPath(string mainXmlPath, string xmlHint, string subreportName)
    {
        var baseDir = Path.GetDirectoryName(mainXmlPath) ?? ".";

        if (!string.IsNullOrWhiteSpace(xmlHint))
        {
            var normalizedHint = xmlHint.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalizedHint)
                ? normalizedHint
                : Path.Combine(baseDir, normalizedHint));
        }

        var fallback = Path.Combine(
            baseDir,
            Path.GetFileNameWithoutExtension(mainXmlPath) + ".subreports",
            SafeFileStem(subreportName) + ".xml");

        return File.Exists(fallback) ? Path.GetFullPath(fallback) : "";
    }

    private static string ResolvePublicWebUrl(string physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath)) return "";

        var normalized = Path.GetFullPath(physicalPath).Replace('\\', '/');
        const string marker = "/wwwroot/";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return "";

        var relative = normalized[(markerIndex + marker.Length)..];
        return "/" + string.Join("/",
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string SafeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (var c in "\\/:*?\"<>|")
            invalid.Add(c);

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);

        var cleaned = Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        while (cleaned.EndsWith('.') || cleaned.EndsWith(' '))
            cleaned = cleaned[..^1];

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Subreport";

        return cleaned.Length > 100 ? cleaned[..100].Trim() : cleaned;
    }

    private static bool AttributeIsTrue(XAttribute? attribute)
        => string.Equals((string?)attribute, "True", StringComparison.OrdinalIgnoreCase);

    private void ExtractReportHeadings(XElement report, ReportDefinition definition)
    {
        var candidates = new List<(string Text, double Size, int Top, int Left, string Alignment, string AreaKind)>();

        foreach (var area in report.Descendants("Area"))
        {
            var areaKind = (string?)area.Attribute("Kind") ?? "";
            if (areaKind != "ReportHeader" && areaKind != "PageHeader") continue;

            foreach (var section in area.Elements("Sections").Elements("Section"))
            {
                var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                    .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
                if (suppress.Replace(" ", "")
                        .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var textObj in section.Descendants("TextObject"))
                {
                    var text = ((string?)textObj.Element("Text") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.Contains('{')) continue; // group-title template, not a real title

                    var objectFormat = textObj.Element("ObjectFormat");
                    var hAlign = (string?)objectFormat?.Attribute("HorizontalAlignment") ?? "";
                    var font = textObj.Element("Font");
                    var bold = string.Equals((string?)font?.Attribute("Bold"), "True",
                                              StringComparison.OrdinalIgnoreCase);
                    var sizeStr = (string?)font?.Attribute("Size") ?? "10";
                    double.TryParse(sizeStr, out var size);

                    var isCentered = hAlign.Contains("Center", StringComparison.OrdinalIgnoreCase);
                    if (!isCentered && !(bold && size >= 12)) continue;

                    candidates.Add((
                        Text: text,
                        Size: size,
                        Top: ParseInt(textObj.Attribute("Top")),
                        Left: ParseInt(textObj.Attribute("Left")),
                        Alignment: hAlign,
                        AreaKind: areaKind));
                }
            }
        }
        if (candidates.Count == 0) return;

        var ordered = candidates
            .OrderByDescending(c => c.Size)
            .ThenBy(c => c.Top)
            .ToList();

        definition.Title = ordered[0].Text;
        definition.TitleAlignment = ResolveAlignment(ordered[0].Alignment);

        var subtitle = ordered.Skip(1).FirstOrDefault(c =>
            !string.Equals(c.Text, definition.Title, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(subtitle.Text))
        {
            definition.Subtitle = subtitle.Text;
            definition.SubtitleAlignment = ResolveAlignment(subtitle.Alignment);
        }
    }

    private static ReportAlignment ResolveAlignment(string crystalAlignment)
    {
        if (string.IsNullOrWhiteSpace(crystalAlignment)) return ReportAlignment.Center;
        if (crystalAlignment.Contains("Left", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Left;
        if (crystalAlignment.Contains("Right", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Right;
        if (crystalAlignment.Contains("Center", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Center;
        return ReportAlignment.Center;
    }

    private List<TableInfo> ParseTables(XElement report)
    {
        return report.Descendants("Table")
            .Where(t => t.Parent?.Name.LocalName == "Tables")
            .Select(t => new TableInfo(
                Alias: (string?)t.Attribute("Alias") ?? "",
                Name: (string?)t.Attribute("Name") ?? "",
                CommandSql: (t.Element("Command")?.Value)?.Trim()))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToList();
    }

    private List<TableLinkInfo> ParseTableLinks(XElement report)
    {
        var links = new List<TableLinkInfo>();
        foreach (var link in report.Descendants("TableLink"))
        {
            var joinType = (string?)link.Attribute("JoinType") ?? "Equal";
            var sourceFields = ParseFieldRefs(link.Element("SourceFields"));
            var destFields = ParseFieldRefs(link.Element("DestinationFields"));
            if (sourceFields.Count > 0 && destFields.Count > 0)
                links.Add(new TableLinkInfo(joinType, sourceFields, destFields));
        }
        return links;
    }

    private static List<(string Table, string Field)> ParseFieldRefs(XElement? container)
    {
        var list = new List<(string, string)>();
        if (container == null) return list;
        foreach (var f in container.Elements("Field"))
        {
            var formula = (string?)f.Attribute("FormulaName") ?? "";
            var parsed = ParseCrystalFieldRef(formula);
            if (parsed.HasValue)
                list.Add(parsed.Value);
        }
        return list;
    }

    private static (string Table, string Field)? ParseCrystalFieldRef(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return null;
        var m = Regex.Match(formula, @"^\{([^.}]+)\.([^}]+)\}$");
        if (!m.Success) return null;
        return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
    }

    private List<ReportParameter> ParseParameters(XElement report)
    {
        var list = new List<ReportParameter>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bindings = ExtractParameterBindings(report);
        var referencedParameters = ExtractReferencedParameterNames(report);

        var tableFields = ExtractTableFieldIndex(report);

        var paramDefs = report.Descendants("ParameterFieldDefinition")
            .Where(p =>
                (string?)p.Attribute("ParameterType") == "ReportParameter"
                && !string.Equals((string?)p.Attribute("IsLinkedToSubreport"),
                    "True", StringComparison.OrdinalIgnoreCase)
                && !p.Ancestors("SubReports").Any());

        foreach (var p in paramDefs)
        {
            var rawName = (string?)p.Attribute("Name") ?? "";
            if (string.IsNullOrWhiteSpace(rawName)) continue;

            var name = SanitizeParamName(rawName);
            if (string.IsNullOrEmpty(name)) continue;

            if (!seenNames.Add(name)) continue;

            var valueKind = (string?)p.Attribute("ParameterValueKind") ?? "StringParameter";
            var promptText = (string?)p.Attribute("PromptText") ?? "";
            var prompt = !string.IsNullOrWhiteSpace(promptText) ? promptText : rawName;
            var usage = (string?)p.Attribute("ParameterFieldUsage") ?? "";
            var promptIsOptional = string.Equals((string?)p.Attribute("IsOptionalPrompt"), "True", StringComparison.OrdinalIgnoreCase);

            var usageFlags = usage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isInUse = usageFlags.Any(f => string.Equals(f, "InUse", StringComparison.OrdinalIgnoreCase))
                || referencedParameters.Contains(name);

            var nameSaysOptional = rawName.IndexOf("(optional)", StringComparison.OrdinalIgnoreCase) >= 0;

            var defaultValues = p.Element("ParameterDefaultValues")?.Elements("ParameterDefaultValue")
                .Select(d => new ReportParameterChoice(
                    Description: (string?)d.Attribute("Description") ?? "",
                    Value: (string?)d.Attribute("Value") ?? ""))
                .ToList() ?? new List<ReportParameterChoice>();

            var defaultVal = (string?)p.Element("ParameterCurrentValues")?.Element("ParameterCurrentValue")?.Attribute("Value")
                           ?? defaultValues.FirstOrDefault()?.Value
                           ?? "";

            var pickListSql = p.Element("PickListSql")?.Value;
            if (string.IsNullOrWhiteSpace(pickListSql)) pickListSql = null;

            var (bindingTable, bindingField) = bindings.TryGetValue(name, out var b)
                ? b
                : ("", "");
            var bindingDescField = ResolveDescriptionField(bindingTable, bindingField, tableFields);

            var looksLikeBaseTable = !string.IsNullOrEmpty(bindingTable)
                && bindingTable.StartsWith("tbl", StringComparison.OrdinalIgnoreCase);
            if (pickListSql == null && looksLikeBaseTable && !string.IsNullOrEmpty(bindingField))
            {
                pickListSql = SynthesizePickListSql(bindingTable, bindingField, bindingDescField, tableFields);
            }

            list.Add(new ReportParameter
            {
                Name = name,
                DisplayName = rawName,
                Prompt = prompt,
                ParameterType = MapParameterType(valueKind),
                Required = !promptIsOptional && isInUse,
                IsOptional = nameSaysOptional,
                AllowMultiple = string.Equals((string?)p.Attribute("EnableAllowMultipleValue"), "True", StringComparison.OrdinalIgnoreCase),
                AllowCustomCurrentValues = string.Equals((string?)p.Attribute("AllowCustomCurrentValues"), "True", StringComparison.OrdinalIgnoreCase),
                DefaultValue = defaultVal,
                DefaultValues = defaultValues,
                PickListSql = pickListSql,
                BindingTable = bindingTable,
                BindingField = bindingField,
                BindingDescriptionField = bindingDescField
            });
        }
        return list;
    }

    private Dictionary<string, (string Table, string Field)> ExtractParameterBindings(XElement report)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var formula = (string?)report.Element("DataDefinition")?.Element("RecordSelectionFormula") ?? "";
        if (string.IsNullOrWhiteSpace(formula)) return result;

        var rx1 = new Regex(
            @"\{([A-Za-z_]\w*)\.([^}]+)\}\s*(?:=|<>|!=|\bin\b|\blike\b)\s*\{\?([^}]+)\}",
            RegexOptions.IgnoreCase);
        var rx2 = new Regex(
            @"\{\?([^}]+)\}\s*(?:=|<>|!=|\bin\b|\blike\b)\s*\{([A-Za-z_]\w*)\.([^}]+)\}",
            RegexOptions.IgnoreCase);

        foreach (Match m in rx1.Matches(formula))
        {
            var table = m.Groups[1].Value.Trim();
            var field = m.Groups[2].Value.Trim();
            var pname = SanitizeParamName(m.Groups[3].Value);
            if (!string.IsNullOrEmpty(pname) && !result.ContainsKey(pname))
                result[pname] = (table, field);
        }
        foreach (Match m in rx2.Matches(formula))
        {
            var pname = SanitizeParamName(m.Groups[1].Value);
            var table = m.Groups[2].Value.Trim();
            var field = m.Groups[3].Value.Trim();
            if (!string.IsNullOrEmpty(pname) && !result.ContainsKey(pname))
                result[pname] = (table, field);
        }
        return result;
    }

    private HashSet<string> ExtractReferencedParameterNames(XElement report)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var formulaTexts = new List<string>();
        var dataDefinition = report.Element("DataDefinition");
        if (dataDefinition != null)
        {
            formulaTexts.Add((string?)dataDefinition.Element("RecordSelectionFormula") ?? "");
            formulaTexts.Add((string?)dataDefinition.Element("GroupSelectionFormula") ?? "");
            formulaTexts.AddRange(dataDefinition
                .Descendants("FormulaFieldDefinition")
                .Select(f => f.Value ?? ""));
        }

        foreach (var formula in formulaTexts)
        {
            if (string.IsNullOrWhiteSpace(formula)) continue;
            foreach (Match match in Regex.Matches(formula, @"\{\?([^}]+)\}"))
            {
                var name = SanitizeParamName(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static Dictionary<string, HashSet<string>> ExtractTableFieldIndex(XElement report)
    {
        var idx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tbl in report.Descendants("Table"))
        {
            var alias = (string?)tbl.Attribute("Alias") ?? (string?)tbl.Attribute("Name") ?? "";
            if (string.IsNullOrWhiteSpace(alias)) continue;
            if (!idx.TryGetValue(alias, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                idx[alias] = set;
            }
            foreach (var f in tbl.Element("Fields")?.Elements("Field") ?? Enumerable.Empty<XElement>())
            {
                var fname = (string?)f.Attribute("Name");
                if (!string.IsNullOrWhiteSpace(fname)) set.Add(fname);
            }
        }
        return idx;
    }

    private string ResolveDescriptionField(string table, string field,
        Dictionary<string, HashSet<string>> tableFields)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(field)) return "";
        if (!tableFields.TryGetValue(table, out var fields)) return "";

        var direct = field + "Desc";
        if (fields.Contains(direct)) return direct;

        foreach (var prefix in new[] { "Cost", "Source", "Pricing" })
        {
            if (field.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && field.Length > prefix.Length)
            {
                var stripped = field.Substring(prefix.Length);
                var candidate = stripped + "Desc";
                if (fields.Contains(candidate)) return candidate;
            }
        }

        if (fields.Contains("Description")) return "Description";

        return "";
    }

    private string SynthesizePickListSql(string table, string field, string descField,
        Dictionary<string, HashSet<string>> tableFields)
    {
        var selectExpr = string.IsNullOrEmpty(descField)
            ? $"SELECT DISTINCT [{field}] AS Value, [{field}] AS Description"
            : $"SELECT DISTINCT [{field}] AS Value, ISNULL([{descField}], [{field}]) AS Description";

        var where = $"WHERE ISNULL([{field}],'') <> ''";
        if (tableFields.TryGetValue(table, out var fields) && fields.Contains("DivisionID"))
            where = $"WHERE DivisionID = @DivisionID AND ISNULL([{field}],'') <> ''";

        return $"{selectExpr} FROM [{table}] {where} ORDER BY [{field}]";
    }

    private static string SanitizeParamName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        return sb.ToString();
    }

    private static ReportParameterType MapParameterType(string valueKind) => valueKind switch
    {
        "NumberParameter" => ReportParameterType.Decimal,
        "CurrencyParameter" => ReportParameterType.Decimal,
        "DateParameter" => ReportParameterType.Date,
        "DateTimeParameter" => ReportParameterType.Date,
        "BooleanParameter" => ReportParameterType.Boolean,
        _ => ReportParameterType.String
    };

    private bool IsDetailSuppressedAtTopLevel(XElement report)
    {
        var detailSections = report.Descendants("Area")
            .Where(a => (string?)a.Attribute("Kind") == "Detail")
            .SelectMany(a => a.Descendants("Section"));

        foreach (var section in detailSections)
        {
            var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
            if (suppress.Replace(" ", "").Replace("\"", "")
                    .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private XElement? FindInnermostVisibleDataSection(XElement report)
    {
        XElement? best = null;
        foreach (var area in report.Descendants("Area"))
        {
            if ((string?)area.Attribute("Kind") != "GroupHeader") continue;

            var areaFormat = area.Element("AreaFormat");
            var hideForDrill = string.Equals((string?)areaFormat?.Attribute("EnableHideForDrillDown"),
                                              "True", StringComparison.OrdinalIgnoreCase);
            if (hideForDrill) continue;

            foreach (var section in area.Descendants("Section"))
            {
                var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                    .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
                var isTopSuppressed = suppress.Replace(" ", "")
                    .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isTopSuppressed) continue;

                if (!section.Descendants("FieldObject").Any(f => !IsSuppressedFieldObject(f))) continue;

                best = section;
            }
        }
        return best;
    }

    private List<DetailFieldInfo> ExtractSectionFields(XElement section,
        Dictionary<string, string> formulaFields, bool detectFormulas)
    {
        var list = new List<DetailFieldInfo>();
        var area = section.Ancestors("Area").FirstOrDefault();
        var areaKind = (string?)area?.Attribute("Kind") ?? "";
        var sectionKind = (string?)section.Attribute("Kind") ?? "";
        var sectionName = (string?)section.Attribute("Name") ?? "";
        foreach (var field in section.Descendants("FieldObject"))
        {
            if (IsSuppressedFieldObject(field)) continue;

            var ds = (string?)field.Attribute("DataSource") ?? "";
            if (ds.StartsWith("Sum ", StringComparison.OrdinalIgnoreCase) ||
                ds.StartsWith("Count ", StringComparison.OrdinalIgnoreCase) ||
                ds.StartsWith("Avg ", StringComparison.OrdinalIgnoreCase)) continue;

            var isFormula = ds.StartsWith("{@");
            var parsed = ParseCrystalFieldRef(ds);

            var name = (string?)field.Attribute("Name") ?? "";
            var left = ParseInt(field.Attribute("Left"));
            var width = ParseInt(field.Attribute("Width"));
            var alignment = (string?)field.Element("ObjectFormat")?.Attribute("HorizontalAlignment") ?? "DefaultAlign";
            var textColor = ParseCrystalColor(field.Element("Color"));
            var bold = AttrIsTrue(field.Element("Font"), "Bold");
            var conditionalStyle = ParseConditionalStyle(field);

            if (ds.StartsWith("GroupName", StringComparison.OrdinalIgnoreCase))
            {
                var gn = Regex.Match(ds, @"GroupName\s*\(\s*\{([A-Za-z_][\w]*)\.([^}]+)\}\s*\)");
                if (gn.Success)
                {
                    list.Add(new DetailFieldInfo(
                        Name: name,
                        Table: gn.Groups[1].Value,
                        Field: gn.Groups[2].Value.Trim(),
                        Left: left,
                        Width: width,
                        Alignment: alignment,
                        DataSource: ds,
                        IsFormula: false,
                        TextColor: textColor,
                        Bold: bold,
                        ConditionalStyle: conditionalStyle,
                        AreaKind: areaKind,
                        SectionKind: sectionKind,
                        SectionName: sectionName));
                }
                continue;
            }

            if (parsed.HasValue)
            {
                list.Add(new DetailFieldInfo(
                    Name: name,
                    Table: parsed.Value.Table,
                    Field: parsed.Value.Field,
                    Left: left,
                    Width: width,
                    Alignment: alignment,
                    DataSource: ds,
                    IsFormula: false,
                    TextColor: textColor,
                    Bold: bold,
                    ConditionalStyle: conditionalStyle,
                    AreaKind: areaKind,
                    SectionKind: sectionKind,
                    SectionName: sectionName));
            }
            else if (isFormula && detectFormulas)
            {
                list.Add(new DetailFieldInfo(
                    Name: name,
                    Table: "",
                    Field: ds.Trim('{', '}'),  // e.g. "@Total Selling" or "@Profit"
                    Left: left,
                    Width: width,
                    Alignment: alignment,
                    DataSource: ds,
                    IsFormula: true,
                    TextColor: textColor,
                    Bold: bold,
                    ConditionalStyle: conditionalStyle,
                    AreaKind: areaKind,
                    SectionKind: sectionKind,
                    SectionName: sectionName));
            }
        }
        return list.OrderBy(f => f.Left).ToList();
    }

    private List<ReportColumn> BuildColumnsFromSection(List<DetailFieldInfo> fields,
        List<PageHeaderLabel> labels, Dictionary<string, string> formulaFields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        var cols = new List<ReportColumn>();
        var assignedLabels = AssignLabelsByCenter(fields, labels);
        var usedLabels = new HashSet<string>(assignedLabels.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var df in fields)
        {
            string alias;
            string header;
            if (df.IsFormula)
            {
                var formulaKey = df.Field;  // e.g. "@Profit"
                var resolved = ResolveFormulaToField(formulaKey, formulaFields);
                if (resolved.HasValue)
                {
                    alias = $"{resolved.Value.Table}_{resolved.Value.Field}";
                }
                else
                {
                    alias = FormulaColumnAlias(formulaKey);
                }
                header = assignedLabels.TryGetValue(df, out var assigned) ? assigned
                       : MatchHeaderLabel(labels, df, usedLabels);
                if (string.IsNullOrWhiteSpace(header))
                    header = formulaKey.TrimStart('@');
            }
            else
            {
                alias = $"{df.Table}_{df.Field}";
                header = assignedLabels.TryGetValue(df, out var assigned) ? assigned
                       : MatchHeaderLabel(labels, df, usedLabels);
                if (string.IsNullOrWhiteSpace(header)) header = df.Field;
            }

            if (!string.IsNullOrEmpty(header)) usedLabels.Add(header);
            if (cols.Any(c => c.Field == alias)) continue; // deduplicate
            cols.Add(new ReportColumn
            {
                Field = alias,
                HeaderText = header,
                Width = TwipsToPx(df.Width),
                Alignment = MapAlignment(df.Alignment),
                ColumnType = InferColumnType(formulaKey: df.IsFormula ? df.Field : null, headerText: header, fieldName: df.Field),
                TextColor = df.TextColor,
                Bold = df.Bold,
                ConditionalStyle = ToReportConditionalStyle(df.ConditionalStyle),
                LayoutLeft = df.Left
            });
        }
        return AddSubreportObjectColumns(cols, fields, subreportObjects);
    }

    private static List<ReportColumn> AddSubreportObjectColumns(
        List<ReportColumn> columns,
        IReadOnlyList<DetailFieldInfo> fields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        if (subreportObjects.Count == 0 || fields.Count == 0)
            return columns;

        var sectionKeys = fields
            .Select(f => SectionKey(f.AreaKind, f.SectionKind, f.SectionName))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sectionKeys.Count == 0)
            return columns;

        var usedFields = columns
            .Select(c => c.Field)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var subreport in subreportObjects
            .Where(s => s.EnableOnDemand && sectionKeys.Contains(SectionKey(s.AreaKind, s.SectionKind, s.SectionName)))
            .OrderBy(s => s.Left))
        {
            columns.Add(new ReportColumn
            {
                Field = BuildSubreportColumnField(subreport, usedFields),
                HeaderText = "",
                Width = TwipsToPx(subreport.Width),
                Alignment = ReportAlignment.Left,
                ColumnType = ReportColumnType.Text,
                LayoutLeft = subreport.Left,
                IsSubreportObject = true,
                SubreportLink = ToReportSubreportLink(subreport)
            });
        }

        return columns
            .OrderBy(c => c.LayoutLeft)
            .ThenBy(c => c.IsSubreportObject ? 1 : 0)
            .ToList();
    }

    private static string SectionKey(string areaKind, string sectionKind, string sectionName)
        => $"{areaKind}|{sectionKind}|{sectionName}";

    private static string BuildSubreportColumnField(SubreportObjectInfo subreport, HashSet<string> usedFields)
    {
        var stem = Regex.Replace(
            string.IsNullOrWhiteSpace(subreport.Name) ? subreport.SubreportName : subreport.Name,
            @"[^\w]+",
            "_").Trim('_');
        if (string.IsNullOrWhiteSpace(stem))
            stem = "Subreport";

        var field = SubreportColumnPrefix + stem;
        var suffix = 2;
        while (!usedFields.Add(field))
            field = $"{SubreportColumnPrefix}{stem}_{suffix++}";
        return field;
    }

    private static string FormulaColumnAlias(string formulaName)
        => "Formula_" + formulaName.TrimStart('@').Replace(" ", "");

    private static Dictionary<DetailFieldInfo, string> AssignLabelsByCenter(
        List<DetailFieldInfo> fields, List<PageHeaderLabel> labels)
    {
        var map = new Dictionary<DetailFieldInfo, string>();
        if (fields.Count == 0 || labels.Count == 0) return map;

        var assignedFields = new HashSet<DetailFieldInfo>();
        foreach (var label in labels)
        {
            var labelCenter = label.Left + label.Width / 2.0;
            DetailFieldInfo? best = null;
            double bestDist = double.MaxValue;
            foreach (var df in fields)
            {
                if (assignedFields.Contains(df)) continue; // one label per field
                if (!(label.Left + label.Width >= df.Left && label.Left <= df.Left + df.Width))
                    continue;
                if (IsMoneyLabel(label.Text) && IsCodeLikeField(df))
                    continue;

                var fieldCenter = df.Left + df.Width / 2.0;
                var dist = Math.Abs(labelCenter - fieldCenter);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = df;
                }
            }
            if (best != null)
            {
                map[best] = label.Text;
                assignedFields.Add(best);
            }
        }
        return map;
    }

    private static bool IsMoneyLabel(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("price") || t.Contains("cost") || t.Contains("profit") ||
               t.Contains("selling") || t.Contains("amount") || t.Contains("total");
    }

    private static bool IsCodeLikeField(DetailFieldInfo df)
    {
        if (df.IsFormula) return false;
        var f = df.Field.ToLowerInvariant();
        return f.EndsWith("code") || f.EndsWith("id") || f.EndsWith("index") ||
               f.EndsWith("num") || f.EndsWith("number") || f.EndsWith("key");
    }

    private static ReportColumnType InferColumnType(string? formulaKey, string? headerText, string? fieldName = null)
    {
        if (!string.IsNullOrEmpty(fieldName))
        {
            var fn = fieldName.ToLowerInvariant();
            if (fn.EndsWith("code") || fn.EndsWith("id") || fn.EndsWith("index") ||
                fn.EndsWith("num") || fn.EndsWith("number") || fn.EndsWith("key"))
                return ReportColumnType.Text;
        }

        var hint = (formulaKey + " " + headerText).ToLowerInvariant();
        if (hint.Contains("price") || hint.Contains("cost") || hint.Contains("profit") ||
            hint.Contains("amount") || hint.Contains("total") || hint.Contains("selling"))
            return ReportColumnType.Currency;
        return ReportColumnType.Text;
    }

    private (string Table, string Field)? ResolveFormulaToField(string formulaName,
        Dictionary<string, string> formulaFields)
    {
        if (!formulaFields.TryGetValue(formulaName, out var sql)) return null;
        sql = sql.Trim();
        var m = Regex.Match(sql, @"^\s*([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)\s*$");
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);
        return null;
    }

    private List<string> BuildSelectFieldsForSummary(List<ReportColumn> columns,
        List<GroupInfo> groups, Dictionary<string, string> formulaFields,
        List<DetailFieldInfo> originalDetailFields)
    {
        var selects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            if (col.IsSubreportObject)
                continue;

            if (col.Field.StartsWith("Formula_"))
            {
                var needle = col.Field.Substring("Formula_".Length);
                var match = formulaFields.FirstOrDefault(kv =>
                    string.Equals(kv.Key.TrimStart('@').Replace(" ", ""), needle,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Value))
                {
                    var inlined = InlineFormulaReferences(match.Value, formulaFields);
                    if (seen.Add(col.Field)) selects.Add($"({inlined}) AS [{col.Field}]");
                }
                continue;
            }
            if (seen.Add(col.Field)) selects.Add(col.Field);
        }

        foreach (var g in groups.Where(g => g.IsVisible))
        {
            if (g.FormulaName != null)
            {
                if (formulaFields.TryGetValue(g.FormulaName, out var sql))
                {
                    var alias = FormulaGroupAlias(g.FormulaName);
                    var computed = $"({sql}) AS [{alias}]";
                    if (seen.Add(alias)) selects.Add(computed);
                }
            }
            else
            {
                var alias = $"{g.Table}_{g.Field}";
                if (seen.Add(alias)) selects.Add(alias);
            }
            AppendDescriptionSelect(g, seen, selects);
        }
        return selects;
    }

    private List<string> BuildSelectFieldsForDrillDown(List<ReportColumn> columns,
        List<GroupInfo> groups, Dictionary<string, string> formulaFields)
    {
        var selects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            if (col.IsSubreportObject)
                continue;

            if (col.Field.StartsWith("Formula_"))
            {
                var needle = col.Field.Substring("Formula_".Length); // "TotalCost"
                var match = formulaFields.FirstOrDefault(kv =>
                    string.Equals(kv.Key.TrimStart('@').Replace(" ", ""), needle,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Value))
                {
                    var inlined = InlineFormulaReferences(match.Value, formulaFields);
                    if (seen.Add(col.Field)) selects.Add($"({inlined}) AS [{col.Field}]");
                }
                continue;
            }
            if (seen.Add(col.Field)) selects.Add(col.Field);
        }

        foreach (var g in groups)
        {
            if (g.FormulaName != null)
            {
                if (formulaFields.TryGetValue(g.FormulaName, out var sql))
                {
                    var alias = FormulaGroupAlias(g.FormulaName);
                    if (seen.Add(alias)) selects.Add($"({sql}) AS [{alias}]");
                }
            }
            else
            {
                var alias = $"{g.Table}_{g.Field}";
                if (seen.Add(alias)) selects.Add(alias);
            }
            AppendDescriptionSelect(g, seen, selects);
        }
        return selects;
    }

    private string InlineFormulaReferences(string sql, Dictionary<string, string> formulaFields, int depth = 0)
    {
        if (depth > 10) return sql; // runaway guard
        return Regex.Replace(sql, @"\{@([^}]+)\}", m =>
        {
            var key = "@" + m.Groups[1].Value.Trim();
            return formulaFields.TryGetValue(key, out var inner)
                ? "(" + InlineFormulaReferences(inner, formulaFields, depth + 1) + ")"
                : m.Value;
        });
    }

    private List<DetailFieldInfo> ParseDetailFields(XElement report)
    {
        var list = new List<DetailFieldInfo>();
        var detailAreas = report.Descendants("Area")
            .Where(a => (string?)a.Attribute("Kind") == "Detail");

        foreach (var area in detailAreas)
        {
            var sections = area.Elements("Sections").Elements("Section")
                .Where(s => (string?)s.Attribute("Kind") == "Detail")
                .ToList();
            if (sections.Count == 0) continue;
            var primarySection = sections[0];
            var areaKind = (string?)area.Attribute("Kind") ?? "";
            var sectionKind = (string?)primarySection.Attribute("Kind") ?? "";
            var sectionName = (string?)primarySection.Attribute("Name") ?? "";

            foreach (var field in primarySection.Descendants("FieldObject"))
            {
                if (IsSuppressedFieldObject(field)) continue;

                var ds = (string?)field.Attribute("DataSource") ?? "";
                var parsed = ParseCrystalFieldRef(ds);
                var isFormula = ds.StartsWith("{@"); // formula field like {@Total Cost}
                if (!parsed.HasValue && !isFormula) continue;

                var name = (string?)field.Attribute("Name") ?? "";
                var left = ParseInt(field.Attribute("Left"));
                var width = ParseInt(field.Attribute("Width"));
                var alignment = (string?)field.Element("ObjectFormat")?.Attribute("HorizontalAlignment") ?? "DefaultAlign";
                var textColor = ParseCrystalColor(field.Element("Color"));
                var bold = AttrIsTrue(field.Element("Font"), "Bold");
                var conditionalStyle = ParseConditionalStyle(field);

                var fieldName = parsed.HasValue ? parsed.Value.Field : ds.Trim('{', '}');

                list.Add(new DetailFieldInfo(
                    Name: name,
                    Table: parsed?.Table ?? "",
                    Field: fieldName,
                    Left: left,
                    Width: width,
                    Alignment: alignment,
                    DataSource: ds,
                    IsFormula: isFormula,
                    TextColor: textColor,
                    Bold: bold,
                    ConditionalStyle: conditionalStyle,
                    AreaKind: areaKind,
                    SectionKind: sectionKind,
                    SectionName: sectionName));
            }
        }
        return list.OrderBy(f => f.Left).ToList();
    }

    private List<PageHeaderLabel> ParseHeaderLabels(XElement report)
    {
        var list = new List<PageHeaderLabel>();
        var headerAreas = report.Descendants("Area")
            .Where(a =>
            {
                var kind = (string?)a.Attribute("Kind") ?? "";
                return kind == "PageHeader" || kind == "GroupHeader";
            });

        foreach (var area in headerAreas)
        {
            var hideForDrill = string.Equals((string?)area.Element("AreaFormat")?.Attribute("EnableHideForDrillDown"),
                                              "True", StringComparison.OrdinalIgnoreCase);
            if (hideForDrill) continue;

            foreach (var section in area.Elements("Sections").Elements("Section"))
            {
                var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                    .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
                if (suppress.Replace(" ", "")
                        .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var obj in section.Descendants().Where(e => e.Name.LocalName is "TextObject" or "FieldHeadingObject"))
                {
                    var text = (string?)obj.Element("Text") ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.Contains('{')) continue;

                    var left = ParseInt(obj.Attribute("Left"));
                    var width = ParseInt(obj.Attribute("Width"));
                    var forField = (string?)obj.Attribute("FieldObjectName");
                    list.Add(new PageHeaderLabel(text.Trim(), left, width, forField));
                }
            }
        }
        return list;
    }

    private List<GroupInfo> ParseGroups(XElement report)
    {
        var groupHeaderAreas = report.Descendants("Area")
            .Where(a => (string?)a.Attribute("Kind") == "GroupHeader")
            .ToList();
        var groupFooterAreas = report.Descendants("Area")
            .Where(a => (string?)a.Attribute("Kind") == "GroupFooter")
            .ToList();

        var groupElements = report.Descendants("Group").ToList();
        var totalGroups = groupElements.Count;

        var list = new List<GroupInfo>();
        for (int groupIndex = 0; groupIndex < groupElements.Count; groupIndex++)
        {
            var g = groupElements[groupIndex];
            var condField = (string?)g.Attribute("ConditionField") ?? "";

            bool visible = true;
            XElement? headerArea = groupIndex < groupHeaderAreas.Count
                ? groupHeaderAreas[groupIndex]
                : null;
            if (headerArea != null)
            {
                var hideForDrill = string.Equals((string?)headerArea.Element("AreaFormat")?.Attribute("EnableHideForDrillDown"),
                                                  "True", StringComparison.OrdinalIgnoreCase);
                if (hideForDrill)
                    visible = false;
                else
                {
                    var sections = headerArea.Elements("Sections").Elements("Section").ToList();
                    if (sections.Count > 0 && sections.All(sec =>
                        ((string?)sec.Descendants("SectionAreaConditionFormulas").FirstOrDefault()?.Attribute("EnableSuppress") ?? "")
                            .Replace(" ", "")
                            .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0))
                        visible = false;
                }
            }

            bool pageBreak = AttrIsTrue(headerArea?.Element("AreaFormat"), "EnableNewPageBefore");
            int footerIndex = totalGroups - 1 - groupIndex;
            if (footerIndex >= 0 && footerIndex < groupFooterAreas.Count)
            {
                var footerArea = groupFooterAreas[footerIndex];
                if (AttrIsTrue(footerArea.Element("AreaFormat"), "EnableNewPageAfter"))
                    pageBreak = true;
            }

            var descTable = "";
            var descField = "";
            if (headerArea != null)
            {
                foreach (var fieldObj in headerArea.Descendants("FieldObject"))
                {
                    var ds = (string?)fieldObj.Attribute("DataSource") ?? "";
                    if (string.IsNullOrWhiteSpace(ds)) continue;
                    if (ds.StartsWith("GroupName", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(ds, condField, StringComparison.OrdinalIgnoreCase)) continue;
                    var fieldRef = ParseCrystalFieldRef(ds);
                    if (!fieldRef.HasValue) continue;
                    descTable = fieldRef.Value.Table;
                    descField = fieldRef.Value.Field;
                    break;
                }

                if (string.IsNullOrEmpty(descField))
                {
                    foreach (var textObj in headerArea.Descendants("TextObject"))
                    {
                        var text = (string?)textObj.Element("Text") ?? "";
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        var matches = Regex.Matches(text, @"\{([A-Za-z_][\w]*)\.([^}]+)\}");
                        foreach (Match m in matches)
                        {
                            var refExpr = m.Value;
                            if (string.Equals(refExpr, condField, StringComparison.OrdinalIgnoreCase)) continue;
                            var fieldRef = ParseCrystalFieldRef(refExpr);
                            if (!fieldRef.HasValue) continue;
                            descTable = fieldRef.Value.Table;
                            descField = fieldRef.Value.Field;
                            break;
                        }
                        if (!string.IsNullOrEmpty(descField)) break;
                    }
                }
            }

            var formulaMatch = Regex.Match(condField, @"^\{@([^}]+)\}$");
            if (formulaMatch.Success)
            {
                list.Add(new GroupInfo("", "", "@" + formulaMatch.Groups[1].Value.Trim(),
                    IsVisible: visible, PageBreakBefore: pageBreak,
                    DescriptionTable: descTable, DescriptionField: descField));
                continue;
            }
            var parsed = ParseCrystalFieldRef(condField);
            if (parsed.HasValue)
                list.Add(new GroupInfo(parsed.Value.Table, parsed.Value.Field, null,
                    IsVisible: visible, PageBreakBefore: pageBreak,
                    DescriptionTable: descTable, DescriptionField: descField));
        }
        return list;
    }

    private static bool AttrIsTrue(XElement? element, string attrName)
    {
        if (element == null) return false;
        return string.Equals((string?)element.Attribute(attrName), "True",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuppressedFieldObject(XElement field)
        => AttrIsTrue(field.Element("ObjectFormat"), "EnableSuppress");

    private static string ParseCrystalColor(XElement? color)
    {
        if (color == null) return "";
        var alpha = ParseInt(color.Attribute("A"));
        if (alpha == 0) return "";

        var r = Math.Clamp(ParseInt(color.Attribute("R")), 0, 255);
        var g = Math.Clamp(ParseInt(color.Attribute("G")), 0, 255);
        var b = Math.Clamp(ParseInt(color.Attribute("B")), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static ConditionalStyleInfo? ParseConditionalStyle(XElement field)
    {
        var formulas = field.Element("FontColorConditionFormulas");
        if (formulas == null) return null;

        var colorRule = ParseCrystalConditionalToken((string?)formulas.Attribute("Color"));
        var styleRule = ParseCrystalConditionalToken((string?)formulas.Attribute("Style"));
        if (colorRule == null && styleRule == null) return null;

        var source = colorRule ?? styleRule!;
        var trueColor = colorRule != null ? CrystalColorTokenToCss(colorRule.TrueToken) : "";
        var falseColor = colorRule != null ? CrystalColorTokenToCss(colorRule.FalseToken) : "";
        var trueBold = styleRule != null ? CrystalStyleTokenToBold(styleRule.TrueToken) : null;
        var falseBold = styleRule != null ? CrystalStyleTokenToBold(styleRule.FalseToken) : null;

        if (colorRule != null && styleRule != null &&
            (!string.Equals(colorRule.FieldAlias, styleRule.FieldAlias, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(colorRule.Operator, styleRule.Operator, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(colorRule.Value, styleRule.Value, StringComparison.OrdinalIgnoreCase)))
        {
            trueBold = null;
            falseBold = null;
        }

        return new ConditionalStyleInfo(
            source.FieldAlias, source.Operator, source.Value,
            trueColor, falseColor, trueBold, falseBold);
    }

    private sealed record CrystalConditionalTokenRule(
        string FieldAlias, string Operator, string Value, string TrueToken, string FalseToken);

    private static CrystalConditionalTokenRule? ParseCrystalConditionalToken(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return null;
        var normalized = Regex.Replace(formula, @"\s+", " ").Trim();
        var match = Regex.Match(normalized,
            @"^if\s+\{(?<table>[A-Za-z_][\w]*)\.(?<field>[^}]+)\}\s*(?<op>=|<>|>=|<=|>|<)\s*(?<value>'[^']*'|""[^""]*""|[-+]?\d+(?:\.\d+)?|true|false)\s+then\s+(?<then>cr[A-Za-z]+)\s+else\s+(?<else>cr[A-Za-z]+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var value = match.Groups["value"].Value.Trim().Trim('\'', '"');
        var alias = $"{match.Groups["table"].Value}_{match.Groups["field"].Value.Trim()}";
        return new CrystalConditionalTokenRule(
            alias,
            match.Groups["op"].Value,
            value,
            match.Groups["then"].Value,
            match.Groups["else"].Value);
    }

    private static string CrystalColorTokenToCss(string token)
    {
        return token.Trim().ToLowerInvariant() switch
        {
            "crblack" => "#000000",
            "crblue" => "#0000FF",
            "crgreen" => "#008000",
            "crmaroon" => "#800000",
            "crnavy" => "#000080",
            "crpurple" => "#800080",
            "crred" => "#FF0000",
            "crsilver" => "#C0C0C0",
            "crteal" => "#008080",
            "crwhite" => "#FFFFFF",
            "cryellow" => "#FFFF00",
            _ => ""
        };
    }

    private static bool? CrystalStyleTokenToBold(string token)
    {
        return token.Trim().ToLowerInvariant() switch
        {
            "crbold" => true,
            "crregular" => false,
            _ => null
        };
    }

    private static ReportColumnConditionalStyle? ToReportConditionalStyle(ConditionalStyleInfo? style)
    {
        if (style == null) return null;
        return new ReportColumnConditionalStyle
        {
            Field = style.FieldAlias,
            Operator = style.Operator,
            Value = style.Value,
            TrueTextColor = style.TrueTextColor,
            FalseTextColor = style.FalseTextColor,
            TrueBold = style.TrueBold,
            FalseBold = style.FalseBold
        };
    }

    private List<SortFieldInfo> ParseSortFields(XElement report)
    {
        var list = new List<SortFieldInfo>();
        foreach (var s in report.Descendants("SortField"))
        {
            var field = (string?)s.Attribute("Field") ?? "";
            var direction = (string?)s.Attribute("SortDirection") ?? "AscendingOrder";
            var sortType = (string?)s.Attribute("SortType") ?? "RecordSortField";

            var formulaMatch = Regex.Match(field, @"^\{@([^}]+)\}$");
            if (formulaMatch.Success)
            {
                list.Add(new SortFieldInfo("", "", "@" + formulaMatch.Groups[1].Value.Trim(), direction, sortType));
                continue;
            }
            var parsed = ParseCrystalFieldRef(field);
            if (parsed.HasValue)
                list.Add(new SortFieldInfo(parsed.Value.Table, parsed.Value.Field, null, direction, sortType));
        }
        return list;
    }

    private Dictionary<string, string> ParseFormulaFields(XElement report)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in report.Descendants("FormulaFieldDefinition"))
        {
            var formulaName = (string?)f.Attribute("FormulaName") ?? "";
            var name = Regex.Match(formulaName, @"^\{(@[^}]+)\}$");
            if (!name.Success) continue;

            var body = f.Value ?? ""; // inner text of the element
            var sql = TranslateCrystalFormula(body);
            map[name.Groups[1].Value] = sql;
        }
        return map;
    }

    private string TranslateCrystalFormula(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "NULL";

        var lines = body.Split('\n')
            .Select(l => Regex.Replace(l, @"//.*$", "").Trim())
            .Where(l => l.Length > 0);
        var text = string.Join(' ', lines).Trim();
        if (text.Length == 0) return "NULL";

        text = Regex.Replace(text, @"\s*;\s*$", "");
        if (text.Length == 0) return "NULL";

        if (Regex.IsMatch(text, @"\bshared\s+(numbervar|stringvar|currencyvar|datevar|datetimevar|timevar|booleanvar)\b",
                RegexOptions.IgnoreCase))
            return "NULL";

        text = Regex.Replace(text, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" [{m.Groups[1].Value}].{m.Groups[2].Value.Trim()} ");
        text = Regex.Replace(text, "\"([^\"]*)\"", m => "'" + m.Groups[1].Value.Replace("'", "''") + "'");

        var ifMatch = Regex.Match(text,
            @"^\s*If\s+(.+?)\s+Then\s+(.+?)\s+Else\s+(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (ifMatch.Success)
        {
            var cond = TranslateCrystalCondition(ifMatch.Groups[1].Value);
            var thenVal = TranslateCrystalFunctions(ifMatch.Groups[2].Value.Trim());
            var elseVal = TranslateCrystalFormula(ifMatch.Groups[3].Value.Trim());
            return $"CASE WHEN {cond} THEN {thenVal} ELSE {elseVal} END";
        }

        if (Regex.IsMatch(text, @"^\s*If\b", RegexOptions.IgnoreCase))
            return "NULL";

        return TranslateCrystalFunctions(text);
    }

    private string TranslateCrystalCondition(string cond)
    {
        cond = cond.Trim();
        cond = Regex.Replace(cond, @"IsNull\s*\(\s*([^)]+?)\s*\)",
            m => $"({m.Groups[1].Value.Trim()} IS NULL)", RegexOptions.IgnoreCase);
        cond = Regex.Replace(cond, @"\bor\b", "OR", RegexOptions.IgnoreCase);
        cond = Regex.Replace(cond, @"\band\b", "AND", RegexOptions.IgnoreCase);
        cond = Regex.Replace(cond, @"\bnot\b", "NOT", RegexOptions.IgnoreCase);
        return TranslateCrystalFunctions(cond);
    }

    private static string TranslateLikeWildcards(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return formula;

        return Regex.Replace(formula,
            @"\blike\s+([^()]*?)(?=\s+(?:and|or)\b|\s*\)|\s*$)",
            m =>
            {
                var rhs = m.Groups[1].Value;
                rhs = Regex.Replace(rhs, @"'([^']*)'", lit =>
                {
                    var s = lit.Groups[1].Value;
                    s = s.Replace('*', '%').Replace('?', '_');
                    return "'" + s + "'";
                });
                return "like " + rhs;
            },
            RegexOptions.IgnoreCase);
    }

    private string TranslateCrystalFunctions(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return expr;

        expr = Regex.Replace(expr,
            @"\b(Sum|Average|Avg|Maximum|Max|Minimum|Min|Count)\s*\(\s*([^,()]+?)\s*,\s*([^,()]+?)\s*\)",
            m =>
            {
                var func = m.Groups[1].Value.ToUpperInvariant() switch
                {
                    "AVERAGE" => "AVG",
                    "MAXIMUM" => "MAX",
                    "MINIMUM" => "MIN",
                    var x => x
                };
                return $"{func}({m.Groups[2].Value.Trim()}) OVER (PARTITION BY {m.Groups[3].Value.Trim()})";
            },
            RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"=\s*true\b",  "= 1", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"=\s*false\b", "= 0", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bDate\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS date)", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bCurrentDate\b", "CAST(GETDATE() AS date)", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bCurrentDateTime\b", "GETDATE()", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bCurrentTime\b", "CAST(GETDATE() AS time)", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bYear\s*\(", "YEAR(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bMonth\s*\(", "MONTH(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bDay\s*\(", "DAY(", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bDayOfWeek\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(WEEKDAY, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bWeekDay\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(WEEKDAY, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bDayOfYear\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(DAYOFYEAR, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bToText\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS varchar(255))", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bCStr\s*\(\s*([^,)]+?)\s*(?:,[^)]*)?\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS varchar(255))", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bToNumber\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS float)", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bval\s*\(\s*([^)]+?)\s*\)",
            m => $"TRY_CAST({m.Groups[1].Value.Trim()} AS float)", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bmid\s*\(", "SUBSTRING(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bleft\s*\(", "LEFT(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bright\s*\(", "RIGHT(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\blen\s*\(", "LEN(", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bUpperCase\s*\(", "UPPER(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bLowerCase\s*\(", "LOWER(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bTrim\s*\(", "LTRIM(RTRIM(", RegexOptions.IgnoreCase);

        expr = Regex.Replace(expr, @"\bIsNull\s*\(\s*([^)]+?)\s*\)",
            m => $"({m.Groups[1].Value.Trim()} IS NULL)", RegexOptions.IgnoreCase);

        return expr;
    }

    private List<SummaryFieldInfo> ParseSummaryFields(XElement report)
    {
        var list = new List<SummaryFieldInfo>();
        foreach (var s in report.Descendants("SummaryFieldDefinition"))
        {
            var formula = (string?)s.Attribute("FormulaName") ?? "";
            var op = (string?)s.Attribute("Operation") ?? "Sum";
            var m = Regex.Match(formula, @"^\w+\s*\(\s*(\{[^}]+\})\s*,\s*(\{[^}]+\})\s*\)$");
            if (!m.Success) continue;

            var field = ParseCrystalFieldRef(m.Groups[1].Value);
            var groupFieldRef = ParseCrystalFieldRef(m.Groups[2].Value);
            var fieldSql = field.HasValue
                ? $"{field.Value.Table}.{field.Value.Field}"
                : m.Groups[1].Value.Trim('{', '}');           // e.g. "@Amount"
            var groupSql = groupFieldRef.HasValue
                ? $"{groupFieldRef.Value.Table}.{groupFieldRef.Value.Field}"
                : m.Groups[2].Value.Trim('{', '}');           // e.g. "@PhaseDesc"
            list.Add(new SummaryFieldInfo(op, fieldSql, groupSql));
        }
        return list;
    }

    private string ConvertRecordSelectionFormula(XElement report, Dictionary<string, string> formulaFields)
    {
        var formula = (string?)report.Element("DataDefinition")?.Element("RecordSelectionFormula") ?? "";
        formula = formula.Trim();
        if (string.IsNullOrWhiteSpace(formula)) return "";

        formula = string.Join('\n', formula.Split('\n')
            .Select(l => Regex.Replace(l, @"//.*$", "").TrimEnd())
            .Where(l => l.Trim().Length > 0));
        if (string.IsNullOrWhiteSpace(formula)) return "";

        formula = Regex.Replace(formula, @"\{@([^}]+)\}", m =>
        {
            var key = "@" + m.Groups[1].Value.Trim();
            return formulaFields.TryGetValue(key, out var sql) ? $"({sql})" : m.Value;
        });

        formula = Regex.Replace(formula, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" [{m.Groups[1].Value}].{m.Groups[2].Value.Trim()} ");

        formula = Regex.Replace(formula, @"\{\?([^}]+)\}", m => "@" + SanitizeParamName(m.Groups[1].Value));

        formula = Regex.Replace(formula, "\"([^\"]*)\"", m => "'" + m.Groups[1].Value.Replace("'", "''") + "'");

        formula = TranslateLikeWildcards(formula);

        formula = TranslateCrystalFunctions(formula);

        formula = Regex.Replace(formula, @"\bAND\b", "AND", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"\bOR\b", "OR", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"\bNOT\b", "NOT", RegexOptions.IgnoreCase);

        formula = Regex.Replace(formula, @"\bNOT\s*\(\s*((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)\s*\)",
            m => $"(ISNULL({m.Groups[1].Value}, 0) = 0)");
        formula = Regex.Replace(formula, @"\bNOT\s+((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)(?=\s|$|\))",
            m => $"ISNULL({m.Groups[1].Value}, 0) = 0");

        formula = Regex.Replace(formula,
            @"(\b(?:AND|OR)\s+)((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)(\s*)(?=\)|\bAND\b|\bOR\b|$)",
            m => $"{m.Groups[1].Value}(ISNULL({m.Groups[2].Value}, 0) = 1){m.Groups[3].Value}");

        formula = Regex.Replace(formula, @"((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)\s*=\s*''",
            m => $"({m.Groups[1].Value} = '' OR {m.Groups[1].Value} IS NULL)");

        formula = TranslateTopLevelIfThenElse(formula);

        return formula.Trim();
    }

    private static string TranslateTopLevelIfThenElse(string formula)
    {
        var m = Regex.Match(formula,
            @"^\s*IF\s+(?<cond>.+?)\s+THEN\s+(?<thenP>.+?)\s+ELSE\s+(?<elseP>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return formula;

        var cond  = m.Groups["cond"].Value.Trim();
        var thenP = m.Groups["thenP"].Value.Trim();
        var elseP = TranslateTopLevelIfThenElse(m.Groups["elseP"].Value.Trim());
        return $"(({cond}) AND ({thenP})) OR (NOT ({cond}) AND ({elseP}))";
    }

    private List<ReportColumn> BuildColumns(
        List<DetailFieldInfo> detailFields,
        List<PageHeaderLabel> headerLabels,
        List<TableInfo> tables,
        Dictionary<string, string> formulaFields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        var columns = new List<ReportColumn>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var df in detailFields)
        {
            string alias;
            string header;
            ReportColumnType colType;

            if (df.IsFormula)
            {
                var formulaKey = df.Field;  // e.g. "@Amount"
                var resolved = ResolveFormulaToField(formulaKey, formulaFields);
                alias = resolved.HasValue
                    ? $"{resolved.Value.Table}_{resolved.Value.Field}"
                    : FormulaColumnAlias(formulaKey);
                header = MatchHeaderLabel(headerLabels, df, usedLabels);
                if (string.IsNullOrWhiteSpace(header))
                    header = formulaKey.TrimStart('@');
                colType = InferColumnType(formulaKey: formulaKey, headerText: header, fieldName: df.Field);
            }
            else
            {
                alias = $"{df.Table}_{df.Field}";
                header = MatchHeaderLabel(headerLabels, df, usedLabels);
                if (string.IsNullOrWhiteSpace(header))
                    header = df.Field;
                colType = InferColumnType(formulaKey: null, headerText: header, fieldName: df.Field);
            }

            if (!string.IsNullOrEmpty(header)) usedLabels.Add(header);

            if (columns.Any(c => string.Equals(c.Field, alias, StringComparison.OrdinalIgnoreCase)))
                continue;

            columns.Add(new ReportColumn
            {
                Field = alias,
                HeaderText = header,
                Width = TwipsToPx(df.Width),
                Alignment = MapAlignment(df.Alignment),
                ColumnType = colType,
                Format = colType == ReportColumnType.Currency ? "C2"
                       : colType == ReportColumnType.Decimal  ? "N2"
                       : "",
                TextColor = df.TextColor,
                Bold = df.Bold,
                ConditionalStyle = ToReportConditionalStyle(df.ConditionalStyle),
                LayoutLeft = df.Left
            });
        }
        return AddSubreportObjectColumns(columns, detailFields, subreportObjects);
    }

    private static string MatchHeaderLabel(List<PageHeaderLabel> labels, DetailFieldInfo df,
        HashSet<string>? usedLabels = null)
    {
        bool NotUsed(PageHeaderLabel l) =>
            usedLabels == null || !usedLabels.Contains(l.Text);
        bool SemanticOk(PageHeaderLabel l) =>
            !(IsMoneyLabel(l.Text) && IsCodeLikeField(df));

        if (!string.IsNullOrEmpty(df.Name))
        {
            var bound = labels.FirstOrDefault(l =>
                NotUsed(l) && SemanticOk(l) &&
                string.Equals(l.FieldObjectName, df.Name, StringComparison.OrdinalIgnoreCase) &&
                l.Left + l.Width >= df.Left && l.Left <= df.Left + df.Width);
            if (bound != null) return bound.Text;
        }

        var positional = labels
            .Where(l => NotUsed(l) && SemanticOk(l) &&
                        l.Left + l.Width >= df.Left && l.Left <= df.Left + df.Width)
            .OrderBy(l => Math.Abs(l.Left - df.Left))
            .FirstOrDefault();
        return positional?.Text ?? "";
    }

    private static ReportAlignment MapAlignment(string alignment) => alignment switch
    {
        "RightAlign" => ReportAlignment.Right,
        "HorizontalCenterAlign" or "CenterAlign" => ReportAlignment.Center,
        _ => ReportAlignment.Left
    };

    private List<string> BuildSelectFields(List<ReportColumn> columns, List<GroupInfo> groups, Dictionary<string, string> formulaFields)
    {
        var selects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            if (col.IsSubreportObject)
                continue;

            if (col.Field.StartsWith("Formula_", StringComparison.OrdinalIgnoreCase))
            {
                var needle = col.Field.Substring("Formula_".Length);
                var match = formulaFields.FirstOrDefault(kv =>
                    string.Equals(kv.Key.TrimStart('@').Replace(" ", ""), needle,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Value) && seen.Add(col.Field))
                {
                    var inlined = InlineFormulaReferences(match.Value, formulaFields);
                    selects.Add($"({inlined}) AS [{col.Field}]");
                }
                continue;
            }
            if (seen.Add(col.Field))
                selects.Add(col.Field);
        }

        foreach (var g in groups)
        {
            if (g.FormulaName != null)
            {
                if (formulaFields.TryGetValue(g.FormulaName, out var sql))
                {
                    var alias = FormulaGroupAlias(g.FormulaName);
                    var computed = $"({sql}) AS [{alias}]";
                    if (seen.Add(alias)) selects.Add(computed);
                }
            }
            else
            {
                var alias = $"{g.Table}_{g.Field}";
                if (seen.Add(alias)) selects.Add(alias);
            }
            AppendDescriptionSelect(g, seen, selects);
        }
        return selects;
    }

    private static void AppendConditionalStyleSelects(List<string> selectFields, IEnumerable<ReportColumn> columns)
    {
        var seen = new HashSet<string>(selectFields, StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            var field = col.ConditionalStyle?.Field;
            if (string.IsNullOrWhiteSpace(field)) continue;
            if (seen.Add(field))
                selectFields.Add(field);
        }
    }

    private static void AppendSubreportLinkSelects(
        List<string> selectFields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        var seen = new HashSet<string>(selectFields, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in subreportObjects
            .SelectMany(s => s.ParameterLinks)
            .Select(link => link.MainReportAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias)))
        {
            if (seen.Add(alias))
                selectFields.Add(alias);
        }
    }

    private static void AppendDescriptionSelect(GroupInfo g, HashSet<string> seen, List<string> selects)
    {
        if (string.IsNullOrEmpty(g.DescriptionTable) || string.IsNullOrEmpty(g.DescriptionField)) return;
        var groupAlias = g.FormulaName != null
            ? FormulaGroupAlias(g.FormulaName)
            : $"{g.Table}_{g.Field}";
        var descAlias = $"{groupAlias}_Desc";
        if (!seen.Add(descAlias)) return;
        selects.Add($"[{g.DescriptionTable}].{g.DescriptionField} AS [{descAlias}]");
    }

    private string BuildOrderByClause(List<SortFieldInfo> sortFields, List<GroupInfo> groups, Dictionary<string, string> formulaFields)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            if (g.FormulaName != null)
            {
                if (formulaFields.TryGetValue(g.FormulaName, out var sql))
                {
                    var key = $"({sql})";
                    if (seen.Add(key)) parts.Add(key);
                }
            }
            else
            {
                var key = $"[{g.Table}].{g.Field}";
                if (seen.Add(key)) parts.Add(key);
            }
        }
        foreach (var s in sortFields.Where(s => s.SortType == "RecordSortField"))
        {
            if (s.FormulaName != null)
            {
                if (formulaFields.TryGetValue(s.FormulaName, out var sql))
                {
                    var key = $"({sql})" + (s.Direction == "DescendingOrder" ? " DESC" : "");
                    if (seen.Add($"({sql})")) parts.Add(key);
                }
            }
            else
            {
                var key = $"[{s.Table}].{s.Field}" + (s.Direction == "DescendingOrder" ? " DESC" : "");
                if (seen.Add($"[{s.Table}].{s.Field}")) parts.Add(key);
            }
        }

        return parts.Count > 0 ? " ORDER BY " + string.Join(", ", parts) : "";
    }

    private static string FormulaGroupAlias(string formulaName)
        => "Formula_" + formulaName.TrimStart('@').Replace(" ", "");

    private string BuildDrillDownWhere(
        IReadOnlyList<DrillDownFilter> path,
        List<GroupInfo> groups,
        Dictionary<string, string> formulaFields)
    {
        var parts = new List<string>();
        foreach (var f in path)
        {
            string? lhs = null;

            if (f.Field.StartsWith("Formula_", StringComparison.OrdinalIgnoreCase))
            {
                var needle = f.Field.Substring("Formula_".Length);
                var group = groups.FirstOrDefault(g =>
                    g.FormulaName != null
                    && string.Equals(g.FormulaName.TrimStart('@').Replace(" ", ""), needle, StringComparison.OrdinalIgnoreCase));
                if (group?.FormulaName != null
                    && formulaFields.TryGetValue(group.FormulaName, out var formulaSql))
                {
                    var inlined = InlineFormulaReferences(formulaSql, formulaFields);
                    lhs = $"LTRIM(RTRIM({inlined}))";
                }
            }
            else
            {
                var group = groups.FirstOrDefault(g =>
                    g.FormulaName == null
                    && string.Equals($"{g.Table}_{g.Field}", f.Field, StringComparison.OrdinalIgnoreCase));
                if (group != null)
                    lhs = $"{group.Table}.{group.Field}";
            }

            if (lhs == null) continue;

            if (string.IsNullOrEmpty(f.Value))
                parts.Add($"({lhs} IS NULL OR {lhs} = '')");
            else
                parts.Add($"{lhs} = {FormatLiteral(f.Value)}");
        }
        return string.Join(" AND ", parts);
    }

    private static string BuildFieldFilterWhere(IReadOnlyList<ReportFieldFilter> filters)
    {
        var parts = new List<string>();
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Table) || string.IsNullOrWhiteSpace(filter.Field))
                continue;

            var lhs = $"[{filter.Table}].{filter.Field}";
            if (string.IsNullOrEmpty(filter.Value))
                parts.Add($"({lhs} IS NULL OR {lhs} = '')");
            else
                parts.Add($"{lhs} = {FormatLiteral(filter.Value)}");
        }
        return string.Join(" AND ", parts);
    }

    private static string FormatLiteral(string value)
    {
        if (decimal.TryParse(value, out _)) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private string BuildSql(
        List<TableInfo> tables,
        List<TableLinkInfo> links,
        string whereSql,
        string orderByClause,
        List<string> selectFields,
        bool useDistinct = false)
    {
        if (tables.Count == 0)
            throw new InvalidDataException("Crystal XML contains no tables — cannot build SQL.");

        var sb = new StringBuilder();

        sb.Append(useDistinct ? "SELECT DISTINCT " : "SELECT ");
        if (selectFields.Count == 0)
        {
            sb.Append("*");
        }
        else
        {
            var tableAliases = tables
                .Select(t => t.Alias)
                .OrderByDescending(a => a.Length)
                .ToList();

            var selects = selectFields.Select(item =>
            {
                if (item.Contains(" AS [", StringComparison.OrdinalIgnoreCase))
                    return item;

                foreach (var tbl in tableAliases)
                {
                    var prefix = tbl + "_";
                    if (item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var fld = item.Substring(prefix.Length);
                        return $"[{tbl}].{fld} AS [{item}]";
                    }
                }

                var underscore = item.IndexOf('_');
                if (underscore < 0) return item;
                var fallbackTbl = item.Substring(0, underscore);
                var fallbackFld = item.Substring(underscore + 1);
                return $"[{fallbackTbl}].{fallbackFld} AS [{item}]";
            });
            sb.Append(string.Join(", ", selects));
        }

        var firstTable = tables[0];
        sb.Append($" FROM {RenderTableSource(firstTable)}");

        var joinedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { firstTable.Alias };
        var remaining = tables.Skip(1).ToList();

        while (remaining.Count > 0)
        {
            TableInfo? pick = null;
            TableLinkInfo? pickLink = null;

            foreach (var t in remaining)
            {
                var candidate = FindLinkFor(links, t.Alias, joinedAliases);
                if (candidate != null)
                {
                    pick = t;
                    pickLink = candidate;
                    break;
                }
            }

            if (pick == null)
            {
                foreach (var t in remaining)
                {
                    sb.Append($" CROSS JOIN {RenderTableSource(t)}");
                    joinedAliases.Add(t.Alias);
                }
                remaining.Clear();
                break;
            }

            var joinKeyword = pickLink!.JoinType switch
            {
                "LeftOuter" => "LEFT JOIN",
                "RightOuter" => "RIGHT JOIN",
                "FullOuter" => "FULL OUTER JOIN",
                _ => "INNER JOIN"
            };
            sb.Append($" {joinKeyword} {RenderTableSource(pick)} ON ");
            var onParts = new List<string>();
            for (int k = 0; k < Math.Min(pickLink.Source.Count, pickLink.Destination.Count); k++)
            {
                var s = pickLink.Source[k];
                var d = pickLink.Destination[k];
                onParts.Add($"[{s.Table}].{s.Field} = [{d.Table}].{d.Field}");
            }
            sb.Append(string.Join(" AND ", onParts));

            joinedAliases.Add(pick.Alias);
            remaining.Remove(pick);
        }

        if (!string.IsNullOrWhiteSpace(whereSql))
            sb.Append(" WHERE ").Append(whereSql);

        sb.Append(orderByClause);

        return sb.ToString();
    }

    private static TableLinkInfo? FindLinkFor(List<TableLinkInfo> links, string alias, HashSet<string> joined)
    {
        foreach (var link in links)
        {
            var srcAliases = link.Source.Select(f => f.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dstAliases = link.Destination.Select(f => f.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (dstAliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                srcAliases.Any(a => joined.Contains(a)))
                return link;

            if (srcAliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                dstAliases.Any(a => joined.Contains(a)))
                return new TableLinkInfo(link.JoinType, link.Destination, link.Source);
        }
        return null;
    }

    private List<ReportGroup> BuildReportGroups(List<GroupInfo> groups, List<SummaryFieldInfo> summaries)
    {
        var result = new List<ReportGroup>();
        var defaultAggregates = new List<(string Field, ReportAggregateType Op)>();
        var seenAggKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in summaries)
        {
            var key = s.Operation + "::" + s.Field;
            if (seenAggKeys.Add(key))
                defaultAggregates.Add((s.Field, MapAggregateType(s.Operation)));
        }

        foreach (var g in groups)
        {
            var alias = g.FormulaName != null ? FormulaGroupAlias(g.FormulaName) : $"{g.Table}_{g.Field}";
            var hasDescription = !string.IsNullOrEmpty(g.DescriptionField);
            var rg = new ReportGroup
            {
                Field = alias,
                ShowHeader = true,
                ShowFooter = false,
                HeaderFormat = hasDescription ? "{Value}  {Description}" : "{Value}",
                DescriptionField = hasDescription ? $"{alias}_Desc" : "",
                PageBreakBefore = g.PageBreakBefore
            };

            var groupKey = g.FormulaName != null
                ? g.FormulaName                                 // "@PhaseDesc"
                : $"{g.Table}.{g.Field}";                       // "tblFoo.Bar"

            foreach (var s in summaries.Where(s =>
                string.Equals(s.GroupField, groupKey, StringComparison.OrdinalIgnoreCase)))
            {
                rg.Aggregates.Add(new ReportAggregate
                {
                    Field = MapAggregateField(s.Field),
                    AggregateType = MapAggregateType(s.Operation),
                    Format = "C2",                  // currency — matches Crystal's @Amount
                    Label = s.Operation + ":"
                });
            }

            if (rg.Aggregates.Count == 0 && defaultAggregates.Count > 0)
            {
                foreach (var d in defaultAggregates)
                {
                    rg.Aggregates.Add(new ReportAggregate
                    {
                        Field = MapAggregateField(d.Field),
                        AggregateType = d.Op,
                        Format = "C2",
                        Label = d.Op + ":"
                    });
                }
            }

            rg.ShowFooter = rg.Aggregates.Count > 0;

            result.Add(rg);
        }
        return result;
    }

    private static string MapAggregateField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.StartsWith("@", StringComparison.Ordinal))
            return "Formula_" + s.TrimStart('@').Replace(" ", "");
        return s.Replace('.', '_');
    }

    private static ReportAggregateType MapAggregateType(string op) => op switch
    {
        "Sum" => ReportAggregateType.Sum,
        "Count" => ReportAggregateType.Count,
        "Average" => ReportAggregateType.Average,
        "Minimum" => ReportAggregateType.Min,
        "Maximum" => ReportAggregateType.Max,
        _ => ReportAggregateType.Sum
    };

    private static int ParseInt(XAttribute? attr)
    {
        if (attr == null) return 0;
        return int.TryParse(attr.Value, out var v) ? v : 0;
    }

    private string QualifyTable(string tableName)
        => string.IsNullOrWhiteSpace(_options.SchemaPrefix)
            ? tableName
            : $"{_options.SchemaPrefix}.{tableName}";

    private string RenderTableSource(TableInfo t)
    {
        if (!string.IsNullOrWhiteSpace(t.CommandSql))
            return $"({t.CommandSql}) AS [{t.Alias}]";

        var qualified = QualifyTable(t.Name);
        return string.Equals(t.Alias, t.Name, StringComparison.Ordinal)
            ? qualified
            : $"{qualified} AS [{t.Alias}]";
    }
}
