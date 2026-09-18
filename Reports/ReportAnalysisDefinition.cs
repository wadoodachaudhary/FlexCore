using System.Globalization;
using System.Xml.Linq;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

public sealed class ReportAnalysisMeasure
{
    public string Field { get; set; } = "";
    public string Caption { get; set; } = "";
    public ReportAggregateType Aggregate { get; set; } = ReportAggregateType.Sum;
    public string Format { get; set; } = "N2";
    public ReportAnalysisMeasure Clone() => (ReportAnalysisMeasure)MemberwiseClone();
}

/// <summary>Bound analytical report item. References use the report's Crystal field syntax.</summary>
public sealed class ReportAnalysisDefinition
{
    public string Title { get; set; } = "";
    public ChartType ChartType { get; set; } = ChartType.Bar;
    public string CategoryField { get; set; } = "";
    public string SeriesField { get; set; } = "";
    public string GroupScope { get; set; } = "";
    public List<string> RowFields { get; set; } = [];
    public List<string> ColumnFields { get; set; } = [];
    public List<ReportAnalysisMeasure> Measures { get; set; } = [];
    public bool ShowLegend { get; set; } = true;
    public bool ShowTotals { get; set; } = true;
    public IEnumerable<string> References => RowFields.Concat(ColumnFields).Concat(Measures.Select(m => m.Field))
        .Append(CategoryField).Append(SeriesField).Append(GroupScope).Where(reference => !string.IsNullOrWhiteSpace(reference)).Distinct(StringComparer.OrdinalIgnoreCase);

    public string? Validate(string kind)
    {
        if (Measures.Count == 0 || Measures.Any(m => string.IsNullOrWhiteSpace(m.Field))) return "Choose at least one measure field.";
        if (Measures.Any(m => m.Aggregate is not (ReportAggregateType.Sum or ReportAggregateType.Count or ReportAggregateType.Average or ReportAggregateType.Min or ReportAggregateType.Max)))
            return "This item supports Sum, Count, Average, Min and Max. Percentage summaries need a denominator.";
        if (kind == "Chart" && string.IsNullOrWhiteSpace(CategoryField)) return "Choose a category field.";
        if (kind == "Chart" && ChartType is not (ChartType.Bar or ChartType.Line or ChartType.Pie)) return "This report adapter supports bar, line and pie charts.";
        if (kind == "Chart" && ChartType == ChartType.Pie && (Measures.Count != 1 || SeriesField.Length > 0)) return "Pie charts require one measure and no series field.";
        if (kind == "CrossTab" && (RowFields.Count == 0 || RowFields.Any(string.IsNullOrWhiteSpace) || ColumnFields.Any(string.IsNullOrWhiteSpace))) return "Choose the cross-tab row fields.";
        if (kind == "CrossTab" && (RowFields.Count > 3 || ColumnFields.Count > 3)) return "The current pivot adapter supports up to three row and column levels.";
        return null;
    }

    public ReportAnalysisDefinition Clone() => new()
    {
        Title = Title, ChartType = ChartType, CategoryField = CategoryField, SeriesField = SeriesField, GroupScope = GroupScope,
        RowFields = [.. RowFields], ColumnFields = [.. ColumnFields], Measures = Measures.Select(m => m.Clone()).ToList(), ShowLegend = ShowLegend, ShowTotals = ShowTotals
    };

    internal XElement ToXml() => new("FlexKitAnalysis", new XAttribute("Title", Title), new XAttribute("ChartType", ChartType),
        new XAttribute("CategoryField", CategoryField), new XAttribute("SeriesField", SeriesField), new XAttribute("GroupScope", GroupScope),
        new XAttribute("ShowLegend", ShowLegend), new XAttribute("ShowTotals", ShowTotals),
        new XElement("Rows", RowFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Columns", ColumnFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Measures", Measures.Select(m => new XElement("Measure", new XAttribute("Field", m.Field), new XAttribute("Caption", m.Caption),
            new XAttribute("Aggregate", m.Aggregate), new XAttribute("Format", m.Format)))));

    internal static ReportAnalysisDefinition? Read(XElement source)
    {
        if (source.Element("FlexKitAnalysis") is not { } xml) return null;
        return new()
        {
            Title = (string?)xml.Attribute("Title") ?? "",
            ChartType = Enum.TryParse<ChartType>((string?)xml.Attribute("ChartType"), out var chart) ? chart : (ChartType)(-1),
            CategoryField = (string?)xml.Attribute("CategoryField") ?? "", SeriesField = (string?)xml.Attribute("SeriesField") ?? "",
            GroupScope = (string?)xml.Attribute("GroupScope") ?? "",
            ShowLegend = !bool.TryParse((string?)xml.Attribute("ShowLegend"), out var legend) || legend,
            ShowTotals = !bool.TryParse((string?)xml.Attribute("ShowTotals"), out var totals) || totals,
            RowFields = xml.Element("Rows")?.Elements("Field").Select(field => (string?)field.Attribute("Binding") ?? "").ToList() ?? [],
            ColumnFields = xml.Element("Columns")?.Elements("Field").Select(field => (string?)field.Attribute("Binding") ?? "").ToList() ?? [],
            Measures = xml.Element("Measures")?.Elements("Measure").Select(m => new ReportAnalysisMeasure
            {
                Field = (string?)m.Attribute("Field") ?? "", Caption = (string?)m.Attribute("Caption") ?? "",
                Aggregate = Enum.TryParse<ReportAggregateType>((string?)m.Attribute("Aggregate"), out var operation) ? operation : (ReportAggregateType)(-1),
                Format = (string?)m.Attribute("Format") ?? "N2"
            }).ToList() ?? []
        };
    }
}

public sealed record ReportAnalysisSnapshot(ReportAnalysisDefinition Definition, IReadOnlyList<Dictionary<string, object>> Rows);
