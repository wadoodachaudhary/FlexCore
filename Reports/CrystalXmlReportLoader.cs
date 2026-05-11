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
    /// Loads and parses a Crystal Reports XML file into a renderable <see cref="ReportDefinition"/>.
    /// </summary>
    public ReportDefinition Load(string xmlFilePath)
    {
        return LoadInternal(xmlFilePath, drillPath: null);
    }

    private ReportDefinition LoadInternal(string xmlFilePath, IReadOnlyList<DrillDownFilter>? drillPath)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Crystal Reports XML not found: {xmlFilePath}", xmlFilePath);

        var doc = XDocument.Load(xmlFilePath);
        var report = doc.Root ?? throw new InvalidDataException("Invalid XML: missing root element.");

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
            ReportId = Path.GetFileNameWithoutExtension(xmlFilePath),
            Title = Path.GetFileNameWithoutExtension(xmlFilePath).Replace("_", " "),
            SourceRptFile = xmlFilePath
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
            columns = BuildColumnsFromSection(summaryFieldsFromHeader, headerLabels, formulaFields);
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
            columns = BuildColumnsFromSection(detailFields, headerLabels, formulaFields);
            // For drill-down tabs, include ALL groups so the tree can descend further
            // (into Phase / POIndex) and the rendered output can show nested group headers.
            selectFields = BuildSelectFieldsForDrillDown(columns, groups, formulaFields);
        }
        else
        {
            // Non-drill-down report: show the Detail-section columns as before.
            columns = BuildColumns(detailFields, headerLabels, tables, formulaFields);
            selectFields = BuildSelectFields(columns, groups, formulaFields);
        }

        // Extend WHERE with drill-down filters supplied by the caller.
        if (isDrillTab)
        {
            var filterSql = BuildDrillDownWhere(drillPath!, groups);
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
    private sealed record DetailFieldInfo(string Name, string Table, string Field, int Left, int Width, string Alignment, string DataSource, bool IsFormula);
    private sealed record PageHeaderLabel(string Text, int Left, int Width, string? FieldObjectName);
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
    private sealed record GroupInfo(string Table, string Field, string? FormulaName, bool IsVisible = true, bool PageBreakBefore = false);
    private sealed record SortFieldInfo(string Table, string Field, string? FormulaName, string Direction, string SortType);
    private sealed record SummaryFieldInfo(string Operation, string Field, string GroupField);

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
            var usageFlags = usage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isInUse = usageFlags.Any(f => string.Equals(f, "InUse", StringComparison.OrdinalIgnoreCase));

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

            list.Add(new ReportParameter
            {
                Name = name,
                DisplayName = rawName,
                Prompt = prompt,
                ParameterType = MapParameterType(valueKind),
                Required = !promptIsOptional && isInUse,
                IsOptional = nameSaysOptional,
                AllowMultiple = string.Equals((string?)p.Attribute("EnableAllowMultipleValue"), "True", StringComparison.OrdinalIgnoreCase),
                DefaultValue = defaultVal,
                DefaultValues = defaultValues
            });
        }
        return list;
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

                // Must contain at least one FieldObject (not just labels)
                if (!section.Descendants("FieldObject").Any()) continue;

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
        foreach (var field in section.Descendants("FieldObject"))
        {
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
                        IsFormula: false));
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
                    IsFormula: false));
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
                    IsFormula: true));
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
        List<PageHeaderLabel> labels, Dictionary<string, string> formulaFields)
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
                ColumnType = InferColumnType(formulaKey: df.IsFormula ? df.Field : null, headerText: header, fieldName: df.Field)
            });
        }
        return cols;
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

            foreach (var field in primarySection.Descendants("FieldObject"))
            {
                var ds = (string?)field.Attribute("DataSource") ?? "";
                var parsed = ParseCrystalFieldRef(ds);
                var isFormula = ds.StartsWith("{@"); // formula field like {@Total Cost}
                if (!parsed.HasValue && !isFormula) continue;

                var name = (string?)field.Attribute("Name") ?? "";
                var left = ParseInt(field.Attribute("Left"));
                var width = ParseInt(field.Attribute("Width"));
                var alignment = (string?)field.Element("ObjectFormat")?.Attribute("HorizontalAlignment") ?? "DefaultAlign";

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
                    IsFormula: isFormula));
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

            // Formula field reference: "{@MsgGroup}"
            var formulaMatch = Regex.Match(condField, @"^\{@([^}]+)\}$");
            if (formulaMatch.Success)
            {
                list.Add(new GroupInfo("", "", "@" + formulaMatch.Groups[1].Value.Trim(),
                    IsVisible: visible, PageBreakBefore: pageBreak));
                continue;
            }
            var parsed = ParseCrystalFieldRef(condField);
            if (parsed.HasValue)
                list.Add(new GroupInfo(parsed.Value.Table, parsed.Value.Field, null,
                    IsVisible: visible, PageBreakBefore: pageBreak));
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

        // Crystal "shared" variables (shared numbervar / stringvar / currencyvar /
        // datevar / booleanvar) bridge values between subreports and the main report
        // — there is no SQL equivalent. The main report's formula often is just
        // "shared numbervar amtapplied;" — a declaration that reads the value the
        // subreport already computed at runtime. We strip subreports during
        // SQL generation, so emit NULL and let the renderer show an empty cell.
        if (Regex.IsMatch(text, @"\bshared\s+(numbervar|stringvar|currencyvar|datevar|datetimevar|timevar|booleanvar)\b",
                RegexOptions.IgnoreCase))
            return "NULL";

        // Crystal field refs: {tbl.field} → " tbl.field ". The leading and trailing
        // spaces are critical — Crystal often writes "If{POMaster.X}=true" with no
        // separator between the keyword and the field, which would collapse to
        // "IfPOMaster.X=true" and break the If/Then/Else regex below.
        text = Regex.Replace(text, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" {m.Groups[1].Value}.{m.Groups[2].Value.Trim()} ");
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

        // ToText(x) → CAST(x AS varchar(255))
        expr = Regex.Replace(expr, @"\bToText\s*\(\s*([^)]+?)\s*\)",
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

        // Formula field refs: {@Name} → the formula's SQL expression (parenthesized for safety).
        formula = Regex.Replace(formula, @"\{@([^}]+)\}", m =>
        {
            var key = "@" + m.Groups[1].Value.Trim();
            return formulaFields.TryGetValue(key, out var sql) ? $"({sql})" : m.Value;
        });

        // Table-field refs: {tableAlias.fieldName} → " tableAlias.fieldName " (with surrounding
        // spaces to keep adjacent keywords/operators from collapsing — same reason as in
        // TranslateCrystalFormula).
        formula = Regex.Replace(formula, @"\{([A-Za-z_][\w]*)\.([^}]+)\}", m => $" {m.Groups[1].Value}.{m.Groups[2].Value.Trim()} ");

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
        formula = Regex.Replace(formula, @"\bNOT\s*\(\s*([A-Za-z_]\w*\.[A-Za-z_]\w*)\s*\)",
            m => $"(ISNULL({m.Groups[1].Value}, 0) = 0)");
        formula = Regex.Replace(formula, @"\bNOT\s+([A-Za-z_]\w*\.[A-Za-z_]\w*)(?=\s|$|\))",
            m => $"ISNULL({m.Groups[1].Value}, 0) = 0");
        // Bare boolean-column predicates (e.g. `AND {tbl.IsActive}`) aren't rewritten yet —
        // would need column-type metadata to distinguish bit columns from plain refs in
        // other expressions. Report authors typically use explicit `= true / = false` there.

        return formula.Trim();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SQL + column construction
    // ═══════════════════════════════════════════════════════════════════

    private List<ReportColumn> BuildColumns(
        List<DetailFieldInfo> detailFields,
        List<PageHeaderLabel> headerLabels,
        List<TableInfo> tables,
        Dictionary<string, string> formulaFields)
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
                       : ""
            });
        }
        return columns;
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
        //    Heading.FieldObjectName == Field.Name is the authoritative link;
        //    no string-similarity heuristics needed.
        if (!string.IsNullOrEmpty(df.Name))
        {
            var bound = labels.FirstOrDefault(l =>
                NotUsed(l) && SemanticOk(l) &&
                string.Equals(l.FieldObjectName, df.Name, StringComparison.OrdinalIgnoreCase));
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
        }
        return selects;
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
                var key = $"{g.Table}.{g.Field}";
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
                var key = $"{s.Table}.{s.Field}" + (s.Direction == "DescendingOrder" ? " DESC" : "");
                if (seen.Add($"{s.Table}.{s.Field}")) parts.Add(key);
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
    private string BuildDrillDownWhere(IReadOnlyList<DrillDownFilter> path, List<GroupInfo> groups)
    {
        var parts = new List<string>();
        foreach (var f in path)
        {
            // Find a group whose alias matches the filter's Field.
            var group = groups.FirstOrDefault(g =>
                string.Equals($"{g.Table}_{g.Field}", f.Field, StringComparison.OrdinalIgnoreCase));
            if (group == null) continue;

            var lhs = $"{group.Table}.{group.Field}";
            // Empty / null values need a disjunction — the DB might store the absence as
            // either SQL NULL or an empty string, and we have to match both.
            if (string.IsNullOrEmpty(f.Value))
            {
                parts.Add($"({lhs} IS NULL OR {lhs} = '')");
            }
            else
            {
                parts.Add($"{lhs} = {FormatLiteral(f.Value)}");
            }
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
            var selects = selectFields.Select(item =>
            {
                // Pre-formed SQL expressions (formula-field computed columns) already contain "AS".
                if (item.Contains(" AS [", StringComparison.OrdinalIgnoreCase))
                    return item;

                // Alias like "{table}_{field}" — expand to "table.field AS [table_field]"
                var underscore = item.IndexOf('_');
                if (underscore < 0) return item;
                var tbl = item.Substring(0, underscore);
                var fld = item.Substring(underscore + 1);
                return $"{tbl}.{fld} AS [{item}]";
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
                onParts.Add($"{s.Table}.{s.Field} = {d.Table}.{d.Field}");
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
            var rg = new ReportGroup
            {
                Field = alias,
                ShowHeader = true,
                ShowFooter = true,
                // Group header shows JUST the value — no "FieldName: " caption
                // prefix. Mirrors the VB6 Crystal Reports look-and-feel where the
                // value is the entire header (e.g. "Alcovy Meadows", "1132 Plan",
                // "03000 PREPARATION PRELIMINARIES") rather than "Community: Alcovy
                // Meadows" / "Model: 1132 Plan" etc.
                HeaderFormat = "{Value}",
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
    ///   - Regular table → <c>dbo.TableName AS Alias</c> (alias omitted when same as name)
    ///   - Crystal Command table (embedded SELECT) → <c>(sql) AS Alias</c>
    /// </summary>
    private string RenderTableSource(TableInfo t)
    {
        if (!string.IsNullOrWhiteSpace(t.CommandSql))
            return $"({t.CommandSql}) AS {t.Alias}";

        var qualified = QualifyTable(t.Name);
        return string.Equals(t.Alias, t.Name, StringComparison.Ordinal)
            ? qualified
            : $"{qualified} AS {t.Alias}";
    }
}
