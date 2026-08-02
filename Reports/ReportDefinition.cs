namespace Fx.ControlKit.Reports;

/// <summary>
/// Defines a report's structure: SQL data source, columns, grouping, parameters, and layout.
/// Used by <see cref="ReportWriterControl"/> to render SQL-driven HTML reports
/// (Option 4: SQL → HTML/CSS → PDF).
/// </summary>
public class ReportDefinition
{
    /// <summary>Report identifier (typically the .rpt filename without extension).</summary>
    public string ReportId { get; set; } = "";

    /// <summary>Display title printed at the top of the report.</summary>
    public string Title { get; set; } = "";

    /// <summary>Horizontal alignment for <see cref="Title"/>. Defaults to Center.</summary>
    public ReportAlignment TitleAlignment { get; set; } = ReportAlignment.Center;

    /// <summary>Optional subtitle (e.g. date range, division name).</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>Horizontal alignment for <see cref="Subtitle"/>. Defaults to Center.</summary>
    public ReportAlignment SubtitleAlignment { get; set; } = ReportAlignment.Center;

    /// <summary>SQL query that produces the report data. Use @ParameterName for parameter placeholders.</summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Optional secondary SQL that selects the full group-hierarchy (all group levels,
    /// including ones hidden in the main summary view — e.g. Phase and POIndex in
    /// drill-down-style reports). When set, <see cref="ReportWriterControl"/> uses this
    /// query exclusively to build the left-side group tree. Lets the tree show every
    /// navigable level even when the main <see cref="Sql"/> returns only a compact
    /// summary view.
    /// </summary>
    public string TreeSql { get; set; } = "";

    /// <summary>Column definitions in display order.</summary>
    public List<ReportColumn> Columns { get; set; } = new();

    /// <summary>Group/subtotal definitions (applied in order).</summary>
    public List<ReportGroup> Groups { get; set; } = new();

    /// <summary>Parameter definitions (values injected at runtime).</summary>
    public List<ReportParameter> Parameters { get; set; } = new();

    /// <summary>Page orientation: Portrait or Landscape.</summary>
    public ReportOrientation Orientation { get; set; } = ReportOrientation.Portrait;

    /// <summary>Paper size.</summary>
    public ReportPaperSize PaperSize { get; set; } = ReportPaperSize.Letter;

    /// <summary>Optional footer text (left side). Supports {PageNumber}, {TotalPages}, {DateTime}, {ReportTitle}.</summary>
    public string FooterLeft { get; set; } = "{ReportTitle}";

    /// <summary>Optional footer text (center).</summary>
    public string FooterCenter { get; set; } = "";

    /// <summary>Optional footer text (right side).</summary>
    public string FooterRight { get; set; } = "Page {PageNumber}";

    /// <summary>Show column header row on each page when printing.</summary>
    public bool RepeatHeaderOnEachPage { get; set; } = true;

    /// <summary>Maximum rows per page for pagination (0 = auto based on paper size).</summary>
    public int RowsPerPage { get; set; } = 0;

    /// <summary>Source .rpt file path if this definition was parsed from a Crystal Report.</summary>
    public string? SourceRptFile { get; set; }

    /// <summary>
    /// On-demand subreport links discovered in the Crystal XML's main report layout.
    /// These are carried separately from <see cref="Columns"/> so renderers can expose
    /// subreport targets without letting nested subreport tables leak into the main SQL.
    /// </summary>
    public List<ReportSubreportLink> SubreportLinks { get; set; } = new();
}

/// <summary>Defines a single column in a report.</summary>
public class ReportColumn
{
    /// <summary>Database field name (must match SQL result column name).</summary>
    public string Field { get; set; } = "";

    /// <summary>Display header text.</summary>
    public string HeaderText { get; set; } = "";

    /// <summary>Column width in pixels (approximate — CSS may adjust).</summary>
    public int Width { get; set; } = 100;

    /// <summary>Text alignment.</summary>
    public ReportAlignment Alignment { get; set; } = ReportAlignment.Left;

    /// <summary>.NET format string (e.g. "C2" for currency, "N0" for integer, "d" for date).</summary>
    public string Format { get; set; } = "";

    /// <summary>Data type hint for formatting and alignment defaults.</summary>
    public ReportColumnType ColumnType { get; set; } = ReportColumnType.Text;

    /// <summary>If true, column is hidden from display but available for grouping/formulas.</summary>
    public bool Hidden { get; set; }

    /// <summary>If true, suppress repeated values in this column (show only on group change).</summary>
    public bool SuppressRepeats { get; set; }

    /// <summary>Optional CSS text color, usually copied from Crystal's FieldObject Color.</summary>
    public string TextColor { get; set; } = "";

    /// <summary>If true, render this column's cells using bold text.</summary>
    public bool Bold { get; set; }

    /// <summary>Optional simple row-dependent style parsed from Crystal conditional color/style formulas.</summary>
    public ReportColumnConditionalStyle? ConditionalStyle { get; set; }

    /// <summary>CSS class applied to this column's cells.</summary>
    public string CssClass { get; set; } = "";

    /// <summary>Original Crystal horizontal position in twips, used to merge non-field report objects.</summary>
    public int LayoutLeft { get; set; }

    /// <summary>True when this column is a synthetic viewer slot for a Crystal SubreportObject.</summary>
    public bool IsSubreportObject { get; set; }

    /// <summary>Optional on-demand subreport target associated with this column's header.</summary>
    public ReportSubreportLink? SubreportLink { get; set; }
}

/// <summary>Metadata for a Crystal on-demand subreport object in the main report layout.</summary>
public class ReportSubreportLink
{
    /// <summary>Crystal report-object name, e.g. <c>Subreport6</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Crystal subreport name, e.g. <c>Cost to Date</c>.</summary>
    public string SubreportName { get; set; } = "";

    /// <summary>Crystal report area kind that owns the object, e.g. <c>Detail</c>.</summary>
    public string AreaKind { get; set; } = "";

    /// <summary>Crystal section name that owns the object, e.g. <c>DetailSection1</c>.</summary>
    public string SectionName { get; set; } = "";

    /// <summary>Original Crystal horizontal position in twips.</summary>
    public int Left { get; set; }

    /// <summary>Original Crystal width in twips.</summary>
    public int Width { get; set; }

    /// <summary>Physical XML sidecar path when one could be resolved.</summary>
    public string XmlPath { get; set; } = "";

    /// <summary>Browser URL for the XML sidecar when the file lives under the host's web root.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// True when the target report is embedded inside the parent XML <c>&lt;SubReports&gt;</c>
    /// block rather than exported as a separate XML file.
    /// </summary>
    public bool IsInline { get; set; }

    /// <summary>Whether the Crystal object was authored as an on-demand subreport link.</summary>
    public bool EnableOnDemand { get; set; }

    /// <summary>Crystal-linked parameter mappings used to filter the opened subreport by the clicked row.</summary>
    public List<ReportSubreportParameterLink> ParameterLinks { get; set; } = new();
}

/// <summary>One Crystal SubReportLink mapping from a main-report row field to a subreport field.</summary>
public class ReportSubreportParameterLink
{
    /// <summary>Crystal linked parameter name, e.g. <c>Pm-tblSalesSheetMaster.Worksheet</c>.</summary>
    public string LinkedParameterName { get; set; } = "";

    /// <summary>Main report field reference as exported by Crystal, e.g. <c>{tblSalesSheetMaster.Worksheet}</c>.</summary>
    public string MainReportFieldName { get; set; } = "";

    /// <summary>Main report SQL alias selected into the row, e.g. <c>tblSalesSheetMaster_Worksheet</c>.</summary>
    public string MainReportAlias { get; set; } = "";

    /// <summary>Subreport field reference as exported by Crystal, e.g. <c>{SalesSheetCostsViewNoCS.Worksheet}</c>.</summary>
    public string SubreportFieldName { get; set; } = "";

    /// <summary>Subreport table alias used in the generated SQL filter.</summary>
    public string SubreportTable { get; set; } = "";

    /// <summary>Subreport field used in the generated SQL filter.</summary>
    public string SubreportField { get; set; } = "";
}

/// <summary>
/// Simple conditional cell style. This intentionally models the common Crystal pattern
/// <c>if {table.field}=value then crcolor/crbold else crblack/crregular</c>.
/// </summary>
public class ReportColumnConditionalStyle
{
    /// <summary>SQL result field alias used to evaluate the condition.</summary>
    public string Field { get; set; } = "";

    /// <summary>Comparison operator (=, &lt;&gt;, &gt;, &gt;=, &lt;, &lt;=).</summary>
    public string Operator { get; set; } = "=";

    /// <summary>Comparison value as exported by Crystal.</summary>
    public string Value { get; set; } = "";

    /// <summary>Text color when the condition is true.</summary>
    public string TrueTextColor { get; set; } = "";

    /// <summary>Text color when the condition is false.</summary>
    public string FalseTextColor { get; set; } = "";

    /// <summary>Bold override when the condition is true; null means no override.</summary>
    public bool? TrueBold { get; set; }

    /// <summary>Bold override when the condition is false; null means no override.</summary>
    public bool? FalseBold { get; set; }
}

/// <summary>Defines a grouping level with optional subtotals.</summary>
public class ReportGroup
{
    /// <summary>Field name to group by.</summary>
    public string Field { get; set; } = "";

    /// <summary>Sort direction within the group.</summary>
    public ReportSortDirection SortDirection { get; set; } = ReportSortDirection.Ascending;

    /// <summary>Show group header row.</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>Show group footer/subtotal row.</summary>
    public bool ShowFooter { get; set; } = true;

    /// <summary>Optional header format string. Supports <c>{Value}</c>,
    /// <c>{Count}</c>, and <c>{Description}</c> placeholders. The
    /// <c>{Description}</c> placeholder resolves to the row's
    /// <see cref="DescriptionField"/> value, or "" if no description is
    /// configured for this group.</summary>
    public string HeaderFormat { get; set; } = "{Value}";

    /// <summary>SQL-safe alias of a column carrying this group's description
    /// (e.g. "PREPARATION" alongside the "03000" code). Sourced from
    /// FieldObjects placed in the matching <c>&lt;Area Kind="GroupHeader"&gt;</c>
    /// section in the Crystal XML — typically a second alias of the
    /// lookup table joined on the group's code field
    /// (Job Budget Report v1's <c>tblEstPhases_1.Description</c> joined on
    /// <c>tblEstPhases.GroupPhaseValue</c> is the canonical example).
    /// Empty when the group has no description column.</summary>
    public string DescriptionField { get; set; } = "";

    /// <summary>Aggregate columns for subtotals in the group footer.</summary>
    public List<ReportAggregate> Aggregates { get; set; } = new();

    /// <summary>Start each group on a new page.</summary>
    public bool PageBreakBefore { get; set; }
}

/// <summary>Defines an aggregate (SUM, COUNT, AVG, etc.) for group footers and grand totals.</summary>
public class ReportAggregate
{
    /// <summary>Target column field name.</summary>
    public string Field { get; set; } = "";

    /// <summary>Aggregate function.</summary>
    public ReportAggregateType AggregateType { get; set; } = ReportAggregateType.Sum;

    /// <summary>Display format for the result.</summary>
    public string Format { get; set; } = "";

    /// <summary>Label prefix (e.g. "Total:", "Avg:").</summary>
    public string Label { get; set; } = "";
}

/// <summary>Defines a report parameter.</summary>
public class ReportParameter
{
    /// <summary>Parameter name (matches @Name in SQL and {?Name} in Crystal Reports).</summary>
    public string Name { get; set; } = "";

    /// <summary>Original parameter name from Crystal XML, including spaces and "(optional)" hints.
    /// Used as the right-aligned secondary label in the parameter dialog (matches the
    /// Crystal native dialog's panel title).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Display prompt text.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Default value.</summary>
    public string DefaultValue { get; set; } = "";

    /// <summary>Data type.</summary>
    public ReportParameterType ParameterType { get; set; } = ReportParameterType.String;

    /// <summary>If true, parameter is required before the report can run.</summary>
    public bool Required { get; set; } = true;

    /// <summary>If true, the parameter is allowed to be blank — used for Crystal "(optional)"
    /// parameters where empty value is treated as "no filter" (e.g. <c>Job like '' + '*'</c>
    /// matches all jobs). The dialog skips required-empty validation for these.</summary>
    public bool IsOptional { get; set; }

    /// <summary>If true, parameter supports multiple comma-separated values.</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>
    /// Crystal's <c>AllowCustomCurrentValues="True"</c> attribute. When true,
    /// the user is allowed to type ad-hoc values in addition to whatever
    /// <see cref="DefaultValues"/> / <see cref="PickListSql"/> exposes —
    /// Crystal renders an "Enter a Value:" text input + an Add button below
    /// the Available pane of the dual-list-box for these parameters.
    /// </summary>
    public bool AllowCustomCurrentValues { get; set; }

    /// <summary>Pre-defined description+value choices from Crystal's
    /// <c>&lt;ParameterDefaultValues&gt;</c> block. When 2+ entries exist the parameter
    /// dialog renders a dropdown (e.g. "Show PO Items"/"Yes", "Hide PO Items"/"No").</summary>
    public IReadOnlyList<ReportParameterChoice> DefaultValues { get; set; } = Array.Empty<ReportParameterChoice>();

    /// <summary>
    /// Optional SQL query that populates the parameter's pick-list when the
    /// parameter is rendered as a dual-list-box in the parameter dialog.
    ///
    /// <para>
    /// Two sources fill this in:
    /// <list type="bullet">
    ///   <item><description>
    ///   <b>Auto-derived from binding</b> — the default. When
    ///   <see cref="BindingTable"/>/<see cref="BindingField"/> are populated
    ///   from the Crystal RecordSelectionFormula, the loader synthesizes
    ///   <c>SELECT DISTINCT BindingField [ , BindingDescriptionField ] FROM
    ///   BindingTable WHERE [DivisionID=@DivisionID]</c>. No host config
    ///   needed — picklist follows the Crystal XML wherever the report
    ///   filters on <c>{T.F} = {?Param}</c>.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Explicit override</b> — a non-Crystal-native
    ///   <c>&lt;PickListSql&gt;</c> child element under
    ///   <c>&lt;ParameterFieldDefinition&gt;</c> in the XML wins over the
    ///   auto-derived form. Useful for parameters that need a wider/narrower
    ///   list than the binding produces.
    ///   </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Query is expected to return either one column (used as both key and
    /// label) or two columns named <c>Value</c>/<c>Description</c> (or
    /// <c>Key</c>/<c>Label</c>; case-insensitive).
    /// </para>
    ///
    /// <para>
    /// May reference auto-inject session parameters (<c>@DivisionID</c>
    /// etc.). The host's <see cref="IReportPickListProvider"/> binds those
    /// before execution.
    /// </para>
    /// </summary>
    public string? PickListSql { get; set; }

    /// <summary>
    /// Table the parameter is bound to in the Crystal RecordSelectionFormula
    /// (e.g. <c>"AssemblyCostsAllCommunities"</c> when the formula says
    /// <c>{AssemblyCostsAllCommunities.Assembly} = {?Model}</c>). Populated
    /// by <c>CrystalXmlReportLoader</c>. Empty when no equality/IN binding
    /// against this parameter was found.
    /// </summary>
    public string BindingTable { get; set; } = "";

    /// <summary>
    /// Column the parameter is bound to in the Crystal RecordSelectionFormula
    /// (e.g. <c>"Assembly"</c>). Populated alongside
    /// <see cref="BindingTable"/>.
    /// </summary>
    public string BindingField { get; set; } = "";

    /// <summary>
    /// Description column paired with <see cref="BindingField"/> for the
    /// auto-generated picklist (e.g. <c>"AssemblyDesc"</c> for Assembly).
    /// Derived heuristically by <c>CrystalXmlReportLoader</c> from the
    /// XML's <c>&lt;Fields&gt;</c> metadata: tries <c>{F}Desc</c> first,
    /// then prefix-stripping variants, then plain <c>Description</c>.
    /// Empty when no matching description column was found.
    /// </summary>
    public string BindingDescriptionField { get; set; } = "";
}

/// <summary>A single (description, value) choice from a Crystal parameter's default-values list.</summary>
public sealed record ReportParameterChoice(string Description, string Value);

// === Enums ===

public enum ReportOrientation { Portrait, Landscape }

public enum ReportPaperSize { Letter, Legal, A4, Tabloid }

public enum ReportAlignment { Left, Center, Right }

public enum ReportColumnType { Text, Integer, Decimal, Currency, Date, DateTime, Boolean, Percent }

public enum ReportSortDirection { Ascending, Descending }

public enum ReportAggregateType { Sum, Count, Average, Min, Max, Percent }

public enum ReportParameterType { String, Integer, Decimal, Date, Boolean }
