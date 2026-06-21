namespace Fx.ControlKit.Reports;

public class ReportDefinition
{
    public string ReportId { get; set; } = "";

    public string Title { get; set; } = "";

    public ReportAlignment TitleAlignment { get; set; } = ReportAlignment.Center;

    public string Subtitle { get; set; } = "";

    public ReportAlignment SubtitleAlignment { get; set; } = ReportAlignment.Center;

    public string Sql { get; set; } = "";

    public string TreeSql { get; set; } = "";

    public List<ReportColumn> Columns { get; set; } = new();

    public List<ReportGroup> Groups { get; set; } = new();

    public List<ReportParameter> Parameters { get; set; } = new();

    public ReportOrientation Orientation { get; set; } = ReportOrientation.Portrait;

    public ReportPaperSize PaperSize { get; set; } = ReportPaperSize.Letter;

    public string FooterLeft { get; set; } = "{ReportTitle}";

    public string FooterCenter { get; set; } = "";

    public string FooterRight { get; set; } = "Page {PageNumber}";

    public bool RepeatHeaderOnEachPage { get; set; } = true;

    public int RowsPerPage { get; set; } = 0;

    public string? SourceRptFile { get; set; }

    public List<ReportSubreportLink> SubreportLinks { get; set; } = new();
}

public class ReportColumn
{
    public string Field { get; set; } = "";

    public string HeaderText { get; set; } = "";

    public int Width { get; set; } = 100;

    public ReportAlignment Alignment { get; set; } = ReportAlignment.Left;

    public string Format { get; set; } = "";

    public ReportColumnType ColumnType { get; set; } = ReportColumnType.Text;

    public bool Hidden { get; set; }

    public bool SuppressRepeats { get; set; }

    public string TextColor { get; set; } = "";

    public bool Bold { get; set; }

    public ReportColumnConditionalStyle? ConditionalStyle { get; set; }

    public string CssClass { get; set; } = "";

    public int LayoutLeft { get; set; }

    public bool IsSubreportObject { get; set; }

    public ReportSubreportLink? SubreportLink { get; set; }
}

public class ReportSubreportLink
{
    public string Name { get; set; } = "";

    public string SubreportName { get; set; } = "";

    public string AreaKind { get; set; } = "";

    public string SectionName { get; set; } = "";

    public int Left { get; set; }

    public int Width { get; set; }

    public string XmlPath { get; set; } = "";

    public string Url { get; set; } = "";

    public bool IsInline { get; set; }

    public bool EnableOnDemand { get; set; }

    public List<ReportSubreportParameterLink> ParameterLinks { get; set; } = new();
}

public class ReportSubreportParameterLink
{
    public string LinkedParameterName { get; set; } = "";

    public string MainReportFieldName { get; set; } = "";

    public string MainReportAlias { get; set; } = "";

    public string SubreportFieldName { get; set; } = "";

    public string SubreportTable { get; set; } = "";

    public string SubreportField { get; set; } = "";
}

public class ReportColumnConditionalStyle
{
    public string Field { get; set; } = "";

    public string Operator { get; set; } = "=";

    public string Value { get; set; } = "";

    public string TrueTextColor { get; set; } = "";

    public string FalseTextColor { get; set; } = "";

    public bool? TrueBold { get; set; }

    public bool? FalseBold { get; set; }
}

public class ReportGroup
{
    public string Field { get; set; } = "";

    public ReportSortDirection SortDirection { get; set; } = ReportSortDirection.Ascending;

    public bool ShowHeader { get; set; } = true;

    public bool ShowFooter { get; set; } = true;

    public string HeaderFormat { get; set; } = "{Value}";

    public string DescriptionField { get; set; } = "";

    public List<ReportAggregate> Aggregates { get; set; } = new();

    public bool PageBreakBefore { get; set; }
}

public class ReportAggregate
{
    public string Field { get; set; } = "";

    public ReportAggregateType AggregateType { get; set; } = ReportAggregateType.Sum;

    public string Format { get; set; } = "";

    public string Label { get; set; } = "";
}

public class ReportParameter
{
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Prompt { get; set; } = "";

    public string DefaultValue { get; set; } = "";

    public ReportParameterType ParameterType { get; set; } = ReportParameterType.String;

    public bool Required { get; set; } = true;

    public bool IsOptional { get; set; }

    public bool AllowMultiple { get; set; }

    public bool AllowCustomCurrentValues { get; set; }

    public IReadOnlyList<ReportParameterChoice> DefaultValues { get; set; } = Array.Empty<ReportParameterChoice>();

    public string? PickListSql { get; set; }

    public string BindingTable { get; set; } = "";

    public string BindingField { get; set; } = "";

    public string BindingDescriptionField { get; set; } = "";
}

public sealed record ReportParameterChoice(string Description, string Value);

public enum ReportOrientation { Portrait, Landscape }

public enum ReportPaperSize { Letter, Legal, A4, Tabloid }

public enum ReportAlignment { Left, Center, Right }

public enum ReportColumnType { Text, Integer, Decimal, Currency, Date, DateTime, Boolean, Percent }

public enum ReportSortDirection { Ascending, Descending }

public enum ReportAggregateType { Sum, Count, Average, Min, Max, Percent }

public enum ReportParameterType { String, Integer, Decimal, Date, Boolean }
