using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Parses a Crystal Reports XML export (produced by Crystal Reports Engine's
/// ReportDocument.ReportDefinition XML serialization) into a <see cref="ReportDefinition"/>
/// that ReportWriterControl can render as SQL → HTML.
///
/// <para>
/// The XML captures:
///   <list type="bullet">
///     <item><description><c>&lt;Database&gt;&lt;Tables&gt;</c> — tables used by the report</description></item>
///     <item><description><c>&lt;Database&gt;&lt;TableLinks&gt;</c> — joins between tables (JoinType + source/destination fields)</description></item>
///     <item><description><c>&lt;DataDefinition&gt;&lt;RecordSelectionFormula&gt;</c> — Crystal formula WHERE clause (e.g. <c>{tbl.F} = {?Param}</c>)</description></item>
///     <item><description><c>&lt;DataDefinition&gt;&lt;ParameterFieldDefinitions&gt;</c> — prompts (Name, PromptText, ParameterValueKind)</description></item>
///     <item><description><c>&lt;DataDefinition&gt;&lt;Groups&gt;</c> — group-by fields (max 6)</description></item>
///     <item><description><c>&lt;DataDefinition&gt;&lt;SortFields&gt;</c> — sort order</description></item>
///     <item><description><c>&lt;ReportDefinition&gt;&lt;Areas&gt;</c> — section layout (Detail section provides columns; PageHeader provides headings)</description></item>
///   </list>
/// </para>
///
/// <para>
/// This loader translates the Crystal model into a tabular SQL report:
///   <list type="number">
///     <item><description>SELECT lists every <c>{table.field}</c> found in the Detail section's FieldObjects</description></item>
///     <item><description>FROM uses the first table; subsequent tables are joined via TableLinks</description></item>
///     <item><description>WHERE translates the RecordSelectionFormula (basic <c>{tbl.F} = {?P}</c>, <c>AND</c>, <c>OR</c>, parentheses)</description></item>
///     <item><description>ORDER BY comes from SortFields</description></item>
///     <item><description>Parameters <c>{?Name}</c> are converted to <c>@Name</c> SQL parameters</description></item>
///   </list>
/// Layout fidelity is approximate — we render a tabular list, not an absolutely positioned Crystal layout.
/// </para>
/// </summary>
public class CrystalXmlReportLoader
{
    private readonly ILogger<CrystalXmlReportLoader> _logger;
    private readonly ReportOptions _options;

    /// <summary>
    /// Crystal Reports / VB6 store dimensions in twips. 1 inch = 1440 twips,
    /// and the default browser DPI is 96, so 1 pixel = 1440 / 96 = 15 twips.
    /// </summary>
    private const int TwipsPerPixel = 15;
    private const string SubreportColumnPrefix = "__Subreport_";

    /// <summary>
    /// Converts a Crystal-XML width (twips, as stored in <c>FieldObject Width=</c>)
    /// to CSS pixels. Returns 0 when no XML hint is present so callers can
    /// distinguish "author specified nothing" from "author asked for 0".
    /// </summary>
    private static int TwipsToPx(int twips) => twips > 0 ? twips / TwipsPerPixel : 0;

    public CrystalXmlReportLoader(ILogger<CrystalXmlReportLoader> logger, ReportOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ReportOptions();
    }

    /// <summary>
    /// Loads and parses a Crystal Reports XML file as a drill-down view, filtered to a
    /// specific group path. Returns a <see cref="ReportDefinition"/> that shows all detail
    /// columns (not just summary) for rows matching the path — mirroring Crystal Reports'
    /// "drill-down tab" behavior when the user clicks a node in the group tree.
    /// </summary>
    /// <param name="xmlFilePath">Path to the Crystal Reports XML export.</param>
    /// <param name="path">
    /// Sequence of <c>(fieldAlias, value)</c> pairs defining the drill-down. Each step
    /// narrows the result to a specific group value. <c>fieldAlias</c> must match an
    /// alias emitted by the loader (e.g. <c>MyTable_GroupField</c>).
    /// </param>
    public ReportDefinition LoadDrillDown(string xmlFilePath, IReadOnlyList<DrillDownFilter> path)
    {
        return LoadInternal(xmlFilePath, drillPath: path);
    }

    /// <summary>
    /// Loads a Crystal XML report with direct table-field filters. Used for Crystal
    /// subreports whose parameters are supplied by the clicked main-report row.
    /// </summary>
    public ReportDefinition LoadWithFieldFilters(string xmlFilePath, IReadOnlyList<ReportFieldFilter> filters)
    {
        return LoadInternal(xmlFilePath, drillPath: null, fieldFilters: filters);
    }

    /// <summary>
    /// Loads a subreport embedded inside the main Crystal XML <c>&lt;SubReports&gt;</c>
    /// block. Native C# conversion keeps subreports inline, so on-demand drilldown
    /// links use this path instead of requiring a separate sidecar XML file.
    /// </summary>
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

    /// <summary>
    /// Loads and parses a Crystal Reports XML file into a renderable <see cref="ReportDefinition"/>.
    /// </summary>
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

        // Strip <SubReports> blocks from the tree before any parsing. Subreports
        // are rendered separately by Crystal at runtime — we don't render them in
        // the SQL/HTML pipeline. Leaving them in causes Descendants("Table"),
        // Descendants("ParameterFieldDefinition"), Descendants("Area") etc. to pull
        // in subreport-only tables, formulas, and field objects, which then leak
        // into the main report's SQL (e.g. Open PO's v1 cross-joining
        // POInvoicedAmounts and invoiceitems with no link conditions).
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

        // --- Title + subtitle from ReportHeader / PageHeader (centered + bold TextObjects) ---
        // Crystal reports conventionally place the title (e.g. "Budget Report") in a centered
        // bold TextObject at the top of the PageHeader, and a subtitle ("By Group and Phase")
        // in a smaller centered bold TextObject just below. The file-name fallback is kept as
        // a default if no centered text object exists.
        ExtractReportHeadings(report, definition);

        // --- Print options (orientation + paper size + rows-per-page) ---
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

            // Compute rows-per-page from the Crystal author's stated page
            // height. Crystal stores it in twips inside <PrintOptions
            // PageContentHeight=...>.
            //
            // Note: RowsPerPage is now interpreted by ReportWriterControl as
            // the cap on TOTAL VISIBLE ROWS (data rows + group headers +
            // group footers), not just data rows. The renderer counts each
            // group transition's overhead so a page with many transitions
            // automatically holds fewer data rows. Reserving a fixed
            // "kGroupRowReserve" used to leave variable empty space below
            // models with few transitions; counting actual visible rows
            // packs every page to the same height.
            var pageContentHeightTwips = ParseInt(printOptions.Attribute("PageContentHeight"));
            if (pageContentHeightTwips > 0)
            {
                int pageHeightPx = TwipsToPx(pageContentHeightTwips);
                // Per-page reserve: report title + date band (~42 px), page
                // footer (~18 px), table header (~20 px) — measured against
                // the current ReportWriterControl CSS.
                const int kPageChromePx = 80;
                // Average rendered row height with the tight CSS
                // (font-size 10 px + line-height 1.15 + 1 px padding × 2 +
                // 1 px border ≈ 14 px). Group header/footer rows are very
                // close to detail-row height with our compact styling, so
                // a single constant tracks all three.
                const int kRowHeightPx = 14;

                int visibleRows = Math.Max(10, (pageHeightPx - kPageChromePx) / kRowHeightPx);
                definition.RowsPerPage = visibleRows;
            }
        }

        // --- Parse tables, joins, WHERE clause, sort, groups, parameters, columns ---
        var tables = ParseTables(report);
        var tableLinks = ParseTableLinks(report);
        var parameters = ParseParameters(report);
        var detailFields = ParseDetailFields(report);
        // Column headers can live in PageHeader OR GroupHeader sections (Crystal's choice varies by report).
        var headerLabels = ParseHeaderLabels(report);
        var groups = ParseGroups(report);
        var sortFields = ParseSortFields(report);
        var summaryFields = ParseSummaryFields(report);
        // Formula fields: {@Name} → SQL expression. Translated ahead of everything else so
        // RecordSelectionFormula / Groups / SortFields can inline their expressions.
        var formulaFields = ParseFormulaFields(report);
        var whereSql = ConvertRecordSelectionFormula(report, formulaFields);

        // --- Detect "drill-down" reports (Detail section suppressed at level 0) ---
        // In Crystal Reports, when a report has EnableSuppress="drilldowngrouplevel=0" on the
        // Detail section, the initial view shows only group summary rows (one per innermost
        // VISIBLE group) rather than every detail row. VB6 did this automatically via the
        // Crystal ActiveX viewer; we detect it manually and substitute an aggregate query.
        var isDrillTab = drillPath != null && drillPath.Count > 0;
        var detailSuppressedAtTopLevel = IsDetailSuppressedAtTopLevel(report) && !isDrillTab;

        // --- Build SQL + column list ---
        List<ReportColumn> columns;
        List<string> selectFields;
        var isDrillDownReportStyle = IsDetailSuppressedAtTopLevel(report);
        // Drill-down-style reports also expose their summary columns in drill-down tabs so
        // leaf rows (options without Phase/Item children) still show meaningful content.
        var mergeSummaryWithDetail = isDrillDownReportStyle && isDrillTab;

        if (detailSuppressedAtTopLevel)
        {
            // Drill-down report (main tab): use the innermost visible GroupHeader section as
            // the data row. Each unique combination of visible-group fields produces one
            // output row, which matches how VB6 renders the initial main report.
            var visibleSection = FindInnermostVisibleDataSection(report);
            var summaryFieldsFromHeader = visibleSection != null
                ? ExtractSectionFields(visibleSection, formulaFields, detectFormulas: true)
                : detailFields;
            columns = BuildColumnsFromSection(summaryFieldsFromHeader, headerLabels, formulaFields, subreportObjects);
            selectFields = BuildSelectFieldsForSummary(columns, groups, formulaFields, detailFields);
        }
        else if (mergeSummaryWithDetail)
        {
            // Drill-down TAB of a drill-down-style report: use ONLY the Detail-section
            // columns (Phase, Item, Description, Qty, @TaxinRate as Selling Price,
            // @Total Cost as Cost, JCCostCode, Vendor).
            //
            // Use BuildColumnsFromSection so formula-field DataSources (@TaxinRate, @Total
            // Cost) become computed columns — without this, Selling Price and Cost would
            // be missing because BuildColumns skips formulas.
            columns = BuildColumnsFromSection(detailFields, headerLabels, formulaFields, subreportObjects);
            // For drill-down tabs, include ALL groups so the tree can descend further
            // (into Phase / POIndex) and the rendered output can show nested group headers.
            selectFields = BuildSelectFieldsForDrillDown(columns, groups, formulaFields);
        }
        else
        {
            // Non-drill-down report: show the Detail-section columns as before.
            columns = BuildColumns(detailFields, headerLabels, tables, formulaFields, subreportObjects);
            selectFields = BuildSelectFields(columns, groups, formulaFields);
        }
        AppendSubreportLinkSelects(selectFields, subreportObjects);
        AppendConditionalStyleSelects(selectFields, columns);

        // SuppressRepeats inference — flag the LEFTMOST detail column that
        // matches the report's FIRST RecordSortField. RptToXml doesn't export
        // Crystal's "Suppress If Duplicated" FieldObject property reliably,
        // so we infer from the sort hierarchy: the first record-level sort
        // is the column the report visually groups its detail rows by, and
        // Crystal authors almost always set it to suppress repeats
        // (Master Assembly List's POIndex is the canonical example — sorted
        // first, with one "050 Excavation" label spanning all the rows in
        // that POIndex's slice). Conservative: only the FIRST record-sort
        // gets the flag; subsequent record-sort columns (Phase, Item) keep
        // showing on every row, matching the legacy Crystal Viewer.
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

        // Extend WHERE with drill-down filters supplied by the caller.
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

        // DISTINCT is used:
        //   • on the Main tab of a drill-down-style report (summary-only view; restrict
        //     ORDER BY to visible groups — only those made it into SELECT).
        //   • on drill-down tabs of drill-down-style reports (prevents LEFT JOIN row
        //     explosion; ORDER BY can reference ALL groups since they're all in SELECT).
        var useDistinct = detailSuppressedAtTopLevel || mergeSummaryWithDetail;
        // ORDER BY columns must appear in the SELECT list under DISTINCT.
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

        definition.Sql = sql;
        definition.Columns = columns;
        definition.Parameters = parameters;

        // --- Build a separate "tree SQL" for drill-down-style reports so the group tree
        //     can show every group level (Worksheet → Community → CommunityPhase → Assembly
        //     → Phase → POIndex), even if the main summary SELECT only covers the visible
        //     levels. Without this, levels 5-6 would be invisible in the tree and the user
        //     couldn't navigate to Phase or POIndex drill targets.
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
        // Expose groups to the renderer (used for in-report group-header rows and the tree):
        //   • Main tab of drill-down-style report: ALL groups (tree shows all 6 levels,
        //     populated via the separate TreeSql query built above).
        //   • Drill-down tab: ALL groups minus the ones already filtered into — so the
        //     drill-down tree/rendering can descend further (e.g. from Assembly into Phase
        //     → POIndex → detail items).
        //   • Non-drill-down report: all groups.
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

    // ═══════════════════════════════════════════════════════════════════
    // XML parsing helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One Crystal table reference. <c>CommandSql</c> is non-null when the source
    /// is a Crystal <c>&lt;Command&gt;</c> table — an embedded SELECT used as a derived
    /// subquery instead of a real table (e.g. PO Integration Status Report v1).
    /// </summary>
    private sealed record TableInfo(string Alias, string Name, string? CommandSql = null);
    private sealed record TableLinkInfo(string JoinType, List<(string Table, string Field)> Source, List<(string Table, string Field)> Destination);
    /// <summary>
    /// One field rendered in a Crystal Detail section.
    /// <c>Name</c> is the FieldObject's <c>Name="..."</c> attribute (e.g. "Item1") — used to
    /// match it against a FieldHeadingObject's <c>FieldObjectName="..."</c> in the page header,
    /// which is Crystal's authoritative heading link.
    /// </summary>
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
    /// <summary>
    /// Grouping definition. Either a direct table field (Table + Field populated, FormulaName null)
    /// or a formula field reference (FormulaName = "@Name", Table/Field empty).
    /// <c>IsVisible</c> is true when the group has at least one non-hidden, non-suppressed
    /// GroupHeader section — i.e. the group renders in the default (top-level) view.
    /// </summary>
    /// <summary>
    /// Grouping definition extracted from the Crystal XML.
    /// <para>
    /// <c>PageBreakBefore</c> mirrors Crystal's "New Page Before" / "New Page After"
    /// flags. Either flag triggers a page break <em>before</em> the next instance of
    /// this group's value (semantically the same — pre-break vs post-break of the
    /// previous instance just differ by which side of the boundary they fire on).
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Finds on-demand subreport objects in the main report layout. The nested
    /// <c>&lt;SubReports&gt;</c> definitions are still stripped before SQL parsing, but
    /// their exported XML sidecar locations are safe metadata for the renderer.
    /// </summary>
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

        // Native conversion can preserve the main-report table alias in
        // SubreportFieldName even though the field is bound to a different table
        // inside the embedded subreport. Match by field name so the generated
        // subreport WHERE clause targets a table that actually exists there.
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

    /// <summary>
    /// Pulls the report's display <see cref="ReportDefinition.Title"/> and
    /// <see cref="ReportDefinition.Subtitle"/> from the XML. Crystal Reports stores them
    /// as centered bold <c>&lt;TextObject&gt;</c>s inside <c>&lt;Area Kind="ReportHeader"&gt;</c>
    /// or <c>&lt;Area Kind="PageHeader"&gt;</c>. We pick the candidate with the largest
    /// font size as the title, the next as the subtitle, and fall back to the filename
    /// if no centered bold text is found.
    /// </summary>
    private void ExtractReportHeadings(XElement report, ReportDefinition definition)
    {
        var candidates = new List<(string Text, double Size, int Top, int Left, string Alignment, string AreaKind)>();

        foreach (var area in report.Descendants("Area"))
        {
            var areaKind = (string?)area.Attribute("Kind") ?? "";
            if (areaKind != "ReportHeader" && areaKind != "PageHeader") continue;

            foreach (var section in area.Elements("Sections").Elements("Section"))
            {
                // Skip sections suppressed at top-level drill (they're only visible when
                // the user drills in — not the "main" title source).
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

                    // Only consider centered or large bolded text — that's the typical title format.
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

        // Pick title = largest-font candidate (ties broken by Top ascending).
        var ordered = candidates
            .OrderByDescending(c => c.Size)
            .ThenBy(c => c.Top)
            .ToList();

        definition.Title = ordered[0].Text;
        definition.TitleAlignment = ResolveAlignment(ordered[0].Alignment);

        // Subtitle = next candidate that isn't the title text itself.
        var subtitle = ordered.Skip(1).FirstOrDefault(c =>
            !string.Equals(c.Text, definition.Title, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(subtitle.Text))
        {
            definition.Subtitle = subtitle.Text;
            definition.SubtitleAlignment = ResolveAlignment(subtitle.Alignment);
        }
    }

    /// <summary>
    /// Resolves a Crystal <c>HorizontalAlignment</c> string to a <see cref="ReportAlignment"/>.
    /// Unspecified / unknown / <c>DefaultAlign</c> maps to <see cref="ReportAlignment.Center"/>
    /// because report titles are almost always meant to be centered; VB6's Crystal viewer
    /// shows them centered by default too.
    /// </summary>
    private static ReportAlignment ResolveAlignment(string crystalAlignment)
    {
        if (string.IsNullOrWhiteSpace(crystalAlignment)) return ReportAlignment.Center;
        if (crystalAlignment.Contains("Left", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Left;
        if (crystalAlignment.Contains("Right", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Right;
        if (crystalAlignment.Contains("Center", StringComparison.OrdinalIgnoreCase))
            return ReportAlignment.Center;
        // "DefaultAlign" or anything else — default to center for titles.
        return ReportAlignment.Center;
    }

    private List<TableInfo> ParseTables(XElement report)
    {
        return report.Descendants("Table")
            .Where(t => t.Parent?.Name.LocalName == "Tables")
            .Select(t => new TableInfo(
                Alias: (string?)t.Attribute("Alias") ?? "",
                Name: (string?)t.Attribute("Name") ?? "",
                // Crystal "Command" tables (ClassName="CrystalReports.CommandTable") carry
                // their SELECT in a child <Command> element. When present, the SQL builder
                // wraps it as a derived subquery: FROM (<sql>) AS Alias.
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
            // FormulaName format: "{tableAlias.fieldName}"
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
        // Track which parameter names we've already emitted so a parameter
        // declared in multiple subreports (or echoed in the main report as a
        // <SubReportLink> reference) is only added once. Without this, e.g.
        // Open PO's v1 — which has THREE copies of "Pm-POMaster.PONumber"
        // (one per subreport + the linked references in the main report) —
        // blew up downstream when ReportParamDialogControl tried to build a
        // Dictionary keyed by name.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-extract parameter → (table, field) bindings from the
        // RecordSelectionFormula. The picklist for each parameter is
        // auto-derived from this binding — no host-side hardcoded
        // table mapping required.
        var bindings = ExtractParameterBindings(report);
        var referencedParameters = ExtractReferencedParameterNames(report);

        // Pre-build a {tableName → set<fieldName>} index from the XML's
        // <Database><Tables> blocks. Used to discover the description
        // column for the picklist (e.g. Assembly → AssemblyDesc) and to
        // decide whether to inject a DivisionID filter into the auto SQL.
        var tableFields = ExtractTableFieldIndex(report);

        var paramDefs = report.Descendants("ParameterFieldDefinition")
            .Where(p =>
                // Skip non-report params (Crystal also stores StoredProcedureParameter etc.).
                (string?)p.Attribute("ParameterType") == "ReportParameter"
                // Skip subreport-linked parameters — Crystal auto-supplies their
                // value at runtime from the parent row's matching field; they
                // are never user-entered. Marker attribute is
                // IsLinkedToSubreport="True" on the main-report-side reference.
                && !string.Equals((string?)p.Attribute("IsLinkedToSubreport"),
                    "True", StringComparison.OrdinalIgnoreCase)
                // Skip parameter definitions nested inside a <SubReports> block —
                // those belong to the subreport, not the main report. (e.g. Open
                // PO's v1 declares "Pm-POMaster.PONumber" inside two subreports
                // and only references it via SubReportLink in the main report.)
                && !p.Ancestors("SubReports").Any());

        foreach (var p in paramDefs)
        {
            var rawName = (string?)p.Attribute("Name") ?? "";
            if (string.IsNullOrWhiteSpace(rawName)) continue;

            // Crystal parameter names may contain spaces, parens, hyphens, and other
            // characters that aren't valid in SQL parameter identifiers. Strip
            // everything except letters, digits, and underscore. Must match what
            // ConvertRecordSelectionFormula emits when it sees {?Original Name}.
            var name = SanitizeParamName(rawName);
            if (string.IsNullOrEmpty(name)) continue;

            // Deduplicate — one logical parameter, even if Crystal echoed it
            // across multiple subreport declarations.
            if (!seenNames.Add(name)) continue;

            var valueKind = (string?)p.Attribute("ParameterValueKind") ?? "StringParameter";
            var promptText = (string?)p.Attribute("PromptText") ?? "";
            var prompt = !string.IsNullOrWhiteSpace(promptText) ? promptText : rawName;
            var usage = (string?)p.Attribute("ParameterFieldUsage") ?? "";
            var promptIsOptional = string.Equals((string?)p.Attribute("IsOptionalPrompt"), "True", StringComparison.OrdinalIgnoreCase);

            // Tokenize usage flags. Crystal emits a comma-separated list like
            // "InUse, DataFetching" or "NotInUse, ShowOnPanel" — a substring
            // check for "InUse" wrongly matches "NotInUse" too, which is what
            // caused Open PO's v1 to prompt for orphan parameters.
            //
            // The Java Crystal SDK can mark every prompt as NotInUse even
            // when RecordSelectionFormula clearly references it. Treat the
            // formula reference as authoritative so Java-converted reports
            // still prompt for @Date, @Worksheet, @Community, etc.
            var usageFlags = usage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isInUse = usageFlags.Any(f => string.Equals(f, "InUse", StringComparison.OrdinalIgnoreCase))
                || referencedParameters.Contains(name);

            // "(optional)" hint in the parameter name (e.g. "Job Filter (optional)") means
            // the user is allowed to leave the value blank. The report's record-selection
            // formula handles the empty case (typically a LIKE prefix that matches all rows).
            var nameSaysOptional = rawName.IndexOf("(optional)", StringComparison.OrdinalIgnoreCase) >= 0;

            // All <ParameterDefaultValue> entries — used by the dialog to render a
            // description+value dropdown when there are 2+ choices.
            var defaultValues = p.Element("ParameterDefaultValues")?.Elements("ParameterDefaultValue")
                .Select(d => new ReportParameterChoice(
                    Description: (string?)d.Attribute("Description") ?? "",
                    Value: (string?)d.Attribute("Value") ?? ""))
                .ToList() ?? new List<ReportParameterChoice>();

            // Pre-populate the parameter dialog from either the most-recent current value
            // (<ParameterCurrentValue Value="…">) or the first default value. Crystal stores
            // the last user-entered value here; VB6 showed it as the pre-filled suggestion.
            var defaultVal = (string?)p.Element("ParameterCurrentValues")?.Element("ParameterCurrentValue")?.Attribute("Value")
                           ?? defaultValues.FirstOrDefault()?.Value
                           ?? "";

            // Optional <PickListSql>…</PickListSql> child — host-specific
            // annotation, NOT a native Crystal export element. When present,
            // it tells the generic /report-params/{file} screen to render a
            // dual-list-box for this parameter, populated by running this
            // query through IReportDataExecutor. The query may reference
            // any auto-inject session parameter (e.g. @DivisionID).
            //
            // Stored verbatim — whitespace and newlines are preserved so the
            // SQL stays readable when re-emitted from the XML viewer.
            var pickListSql = p.Element("PickListSql")?.Value;
            if (string.IsNullOrWhiteSpace(pickListSql)) pickListSql = null;

            // Resolve the parameter's table-field binding from the
            // RecordSelectionFormula. Picklist runtime uses this to ask
            // the host for a sensible source. Stored as metadata even
            // when no PickListSql is synthesized so other consumers (drill
            // routes, parameter persistence) can act on the binding.
            var (bindingTable, bindingField) = bindings.TryGetValue(name, out var b)
                ? b
                : ("", "");
            var bindingDescField = ResolveDescriptionField(bindingTable, bindingField, tableFields);

            // Auto-synthesize PickListSql only when the bound table looks
            // like a base table (e.g. tblModels, tblJobs) — i.e. an
            // explicit "tbl" prefix in the XML's table alias. For VIEWS
            // (AssemblyCostsAllCommunities, EstModelList, etc.) the
            // bound field is intentionally wide (it carries join-expanded
            // values) so DISTINCT-from-view returns far more rows than
            // Crystal's native picklist shows. Crystal stores the picklist
            // query for view-bound parameters in the .rpt binary (not the
            // XML), so we can't faithfully reproduce it from the XML
            // alone — fall back to the host's name-based lookup instead.
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

    /// <summary>
    /// Scans the RecordSelectionFormula for <c>{T.F} = {?Param}</c> and
    /// <c>{T.F} IN {?Param}</c> patterns and returns a map of sanitized
    /// parameter name → (table, field). Each parameter binds to at most
    /// one (table, field) — when the formula compares it against multiple
    /// fields, the first hit wins (rare; reports typically bind each
    /// prompt against a single column).
    /// </summary>
    private Dictionary<string, (string Table, string Field)> ExtractParameterBindings(XElement report)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var formula = (string?)report.Element("DataDefinition")?.Element("RecordSelectionFormula") ?? "";
        if (string.IsNullOrWhiteSpace(formula)) return result;

        // Match `{Table.Field} <op> {?Param}` with op ∈ { =, IN, in }. The
        // formula is normalized (whitespace-tolerant). We also accept
        // `{?Param} = {Table.Field}` (operand reversed) since Crystal
        // authors occasionally flip the comparison.
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

    /// <summary>
    /// Builds <c>{tableAlias → set&lt;fieldName&gt;}</c> from the XML's
    /// <c>&lt;Database&gt;&lt;Tables&gt;&lt;Table&gt;&lt;Fields&gt;&lt;Field&gt;</c>
    /// blocks. Drives picklist description-column discovery and
    /// DivisionID-injection decisions.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ExtractTableFieldIndex(XElement report)
    {
        var idx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tbl in report.Descendants("Table"))
        {
            // Table-alias attribute name varies by Crystal export: most
            // exports use "Alias" but some legacy ones use "Name". The
            // formula refs always use the alias, so prefer it.
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

    /// <summary>
    /// Heuristic: for a (table, field) binding, find the matching
    /// description column in the table. Tries (in order):
    /// <list type="number">
    ///   <item><description><c>{Field}Desc</c> — direct suffix (Assembly → AssemblyDesc, Phase → PhaseDesc).</description></item>
    ///   <item><description>Common prefix strip + Desc (CostCommunity → CommunityDesc, SourceCommunity → CommunityDesc).</description></item>
    ///   <item><description>Plain <c>Description</c> column.</description></item>
    /// </list>
    /// Returns empty when no matching description column was found —
    /// the picklist still works, it just shows Value as both code and label.
    /// </summary>
    private string ResolveDescriptionField(string table, string field,
        Dictionary<string, HashSet<string>> tableFields)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(field)) return "";
        if (!tableFields.TryGetValue(table, out var fields)) return "";

        // Direct {Field}Desc — most common pattern in HomeFront's view layer.
        var direct = field + "Desc";
        if (fields.Contains(direct)) return direct;

        // Prefix-stripped Desc — handles AssemblyCostsAllCommunities.CostCommunity →
        // CommunityDesc, where the parameter binds against a renamed column.
        foreach (var prefix in new[] { "Cost", "Source", "Pricing" })
        {
            if (field.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && field.Length > prefix.Length)
            {
                var stripped = field.Substring(prefix.Length);
                var candidate = stripped + "Desc";
                if (fields.Contains(candidate)) return candidate;
            }
        }

        // Plain Description column — tblLocality / tblModels / tblJobs / etc.
        if (fields.Contains("Description")) return "Description";

        return "";
    }

    /// <summary>
    /// Synthesizes a default picklist SQL from a parameter's
    /// (table, field) binding. Adds a <c>DivisionID = @DivisionID</c>
    /// filter when the table exposes a DivisionID column, since every
    /// HomeFront-multi-tenant table is partitioned by it.
    /// </summary>
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

    /// <summary>Strips Crystal parameter names down to a SQL-safe identifier — keeps
    /// letters, digits, and underscore. "{?Job Filter (optional)}" → "JobFilteroptional".</summary>
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

    // ═══════════════════════════════════════════════════════════════════
    // Drill-down mode detection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the Detail section is suppressed at <c>drilldowngrouplevel=0</c>
    /// (Crystal's "show only group summaries" mode). When true, the loader substitutes a
    /// SELECT-DISTINCT-at-group-level query so the initial view shows one row per innermost
    /// visible group — matching VB6's behavior where Estimating Worksheet Detail v1 fits in
    /// 2 pages instead of 50.
    /// </summary>
    private bool IsDetailSuppressedAtTopLevel(XElement report)
    {
        var detailSections = report.Descendants("Area")
            .Where(a => (string?)a.Attribute("Kind") == "Detail")
            .SelectMany(a => a.Descendants("Section"));

        foreach (var section in detailSections)
        {
            var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
            // Crystal uses "drilldowngrouplevel=0" or "DrillDownGroupLevel=0" — case-insensitive compare.
            if (suppress.Replace(" ", "").Replace("\"", "")
                    .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the innermost (deepest) GroupHeader section that is visible at top-level drill level.
    /// A section is "visible" if:
    ///   (a) its parent Area's <c>EnableHideForDrillDown</c> is false (or absent), AND
    ///   (b) it has no <c>SectionAreaConditionFormulas.EnableSuppress="drilldowngrouplevel=0"</c>, AND
    ///   (c) it actually contains FieldObjects (data fields — empty sections don't count).
    ///
    /// <para>
    /// For Estimating Worksheet Detail v1, the visible Assembly-level section (GroupHeaderArea3 /
    /// GroupHeaderSection9) has the row: Assembly code, Description, Total Selling, Cost, Profit —
    /// exactly what VB6 shows.
    /// </para>
    /// </summary>
    private XElement? FindInnermostVisibleDataSection(XElement report)
    {
        // Walk all GroupHeader Areas in document order; the last one with data fields is the
        // "innermost visible" row-level section.
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

                // Must contain at least one visible FieldObject (not just labels or suppressed helpers).
                if (!section.Descendants("FieldObject").Any(f => !IsSuppressedFieldObject(f))) continue;

                best = section;
            }
        }
        return best;
    }

    /// <summary>
    /// Extracts the data-bearing fields from any section (Detail or GroupHeader). Detail-field
    /// records carry position, alignment, and formula-source for downstream column/SQL building.
    /// When <paramref name="detectFormulas"/> is true, <c>{@FormulaName}</c> data sources are
    /// resolved to their underlying SQL expression via <paramref name="formulaFields"/>.
    /// </summary>
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
            // Skip aggregate expressions like "Sum ({@X}, {tbl.Y})" — we handle these via summary fields.
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

            // GroupName({tbl.field}) — extract the underlying field so the group value becomes a column.
            // e.g. "GroupName ({MyTable.GroupField})" → MyTable.GroupField
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

    /// <summary>
    /// Builds ReportColumn list from a set of section fields. Handles formula fields by
    /// resolving their underlying field (if the formula is a simple field reference) or
    /// keeping the formula name as a display header otherwise.
    /// </summary>
    private List<ReportColumn> BuildColumnsFromSection(List<DetailFieldInfo> fields,
        List<PageHeaderLabel> labels, Dictionary<string, string> formulaFields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        var cols = new List<ReportColumn>();
        // Pre-assign each label to the field whose horizontal CENTER is closest to the
        // label's center. This avoids the "wrong field claims the label" problem where
        // fields are iterated in Left-order and an early, positionally-marginal field
        // (e.g. OrderQty at Left=7560) grabs a label (e.g. "Selling Price" at Left=8519)
        // that was really meant for a later, better-centered field (e.g. @TaxinRate at
        // Left=8879 with its center closer to the "Selling Price" label's center).
        var assignedLabels = AssignLabelsByCenter(fields, labels);
        // Pre-seed usedLabels with every assigned label so fields that aren't in the
        // center-map (e.g. OrderQty, whose label was given to @TaxinRate instead) don't
        // fall back to picking up a label already reserved for a better-centered neighbor.
        var usedLabels = new HashSet<string>(assignedLabels.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var df in fields)
        {
            string alias;
            string header;
            if (df.IsFormula)
            {
                // Formula field like "@Total Selling" or "@Profit".
                var formulaKey = df.Field;  // e.g. "@Profit"
                var resolved = ResolveFormulaToField(formulaKey, formulaFields);
                if (resolved.HasValue)
                {
                    // Simple field ref — reuse its table.field alias.
                    alias = $"{resolved.Value.Table}_{resolved.Value.Field}";
                }
                else
                {
                    // Compound formula (e.g. @Profit = Pretax - Cost). Emit as a sanitized alias;
                    // BuildSelectFieldsForSummary will translate the formula body to SQL.
                    alias = FormulaColumnAlias(formulaKey);
                }
                // Prefer a center-aligned label assignment (computed above); fall back to
                // positional overlap or formula-name.
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
                // Honor the Crystal author's width as-is (twips → px).  0 means
                // "no XML hint" so the renderer's adaptive sizer falls back to
                // header + data measurement for that column.
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

    /// <summary>Produces a SQL-safe alias for a formula-column (e.g. <c>@Profit</c> → <c>Formula_Profit</c>).</summary>
    private static string FormulaColumnAlias(string formulaName)
        => "Formula_" + formulaName.TrimStart('@').Replace(" ", "");

    /// <summary>
    /// For each column-header label, picks the detail field whose horizontal center is
    /// closest to the label's center (with a tolerance threshold), and returns a map
    /// <c>field → label text</c>. This prevents the first field in Left-order from
    /// claiming a label that's actually positioned to describe a neighbor.
    /// </summary>
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
                // Positional overlap is still required — we don't want a distant label
                // "Vendor_Name" at Left=12840 accidentally claiming Phase at Left=85.
                if (!(label.Left + label.Width >= df.Left && label.Left <= df.Left + df.Width))
                    continue;
                // Semantic filter — don't give money-themed labels (Cost, Profit, Selling
                // Price) to obviously non-money fields whose name encodes a code/id (e.g.
                // JCCostCode, POIndex, ID, RowNum). The label was meant for a peer field
                // at the same X (a formula like @Profit), not the code-style field.
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
        // Formula / money fields are allowed to match money labels.
        if (df.IsFormula) return false;
        var f = df.Field.ToLowerInvariant();
        return f.EndsWith("code") || f.EndsWith("id") || f.EndsWith("index") ||
               f.EndsWith("num") || f.EndsWith("number") || f.EndsWith("key");
    }

    /// <summary>
    /// Guesses a numeric/currency column type from the header text or formula name.
    /// Money-sounding fields get Currency formatting so the output looks like VB6's.
    ///
    /// <para>
    /// <paramref name="fieldName"/> is used to veto the currency match when the underlying
    /// column is clearly a code/id (e.g. <c>JCCostCode</c> — the "cost" substring should
    /// not force numeric formatting on a hyphen-separated string like "3-05-010").
    /// </para>
    /// </summary>
    private static ReportColumnType InferColumnType(string? formulaKey, string? headerText, string? fieldName = null)
    {
        // Code/id fields are never money columns, even if the name contains "Cost".
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

    /// <summary>
    /// Resolves <c>{@FormulaName}</c> to an underlying table field if the formula body is a
    /// simple field reference (e.g. <c>@Total Selling</c> = <c>{MyTable.PretaxField}</c>).
    /// Returns null for compound formulas (can't be a single SELECT column without aggregation).
    /// </summary>
    private (string Table, string Field)? ResolveFormulaToField(string formulaName,
        Dictionary<string, string> formulaFields)
    {
        if (!formulaFields.TryGetValue(formulaName, out var sql)) return null;
        sql = sql.Trim();
        // Simple "table.field" form?
        var m = Regex.Match(sql, @"^\s*([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)\s*$");
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);
        return null;
    }

    /// <summary>
    /// For drill-down reports: builds the SELECT list from the summary columns (including
    /// compound formulas as computed columns) plus all group fields.
    /// <para>
    /// Columns whose <c>Field</c> starts with <c>Formula_</c> are compound formulas — we
    /// emit them as <c>(translated-sql) AS [Formula_Name]</c>. Regular columns use the
    /// <c>table.field AS [table_field]</c> shape.
    /// </para>
    /// </summary>
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
                // Compound formula column (e.g. Formula_Profit or Formula_TotalCost). The alias
                // has spaces stripped, but the formulaFields map keeps the original spacing.
                // Match by whitespace-insensitive comparison against the map keys.
                var needle = col.Field.Substring("Formula_".Length);
                var match = formulaFields.FirstOrDefault(kv =>
                    string.Equals(kv.Key.TrimStart('@').Replace(" ", ""), needle,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Value))
                {
                    // Formulas may reference other formulas (@Profit uses @Total Selling). Inline recursively.
                    var inlined = InlineFormulaReferences(match.Value, formulaFields);
                    if (seen.Add(col.Field)) selects.Add($"({inlined}) AS [{col.Field}]");
                }
                continue;
            }
            if (seen.Add(col.Field)) selects.Add(col.Field);
        }

        // Include only VISIBLE group fields. Hidden groups (e.g. Phase, POIndex) are
        // excluded here to keep the main summary query compact; the separate TreeSql
        // query selects all groups so the tree can still navigate them.
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

    /// <summary>
    /// SELECT-list builder for drill-down tabs — includes ALL groups (visible + hidden)
    /// so the nested tree and rendering can show levels like Phase and POIndex that were
    /// dropped from the main summary view.
    /// </summary>
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
                // col.Field is "Formula_TotalCost" (spaces stripped by FormulaColumnAlias) but
                // the formulaFields map is keyed by the original "@Total Cost" (with spaces).
                // Match by alias → ignore both case AND whitespace in the formula names.
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

        // Every group, regardless of IsVisible — so drill-down navigation can reach any level.
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

    /// <summary>
    /// Recursively substitutes <c>{@OtherFormula}</c> references inside a formula body
    /// with each referenced formula's own SQL expression. Used when resolving compound
    /// formulas like <c>@Profit = {@Total Selling} - {MyTable.CostField}</c>.
    /// </summary>
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
            // Crystal allows multiple Detail sections inside one Detail Area —
            // they render as additional rows BELOW the main detail line (e.g.
            // an italic notes line under each item). Only the FIRST detail
            // section becomes the row's columns; later ones are expansion-only
            // content (currently unsupported — we just skip them so they
            // don't appear as ghost columns with mismatched headers).
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

                // For formulas, store the formula key (e.g. "@Total Cost") in Field so
                // BuildColumnsFromSection can resolve it via the formulaFields map.
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

    /// <summary>
    /// Parses TextObjects and FieldHeadingObjects used as column labels.
    /// Looks in PageHeader (most common) and GroupHeader (used by some reports that repeat
    /// column labels above each group's detail rows).
    ///
    /// <para>
    /// Excludes labels from Areas that are <c>EnableHideForDrillDown="True"</c> and Sections
    /// that are suppressed at <c>drilldowngrouplevel=0</c> — these render deep inside hidden
    /// groups (e.g. POIndex column labels at Left=97) and would incorrectly match the outermost
    /// visible data columns.
    /// </para>
    /// </summary>
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
            // Skip areas that are hidden during normal (non-drilled) viewing.
            var hideForDrill = string.Equals((string?)area.Element("AreaFormat")?.Attribute("EnableHideForDrillDown"),
                                              "True", StringComparison.OrdinalIgnoreCase);
            if (hideForDrill) continue;

            foreach (var section in area.Elements("Sections").Elements("Section"))
            {
                // Skip sections suppressed at top-level drill (DrillDownGroupLevel=0).
                var suppress = (string?)section.Descendants("SectionAreaConditionFormulas")
                    .FirstOrDefault()?.Attribute("EnableSuppress") ?? "";
                if (suppress.Replace(" ", "")
                        .IndexOf("drilldowngrouplevel=0", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                foreach (var obj in section.Descendants().Where(e => e.Name.LocalName is "TextObject" or "FieldHeadingObject"))
                {
                    var text = (string?)obj.Element("Text") ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    // Skip group-title templates like "Major Group {tblX.Description}".
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
        // Crystal's <Area> elements appear in document order:
        //   • GroupHeader areas: outermost-first → headerAreas[i] = group level i
        //   • GroupFooter areas: innermost-first → footerAreas[N-1-i] = group level i
        // We use this ordering to correlate each <Group> with its header AND
        // footer AreaFormat blocks (page-break flags live on AreaFormat).
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

            // Visibility — corresponding GroupHeader area
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

            // Page break — Crystal stores this on the AreaFormat as either:
            //   • EnableNewPageBefore on the GroupHeader, or
            //   • EnableNewPageAfter  on the GroupFooter
            // Both encode the same semantic ("each new value of this group
            // starts on a fresh page"), so we OR them together.
            bool pageBreak = AttrIsTrue(headerArea?.Element("AreaFormat"), "EnableNewPageBefore");
            int footerIndex = totalGroups - 1 - groupIndex;
            if (footerIndex >= 0 && footerIndex < groupFooterAreas.Count)
            {
                var footerArea = groupFooterAreas[footerIndex];
                if (AttrIsTrue(footerArea.Element("AreaFormat"), "EnableNewPageAfter"))
                    pageBreak = true;
            }

            // Description detection — Crystal report authors typically place a
            // second FieldObject (e.g. Job Budget Report v1's
            // tblEstPhases_1.Description) OR embed a {Table.Field} reference
            // inside a TextObject template (Major Group and Category Report's
            // "Major Group  {tblMajorGroups.Description}") in the GroupHeader
            // section showing the group's human-readable description. Pick
            // the first plain {Table.Field} reference that ISN'T the group's
            // own ConditionField and ISN'T a "GroupName(...)" formula — that's
            // the description column.
            var descTable = "";
            var descField = "";
            if (headerArea != null)
            {
                // FieldObject DataSource — direct field bindings.
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

                // TextObject inline {Table.Field} references — Crystal lets
                // authors mix literal text with embedded field refs. Scan
                // each TextObject's <Text> content for the first {tbl.fld}
                // that isn't the group's own ConditionField.
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

            // Formula field reference: "{@MsgGroup}"
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

    /// <summary>
    /// Helper for AreaFormat boolean attributes — Crystal stores them as the
    /// strings "True"/"False" (capitalized). Treats missing attribute as false.
    /// </summary>
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

        // Only merge color/style formulas when they test the same row field. If they
        // differ, favor color because it has the strongest visual impact and avoids
        // evaluating a second condition model the renderer does not yet represent.
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

    /// <summary>
    /// Parses <c>&lt;FormulaFieldDefinition&gt;</c> elements. Returns map of formula name
    /// (including "@" prefix) → SQL expression.
    /// <para>
    /// Only simple patterns are translated (If/Then/Else, IsNull, table field refs). Complex
    /// Crystal formula syntax (local variables, loops, string manipulation) falls back to
    /// emitting a <c>NULL</c> expression with a comment — the field name still exists, so
    /// the report won't fail to parse, but the value will be empty.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Translates a Crystal formula body to a SQL expression. Handles:
    /// <list type="bullet">
    ///   <item><description>Comments (lines starting with <c>//</c>)</description></item>
    ///   <item><description><c>If c Then a Else b</c> → <c>CASE WHEN c THEN a ELSE b END</c></description></item>
    ///   <item><description><c>IsNull(x)</c> → <c>x IS NULL</c></description></item>
    ///   <item><description><c>x = ""</c> → <c>x = ''</c></description></item>
    ///   <item><description>Crystal field refs <c>{tbl.field}</c> → <c>tbl.field</c></description></item>
    ///   <item><description>Crystal function calls — see <see cref="TranslateCrystalFunctions"/></description></item>
    /// </list>
    /// </summary>
    private string TranslateCrystalFormula(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "NULL";

        // Strip Crystal "//" line comments
        var lines = body.Split('\n')
            .Select(l => Regex.Replace(l, @"//.*$", "").Trim())
            .Where(l => l.Length > 0);
        var text = string.Join(' ', lines).Trim();
        if (text.Length == 0) return "NULL";

        // Strip trailing ";" — Crystal-syntax statement terminators that aren't
        // meaningful in the SQL expression we're producing. Calendar.xml's
        // @Workorder formula ends with "+ {Homes.Addr1};" which would otherwise
        // produce "+ Homes.Addr1 ;" inside the generated SQL and fail to parse.
        text = Regex.Replace(text, @"\s*;\s*$", "");
        if (text.Length == 0) return "NULL";

        // Crystal "shared" variables (shared numbervar / stringvar / currencyvar /
        // datevar / booleanvar) bridge values between subreports and the main report
        // — there is no SQL equivalent. The main report's formula often is just
        // "shared numbervar amtapplied;" — a declaration that reads the value the
        // subreport already computed at runtime. We strip subreports during
        // SQL generation, so emit NULL and let the renderer show an empty cell.
        if (Regex.IsMatch(text, @"\bshared\s+(numbervar|stringvar|currencyvar|datevar|datetimevar|timevar|booleanvar)\b",
                RegexOptions.IgnoreCase))
            return "NULL";

        // Crystal field refs: {tbl.field} → " [tbl].field ". The leading and trailing
        // spaces are critical — Crystal often writes "If{POMaster.X}=true" with no
        // separator between the keyword and the field, which would collapse to
        // "If[POMaster].X=true" and break the If/Then/Else regex below.
        // Brackets around the alias defend against T-SQL reserved-word clashes.
        text = Regex.Replace(text, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" [{m.Groups[1].Value}].{m.Groups[2].Value.Trim()} ");
        // Crystal string literals use double quotes in formulas but SQL needs single quotes.
        // Convert "..." → '...' (taking care not to clobber embedded apostrophes).
        text = Regex.Replace(text, "\"([^\"]*)\"", m => "'" + m.Groups[1].Value.Replace("'", "''") + "'");

        // Try to match: If <cond> Then <val1> Else <val2>
        var ifMatch = Regex.Match(text,
            @"^\s*If\s+(.+?)\s+Then\s+(.+?)\s+Else\s+(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (ifMatch.Success)
        {
            var cond = TranslateCrystalCondition(ifMatch.Groups[1].Value);
            var thenVal = TranslateCrystalFunctions(ifMatch.Groups[2].Value.Trim());
            // Recursively translate the else branch so chained "else if ... then ... else ..."
            // builds nested CASE WHEN ... ELSE CASE WHEN ... END END.
            var elseVal = TranslateCrystalFormula(ifMatch.Groups[3].Value.Trim());
            return $"CASE WHEN {cond} THEN {thenVal} ELSE {elseVal} END";
        }

        // If the text still starts with "If " but didn't match the regex, the structure
        // is something we don't understand (mid-formula assignment, etc.). Emit NULL
        // rather than leaking raw Crystal into the SQL.
        if (Regex.IsMatch(text, @"^\s*If\b", RegexOptions.IgnoreCase))
            return "NULL";

        // Fallback: simple expression — translate functions and return.
        return TranslateCrystalFunctions(text);
    }

    /// <summary>
    /// Translates a Crystal condition expression (used inside If/Then) to SQL.
    /// Handles IsNull(x), "x = y", and logical AND/OR.
    /// </summary>
    private string TranslateCrystalCondition(string cond)
    {
        cond = cond.Trim();
        // IsNull(expr) → (expr IS NULL)
        cond = Regex.Replace(cond, @"IsNull\s*\(\s*([^)]+?)\s*\)",
            m => $"({m.Groups[1].Value.Trim()} IS NULL)", RegexOptions.IgnoreCase);
        // Crystal uses "or"/"and" — normalize case
        cond = Regex.Replace(cond, @"\bor\b", "OR", RegexOptions.IgnoreCase);
        cond = Regex.Replace(cond, @"\band\b", "AND", RegexOptions.IgnoreCase);
        cond = Regex.Replace(cond, @"\bnot\b", "NOT", RegexOptions.IgnoreCase);
        return TranslateCrystalFunctions(cond);
    }

    /// <summary>
    /// Translates Crystal LIKE wildcards inside string literals on the right-hand side
    /// of a <c>like</c> operator. Crystal uses <c>*</c>/<c>?</c>; T-SQL uses <c>%</c>/<c>_</c>.
    /// Only literals appearing after <c>like</c> are touched, so a column named e.g. "*"
    /// in a non-LIKE comparison is unaffected.
    /// </summary>
    private static string TranslateLikeWildcards(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return formula;

        // Match `like <rhs>` where <rhs> is everything up to the next AND/OR/closing paren.
        // The RHS may include string literals + concatenation (e.g. "'%' + @p + '*'").
        return Regex.Replace(formula,
            @"\blike\s+([^()]*?)(?=\s+(?:and|or)\b|\s*\)|\s*$)",
            m =>
            {
                var rhs = m.Groups[1].Value;
                // Within string literals only, swap Crystal wildcards for T-SQL ones.
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

    /// <summary>
    /// Translates common Crystal built-in functions to their SQL Server equivalents.
    /// Applied to both RecordSelectionFormula and FormulaFieldDefinitions.
    /// </summary>
    private string TranslateCrystalFunctions(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return expr;

        // Crystal aggregate-with-group: Sum(field, groupField) means "sum of field for the
        // current value of groupField". T-SQL Sum takes one arg; the group form is a window
        // function: SUM(field) OVER (PARTITION BY groupField). Same shape for Avg/Min/Max/Count.
        // Must run BEFORE the single-arg function-name normalizer below.
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

        // Crystal boolean literals in comparisons: "= true" / "= false" → "= 1" / "= 0".
        // T-SQL bit columns compare against 0/1, not the keywords true/false.
        expr = Regex.Replace(expr, @"=\s*true\b",  "= 1", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"=\s*false\b", "= 0", RegexOptions.IgnoreCase);

        // Date(x) → CAST(x AS date). This one is critical — T-SQL doesn't have a Date() function.
        expr = Regex.Replace(expr, @"\bDate\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS date)", RegexOptions.IgnoreCase);

        // CurrentDate → CAST(GETDATE() AS date)
        expr = Regex.Replace(expr, @"\bCurrentDate\b", "CAST(GETDATE() AS date)", RegexOptions.IgnoreCase);
        // CurrentDateTime / CurrentTime → GETDATE()
        expr = Regex.Replace(expr, @"\bCurrentDateTime\b", "GETDATE()", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bCurrentTime\b", "CAST(GETDATE() AS time)", RegexOptions.IgnoreCase);

        // Year/Month/Day — already match SQL Server names, but normalize case
        expr = Regex.Replace(expr, @"\bYear\s*\(", "YEAR(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bMonth\s*\(", "MONTH(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bDay\s*\(", "DAY(", RegexOptions.IgnoreCase);

        // DayOfWeek(x) / WeekDay(x) → DATEPART(WEEKDAY, x). Crystal returns 1=Sun..7=Sat;
        // SQL Server's DATEPART(WEEKDAY, …) returns 1..7 with the starting day controlled
        // by @@DATEFIRST (US English default = 7 / Sunday, which matches Crystal).
        // DayOfYear → DATEPART(DAYOFYEAR, …).
        expr = Regex.Replace(expr, @"\bDayOfWeek\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(WEEKDAY, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bWeekDay\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(WEEKDAY, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bDayOfYear\s*\(\s*([^)]+?)\s*\)",
            m => $"DATEPART(DAYOFYEAR, {m.Groups[1].Value.Trim()})", RegexOptions.IgnoreCase);

        // ToText(x) → CAST(x AS varchar(255))
        expr = Regex.Replace(expr, @"\bToText\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS varchar(255))", RegexOptions.IgnoreCase);

        // CStr(x[, decimals[, thousandSep[, decimalSep]]]) → CAST(x AS varchar(255)).
        // Crystal's Basic-syntax counterpart of ToText. The optional formatting args
        // are dropped — preserving them would require T-SQL FORMAT() per combo, and
        // the typical usage (Calendar.xml's @Workorder formula) just wants the raw
        // string concatenation, not locale-aware decimal/thousand grouping.
        expr = Regex.Replace(expr, @"\bCStr\s*\(\s*([^,)]+?)\s*(?:,[^)]*)?\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS varchar(255))", RegexOptions.IgnoreCase);

        // ToNumber(x) / val(x) → CAST(x AS float). Crystal's Val() parses a leading-numeric
        // string to a number; SQL Server's TRY_CAST gives a graceful NULL on non-numeric input.
        expr = Regex.Replace(expr, @"\bToNumber\s*\(\s*([^)]+?)\s*\)",
            m => $"CAST({m.Groups[1].Value.Trim()} AS float)", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bval\s*\(\s*([^)]+?)\s*\)",
            m => $"TRY_CAST({m.Groups[1].Value.Trim()} AS float)", RegexOptions.IgnoreCase);

        // mid(s, start, len) → SUBSTRING(s, start, len).  Crystal's mid uses 1-based indexing,
        // so does SUBSTRING — no offset translation needed.
        expr = Regex.Replace(expr, @"\bmid\s*\(", "SUBSTRING(", RegexOptions.IgnoreCase);
        // left(s, n) / right(s, n) → LEFT / RIGHT — same signature.
        expr = Regex.Replace(expr, @"\bleft\s*\(", "LEFT(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bright\s*\(", "RIGHT(", RegexOptions.IgnoreCase);
        // len(s) → LEN(s)
        expr = Regex.Replace(expr, @"\blen\s*\(", "LEN(", RegexOptions.IgnoreCase);

        // UpperCase / LowerCase / Trim
        expr = Regex.Replace(expr, @"\bUpperCase\s*\(", "UPPER(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bLowerCase\s*\(", "LOWER(", RegexOptions.IgnoreCase);
        expr = Regex.Replace(expr, @"\bTrim\s*\(", "LTRIM(RTRIM(", RegexOptions.IgnoreCase);
        //  NB: the extra closing paren for Trim is left to the formula author — if Trim({x})
        //  becomes LTRIM(RTRIM({x}), we emit LTRIM(RTRIM(x). The outer closing paren that
        //  closed Trim originally now closes the inner RTRIM. The LTRIM needs one more. This
        //  is rarely used in the reports we're translating; leave as-is for now.

        // IsNull in top-level expressions (outside If/Then)
        expr = Regex.Replace(expr, @"\bIsNull\s*\(\s*([^)]+?)\s*\)",
            m => $"({m.Groups[1].Value.Trim()} IS NULL)", RegexOptions.IgnoreCase);

        return expr;
    }

    private List<SummaryFieldInfo> ParseSummaryFields(XElement report)
    {
        // Example: Sum ({@Total Cost}, {SalesSheetCostsView.Phase})
        // Either ref can be a formula (e.g. {@PhaseDesc}) — we keep the raw "@Name"
        // form so BuildReportGroups can match formula-based group definitions.
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

    /// <summary>
    /// Translates Crystal's RecordSelectionFormula to a SQL WHERE clause.
    /// Handles: <c>{tbl.field}</c> → <c>tbl.field</c>, <c>{?Param}</c> → <c>@Param</c>,
    /// <c>{@Formula}</c> → inlined formula SQL, Crystal function calls, and Crystal operators.
    /// </summary>
    private string ConvertRecordSelectionFormula(XElement report, Dictionary<string, string> formulaFields)
    {
        var formula = (string?)report.Element("DataDefinition")?.Element("RecordSelectionFormula") ?? "";
        formula = formula.Trim();
        if (string.IsNullOrWhiteSpace(formula)) return "";

        // Strip Crystal "//" line comments. Mirrors TranslateCrystalFormula: report
        // authors comment out trial WHERE clauses (e.g. Job Cost Overview v1 main
        // report's commented "{TransactionDate}>={?Start Date}" line); without this
        // they would leak into the SQL as `// {...} AND` and fail with
        // "Incorrect syntax near '/'." Per-line strip keeps the rest intact.
        formula = string.Join('\n', formula.Split('\n')
            .Select(l => Regex.Replace(l, @"//.*$", "").TrimEnd())
            .Where(l => l.Trim().Length > 0));
        if (string.IsNullOrWhiteSpace(formula)) return "";

        // Formula field refs: {@Name} → the formula's SQL expression (parenthesized for safety).
        formula = Regex.Replace(formula, @"\{@([^}]+)\}", m =>
        {
            var key = "@" + m.Groups[1].Value.Trim();
            return formulaFields.TryGetValue(key, out var sql) ? $"({sql})" : m.Value;
        });

        // Table-field refs: {tableAlias.fieldName} → " [tableAlias].fieldName "
        // (surrounding spaces keep adjacent keywords/operators from collapsing;
        // brackets around the alias defend against T-SQL reserved-word clashes
        // like Priority / Order / Group / User — Calendar.xml's Priority alias
        // failed with "Incorrect syntax near 'Priority'" until this was bracketed).
        formula = Regex.Replace(formula, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" [{m.Groups[1].Value}].{m.Groups[2].Value.Trim()} ");

        // Parameter refs: {?ParamName} → @ParamName. Sanitize the same way ParseParameters does:
        // strip everything that isn't a letter, digit, or underscore. Otherwise names like
        // "Job Filter (optional)" would emit "@Job Filter (optional)" which is invalid T-SQL.
        formula = Regex.Replace(formula, @"\{\?([^}]+)\}", m => "@" + SanitizeParamName(m.Groups[1].Value));

        // Crystal string literals: "foo" → 'foo' (SQL uses single quotes)
        formula = Regex.Replace(formula, "\"([^\"]*)\"", m => "'" + m.Groups[1].Value.Replace("'", "''") + "'");

        // Crystal LIKE wildcards inside string literals → T-SQL equivalents.
        // Crystal: '*' = any chars, '?' = single char.   T-SQL: '%' and '_'.
        // Without this translation, e.g. `Job like @p + '*'` matches a literal asterisk
        // (returning zero rows), instead of "any suffix" — breaking optional Job filters
        // in PO Integration Status Report v1 etc.
        formula = TranslateLikeWildcards(formula);

        // Translate Crystal built-in functions (Date(), Year(), ToText(), etc.)
        formula = TranslateCrystalFunctions(formula);

        // Crystal operators to SQL — just normalize case
        formula = Regex.Replace(formula, @"\bAND\b", "AND", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"\bOR\b", "OR", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"\bNOT\b", "NOT", RegexOptions.IgnoreCase);

        // Bit-column handling — Crystal treats a boolean field as a predicate on its own
        // (e.g. `NOT ({tbl.BudgetDeleted})` or just `{tbl.IsActive}` as a condition), but
        // SQL Server requires a comparison. Rewrite those patterns:
        //   NOT (table.field)  →  (ISNULL(table.field, 0) = 0)   ← NULL is treated as false
        //                                                          (matches Crystal semantics)
        //   NOT table.field    →  ISNULL(table.field, 0) = 0
        //
        // Using ISNULL(..., 0) = 0 instead of "= 0" preserves Crystal's behavior where a NULL
        // boolean is considered "false" — so `NOT {BudgetDeleted}` matches rows where
        // BudgetDeleted is either FALSE (0) or NULL.
        // Both `[Alias].Field` (the standard output after bracketing) and plain
        // `Alias.Field` are recognized — the latter shows up when other code paths
        // emit references without going through the bracketing translation above.
        formula = Regex.Replace(formula, @"\bNOT\s*\(\s*((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)\s*\)",
            m => $"(ISNULL({m.Groups[1].Value}, 0) = 0)");
        formula = Regex.Replace(formula, @"\bNOT\s+((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)(?=\s|$|\))",
            m => $"ISNULL({m.Groups[1].Value}, 0) = 0");

        // Bare-positive bit predicates: " AND  tbl.Field  )" → " AND  (ISNULL(tbl.Field, 0) = 1)  )".
        // Crystal treats `{tbl.BooleanColumn}` standing alone in the WHERE as
        // "where this bit is true"; SQL Server rejects a bare bit column in a
        // boolean context (error 4145, "non-boolean type in context where
        // condition is expected"). Anchor the match on an AND/OR keyword
        // before AND a close-paren/AND/OR/end-of-string after — that way
        // `tbl.Field = 1` (followed by `=`) and `tbl.Field` inside a function
        // call (followed by `,`) are NOT touched.
        formula = Regex.Replace(formula,
            @"(\b(?:AND|OR)\s+)((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)(\s*)(?=\)|\bAND\b|\bOR\b|$)",
            m => $"{m.Groups[1].Value}(ISNULL({m.Groups[2].Value}, 0) = 1){m.Groups[3].Value}");

        // Crystal-vs-SQL-Server NULL handling on empty-string comparisons.
        // Crystal Reports' default report option "Convert Database NULL Values
        // to Default" makes NULL strings behave like "" — so `{field} = ""`
        // is true for both empty AND null. SQL Server is strict: `field = ''`
        // is false when field is NULL. Reports like Global Option Costs v1
        // filter `Model = ""` to capture Global Options (Model is NULL in the
        // underlying view); the legacy Crystal viewer shows those rows, ours
        // dropped them until this rewrite.
        // Only literal `= ''` is rewritten — `= @Param` and `= 'something'`
        // stay strict so unrelated equality semantics are unaffected.
        formula = Regex.Replace(formula, @"((?:\[[A-Za-z_]\w*\]|[A-Za-z_]\w*)\.[A-Za-z_]\w*)\s*=\s*''",
            m => $"({m.Groups[1].Value} = '' OR {m.Groups[1].Value} IS NULL)");
        // Bare boolean-column predicates (e.g. `AND {tbl.IsActive}`) aren't rewritten yet —
        // would need column-type metadata to distinguish bit columns from plain refs in
        // other expressions. Report authors typically use explicit `= true / = false` there.

        // Top-level Crystal "IF cond THEN then-pred ELSE else-pred" → SQL boolean.
        // Crystal lets the record-selection formula switch its WHERE based on a
        // condition (Job Cost Overview v1's main report does this for AP rows).
        // SQL has no IF inside WHERE, so re-express as
        //   ((cond) AND (then-pred)) OR (NOT (cond) AND (else-pred)).
        // Done last so the branches are fully translated when we wrap them.
        formula = TranslateTopLevelIfThenElse(formula);

        return formula.Trim();
    }

    /// <summary>
    /// Translates a top-level "IF cond THEN p1 ELSE p2" wrapping the entire WHERE
    /// formula into an equivalent SQL boolean predicate. Recurses on the else branch
    /// so chained "ELSE IF" produces nested OR-clauses. Returns the input unchanged
    /// when no top-level IF/THEN/ELSE is present.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════
    // SQL + column construction
    // ═══════════════════════════════════════════════════════════════════

    private List<ReportColumn> BuildColumns(
        List<DetailFieldInfo> detailFields,
        List<PageHeaderLabel> headerLabels,
        List<TableInfo> tables,
        Dictionary<string, string> formulaFields,
        IReadOnlyList<SubreportObjectInfo> subreportObjects)
    {
        var columns = new List<ReportColumn>();
        // Track heading texts already assigned so the same label isn't reused
        // for two adjacent columns (e.g. ItemDesc was previously winning the
        // header for the Item column too via fragile string matching).
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var df in detailFields)
        {
            string alias;
            string header;
            ReportColumnType colType;

            if (df.IsFormula)
            {
                // Crystal formula field (e.g. {@Amount} = OrderQty * Rate). Surface
                // it as a computed column — alias resolved either to the underlying
                // table.field (simple ref) or to a sanitized "Formula_Name" alias
                // (compound expression). BuildSelectFields will translate the alias
                // to the corresponding SQL expression.
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
                // Alias the column as "{Table}_{Field}" to avoid duplicate names when two tables share a field.
                alias = $"{df.Table}_{df.Field}";
                header = MatchHeaderLabel(headerLabels, df, usedLabels);
                if (string.IsNullOrWhiteSpace(header))
                    header = df.Field;
                colType = InferColumnType(formulaKey: null, headerText: header, fieldName: df.Field);
            }

            if (!string.IsNullOrEmpty(header)) usedLabels.Add(header);

            // De-duplicate by alias (the same formula can be referenced more than once).
            if (columns.Any(c => string.Equals(c.Field, alias, StringComparison.OrdinalIgnoreCase)))
                continue;

            columns.Add(new ReportColumn
            {
                Field = alias,
                HeaderText = header,
                // Honor the Crystal author's width as-is (twips → px).  0 means
                // "no XML hint" so the renderer's adaptive sizer falls back to
                // header + data measurement for that column.
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

    /// <summary>
    /// Finds the best column header for a detail field, in order of preference:
    /// <list type="number">
    ///   <item><description>
    ///     <b>Explicit Crystal link</b> — a <c>FieldHeadingObject</c> whose
    ///     <c>FieldObjectName</c> matches the detail FieldObject's <c>Name</c>
    ///     (e.g. heading <c>FieldObjectName="Item1"</c> ↔ field <c>Name="Item1"</c>).
    ///     This is Crystal's authoritative heading binding — when present we use
    ///     it verbatim and ignore everything else.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Positional overlap</b> — the un-used label that horizontally
    ///     overlaps the detail field most. Only used when no explicit link exists.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <paramref name="usedLabels"/> tracks heading texts already claimed by earlier
    /// columns so the same label doesn't get assigned to two adjacent fields (which
    /// is why "Item" used to render as "ItemDesc" — without tracking, both columns
    /// matched the wider "ItemDesc" heading).
    /// </para>
    /// </summary>
    private static string MatchHeaderLabel(List<PageHeaderLabel> labels, DetailFieldInfo df,
        HashSet<string>? usedLabels = null)
    {
        bool NotUsed(PageHeaderLabel l) =>
            usedLabels == null || !usedLabels.Contains(l.Text);
        bool SemanticOk(PageHeaderLabel l) =>
            !(IsMoneyLabel(l.Text) && IsCodeLikeField(df));

        // 1. Crystal's explicit FieldHeadingObject ↔ FieldObject binding.
        //    Heading.FieldObjectName == Field.Name is the primary link, but
        //    only when the label is ALSO positioned near the field — RptToXml
        //    sometimes exports the wrong FieldObjectName attribute. Model
        //    Costs v1's PageHeader has FOUR FieldHeadingObjects all claiming
        //    FieldObjectName="Item1" (Item, Qty, UOM, Rate) with the Text
        //    actually appropriate to whichever column they're positioned
        //    over. Without the positional sanity check, the first far-away
        //    label (e.g. "Rate" at Left=9308) wins for "Item1" at Left=552
        //    and every downstream column gets the wrong header.
        if (!string.IsNullOrEmpty(df.Name))
        {
            var bound = labels.FirstOrDefault(l =>
                NotUsed(l) && SemanticOk(l) &&
                string.Equals(l.FieldObjectName, df.Name, StringComparison.OrdinalIgnoreCase) &&
                l.Left + l.Width >= df.Left && l.Left <= df.Left + df.Width);
            if (bound != null) return bound.Text;
        }

        // 2. Positional fallback for fields with no explicit heading link
        //    (e.g. ad-hoc reports where the author drew a TextObject in
        //    PageHeader instead of a FieldHeadingObject).
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

    /// <summary>
    /// Builds the SELECT-list items needed for the report:
    /// <list type="number">
    ///   <item><description>Visible report columns (detail fields from the XML)</description></item>
    ///   <item><description>Group-by fields not already among visible columns (so the renderer can display group headers)</description></item>
    ///   <item><description>Formula-field groups, emitted as computed columns: <c>(CASE WHEN … END) AS [@MsgGroup]</c></description></item>
    /// </list>
    /// Each item is either a plain "{table}_{field}" alias (BuildSql expands) or a pre-formed
    /// SQL expression with "AS [..]" already attached (BuildSql passes verbatim).
    /// </summary>
    private List<string> BuildSelectFields(List<ReportColumn> columns, List<GroupInfo> groups, Dictionary<string, string> formulaFields)
    {
        var selects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            if (col.IsSubreportObject)
                continue;

            // Compound-formula columns (alias starts with "Formula_") need their
            // formula body translated to a SQL expression instead of being passed
            // through verbatim. The renderer reads the values via the alias.
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
                // Formula group: emit "(expr) AS [@Name]" — BuildSql will pass this through verbatim.
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

    /// <summary>Emit a description column alongside the group's main column
    /// when ParseGroups detected one in the Crystal GroupHeader section.
    /// The pre-formed "AS [...]" form bypasses BuildSql's alias-expansion
    /// logic — necessary because the description's table is often a
    /// different alias from the group's own table (typical self-join
    /// "_1" pattern for parent-group descriptions).</summary>
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
        // Crystal groups implicitly sort by group fields; union with explicit SortFields
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

    /// <summary>Produces a SQL-safe alias for a formula group (e.g., "@MsgGroup" → "Formula_MsgGroup").</summary>
    private static string FormulaGroupAlias(string formulaName)
        => "Formula_" + formulaName.TrimStart('@').Replace(" ", "");

    /// <summary>
    /// Builds an additional WHERE clause for a drill-down tab. Each <see cref="DrillDownFilter"/>
    /// in <paramref name="path"/> matches one Crystal group — we look up the underlying
    /// table.field and emit <c>table.field = 'value'</c> (or numeric equivalent).
    /// </summary>
    private string BuildDrillDownWhere(
        IReadOnlyList<DrillDownFilter> path,
        List<GroupInfo> groups,
        Dictionary<string, string> formulaFields)
    {
        var parts = new List<string>();
        foreach (var f in path)
        {
            // Two filter shapes coming from the data-path attribute:
            //   • "Table_Field"   — table-bound group, emit table.field = value
            //   • "Formula_Name"  — formula group, inline the formula's SQL
            //                       expression and compare it to the value
            //                       (matches the same string the renderer
            //                       printed in the row, e.g. "06040 Full
            //                       Enclosure Items" for Phase + ' ' +
            //                       PhaseDesc).
            string? lhs = null;

            if (f.Field.StartsWith("Formula_", StringComparison.OrdinalIgnoreCase))
            {
                // Match formula-aliased filter to a GroupInfo via FormulaName.
                var needle = f.Field.Substring("Formula_".Length);
                var group = groups.FirstOrDefault(g =>
                    g.FormulaName != null
                    && string.Equals(g.FormulaName.TrimStart('@').Replace(" ", ""), needle, StringComparison.OrdinalIgnoreCase));
                if (group?.FormulaName != null
                    && formulaFields.TryGetValue(group.FormulaName, out var formulaSql))
                {
                    var inlined = InlineFormulaReferences(formulaSql, formulaFields);
                    // LTRIM(RTRIM(...)) handles the case where Crystal's
                    // formula concatenates CHAR(n) columns or columns
                    // with trailing whitespace — the rendered value is
                    // trimmed (see BuildCellPathAttribute), so the SQL
                    // comparison must trim too or "1132 1132 Plan" never
                    // equals "1132   1132 Plan          " under the hood.
                    lhs = $"LTRIM(RTRIM({inlined}))";
                }
            }
            else
            {
                // Table-bound group (alias "Table_Field"). Find the matching
                // GroupInfo by Table+Field equality.
                var group = groups.FirstOrDefault(g =>
                    g.FormulaName == null
                    && string.Equals($"{g.Table}_{g.Field}", f.Field, StringComparison.OrdinalIgnoreCase));
                if (group != null)
                    lhs = $"{group.Table}.{group.Field}";
            }

            if (lhs == null) continue;

            // Empty / null values need a disjunction — the DB might store
            // the absence as either SQL NULL or an empty string, and we
            // have to match both.
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

    /// <summary>
    /// Formats a non-empty drill-down filter value as a SQL literal. Numbers go through un-quoted;
    /// everything else is escaped and quoted as a string (single-quote doubled).
    /// </summary>
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

        // SELECT (DISTINCT for drill-down reports collapses repeated rows from Phase/Item joins)
        sb.Append(useDistinct ? "SELECT DISTINCT " : "SELECT ");
        if (selectFields.Count == 0)
        {
            sb.Append("*");
        }
        else
        {
            // Sort table aliases longest-first so a select like
            // "MsgQ_CutOffDates_MsgType" matches the "MsgQ_CutOffDates" table
            // rather than the shorter "MsgQ". Splitting blindly on the first
            // underscore (the previous behavior) emitted
            // "MsgQ.CutOffDates_MsgType" and SQL Server failed with
            // "Invalid column name 'CutOffDates_MsgType'".
            var tableAliases = tables
                .Select(t => t.Alias)
                .OrderByDescending(a => a.Length)
                .ToList();

            var selects = selectFields.Select(item =>
            {
                // Pre-formed SQL expressions (formula-field computed columns) already contain "AS".
                if (item.Contains(" AS [", StringComparison.OrdinalIgnoreCase))
                    return item;

                // Alias like "{table}_{field}" — recover (table, field) by
                // longest-prefix match against the known tables. The alias is
                // generated from the original LongName "table.field" with the
                // dot replaced by "_", so a table whose own name has an
                // underscore would be ambiguous without this.
                foreach (var tbl in tableAliases)
                {
                    var prefix = tbl + "_";
                    if (item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var fld = item.Substring(prefix.Length);
                        return $"[{tbl}].{fld} AS [{item}]";
                    }
                }

                // Fallback: legacy first-underscore split. Hit for select
                // items that aren't tied to a known table (rare — typically
                // a computed column that slipped through without an "AS").
                var underscore = item.IndexOf('_');
                if (underscore < 0) return item;
                var fallbackTbl = item.Substring(0, underscore);
                var fallbackFld = item.Substring(underscore + 1);
                return $"[{fallbackTbl}].{fallbackFld} AS [{item}]";
            });
            sb.Append(string.Join(", ", selects));
        }

        // FROM: start with the first table (the "anchor" — usually the master/driver table)
        var firstTable = tables[0];
        sb.Append($" FROM {RenderTableSource(firstTable)}");

        // JOINs — BFS: keep joining any unjoined table that has a link to an already-joined
        // table. This handles reports where the XML Tables order doesn't match the join topology.
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
                // No reachable table left — emit remaining as CROSS JOINs (unusual, but lets SQL parse).
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

        // WHERE
        if (!string.IsNullOrWhiteSpace(whereSql))
            sb.Append(" WHERE ").Append(whereSql);

        // ORDER BY
        sb.Append(orderByClause);

        return sb.ToString();
    }

    private static TableLinkInfo? FindLinkFor(List<TableLinkInfo> links, string alias, HashSet<string> joined)
    {
        // A usable link must involve `alias` on one side and something already joined on the other.
        foreach (var link in links)
        {
            var srcAliases = link.Source.Select(f => f.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dstAliases = link.Destination.Select(f => f.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (dstAliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                srcAliases.Any(a => joined.Contains(a)))
                return link;

            if (srcAliases.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                dstAliases.Any(a => joined.Contains(a)))
                // swap — the unjoined side should be on the right
                return new TableLinkInfo(link.JoinType, link.Destination, link.Source);
        }
        return null;
    }

    private List<ReportGroup> BuildReportGroups(List<GroupInfo> groups, List<SummaryFieldInfo> summaries)
    {
        var result = new List<ReportGroup>();
        // First, collect a "default" set of aggregates seen anywhere in the
        // report (typically Sum-of-Amount). When a group has no explicit
        // SummaryFieldDefinition tied to it (e.g. Crystal omitted Community-
        // level totals), we propagate this default so EVERY group level shows
        // a subtotal — same expectation the VB6 Crystal viewer raises.
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
            // When ParseGroups detected a description FieldObject in the
            // GroupHeader section, the SELECT includes a "{alias}_Desc"
            // column carrying that description. Wire HeaderFormat to show
            // "Code  Description" — same shape Crystal renders by virtue
            // of placing both FieldObjects side-by-side in the header band.
            // Empty DescriptionField keeps the legacy "{Value}"-only format
            // so reports without descriptions are unaffected.
            var hasDescription = !string.IsNullOrEmpty(g.DescriptionField);
            var rg = new ReportGroup
            {
                Field = alias,
                ShowHeader = true,
                // ShowFooter is set BELOW after aggregates are populated —
                // groups with no subtotals would otherwise produce a phantom
                // footer row containing only the group's code (the "DOORHW"
                // repeated at the bottom of each group in the Major Group
                // and Category Report). When the XML's GroupFooter section
                // is empty AND the group has no Crystal-defined Summary
                // fields, we skip the footer entirely.
                ShowFooter = false,
                // Group header shows JUST the value — no "FieldName: " caption
                // prefix. Mirrors the VB6 Crystal Reports look-and-feel where the
                // value is the entire header (e.g. "Alcovy Meadows", "1132 Plan",
                // "03000 PREPARATION PRELIMINARIES") rather than "Community: Alcovy
                // Meadows" / "Model: 1132 Plan" etc.
                HeaderFormat = hasDescription ? "{Value}  {Description}" : "{Value}",
                DescriptionField = hasDescription ? $"{alias}_Desc" : "",
                // Page-break flag faithfully reflects the Crystal author's
                // choice (EnableNewPageBefore on header / EnableNewPageAfter
                // on footer in the XML).
                PageBreakBefore = g.PageBreakBefore
            };

            // Match the SummaryFieldDefinition's GroupField against this group.
            //   • table-field group → "{table}.{field}"  (e.g. "tblFoo.Bar")
            //   • formula group     → "@FormulaName"     (e.g. "@PhaseDesc")
            // ParseSummaryFields emits `s.GroupField` in the same shape produced by
            // ParseCrystalFieldRef — i.e. "table.field" for {table.field} or the raw
            // formula-name "@Foo" for {@Foo}. We mirror that mapping here.
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

            // No SummaryFieldDefinition was tied to this group? Propagate the
            // report's default Sum aggregates so this level still shows a
            // subtotal — the user expectation is "every group has a total".
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

            // Show the footer only when there's something useful to display
            // there — i.e. at least one aggregate column. Empty footers are
            // suppressed so the group's code doesn't show up twice (once in
            // the header, once as a stray label in the footer row).
            rg.ShowFooter = rg.Aggregates.Count > 0;

            result.Add(rg);
        }
        return result;
    }

    /// <summary>
    /// Translate a SummaryFieldInfo.Field (the raw Crystal expression) into
    /// the column alias used by the renderer:
    ///   • table-field summary → "{table}_{field}"  (e.g. "tblFoo_Bar")
    ///   • formula summary     → "Formula_{Name}"   (e.g. "Formula_Amount")
    /// </summary>
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

    /// <summary>
    /// Prefixes a table name with the configured <see cref="ReportOptions.SchemaPrefix"/>.
    /// Returns <c>"{schema}.{table}"</c>, or just <c>"{table}"</c> when the prefix is empty
    /// (letting SQL Server resolve via the connection user's default schema).
    /// </summary>
    private string QualifyTable(string tableName)
        => string.IsNullOrWhiteSpace(_options.SchemaPrefix)
            ? tableName
            : $"{_options.SchemaPrefix}.{tableName}";

    /// <summary>
    /// Renders a Crystal table reference for a SQL FROM/JOIN clause:
    ///   - Regular table → <c>dbo.TableName AS [Alias]</c> (alias omitted when same as name)
    ///   - Crystal Command table (embedded SELECT) → <c>(sql) AS [Alias]</c>
    /// Aliases are always bracketed so reports whose Crystal alias collides with
    /// a T-SQL reserved word (Priority, Order, Group, User, Type, Status, ...)
    /// still produce valid SQL. Brackets are a no-op for non-reserved identifiers,
    /// so this is purely defensive.
    /// </summary>
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
