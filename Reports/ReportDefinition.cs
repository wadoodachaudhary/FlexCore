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

    /// <summary>CSS class applied to this column's cells.</summary>
    public string CssClass { get; set; } = "";
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

    /// <summary>Optional header format string. Supports {Value} and {Count} placeholders.</summary>
    public string HeaderFormat { get; set; } = "{Value}";

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

    /// <summary>Pre-defined description+value choices from Crystal's
    /// <c>&lt;ParameterDefaultValues&gt;</c> block. When 2+ entries exist the parameter
    /// dialog renders a dropdown (e.g. "Show PO Items"/"Yes", "Hide PO Items"/"No").</summary>
    public IReadOnlyList<ReportParameterChoice> DefaultValues { get; set; } = Array.Empty<ReportParameterChoice>();
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
