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

public sealed class ReportTableColumn
{
    public string Field { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Format { get; set; } = "";
    public int WidthTwips { get; set; } = 1800;
    public ReportTableColumn Clone() => (ReportTableColumn)MemberwiseClone();
}

/// <summary>Bound analytical report item. References use the report's Crystal field syntax.</summary>
public sealed partial class ReportAnalysisDefinition
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
    public bool EachRecord { get; set; }
    /// <summary>No category group: each measure is one bar or pie slice, aggregated over the chart's rows.</summary>
    public bool SummariesAsCategories { get; set; }
    /// <summary>The chart record points at a cross-tab. The converter copies that cross-tab's fields before the chart is drawn.</summary>
    internal bool LinkedToCrossTab { get; set; }
    public bool SortCategories { get; set; }
    public List<string> DescendingFields { get; set; } = [];
    /// <summary>Axis fields whose Crystal direction is original or specified order, so categories stay in record order.</summary>
    public List<string> OriginalOrderFields { get; set; } = [];
    /// <summary>Bindings that were read but not evaluated (linked chart, OLAP, calculated member, per-measure transform). Conversion records them; the drawn values are the summaries that were imported.</summary>
    internal List<string> BindingNotes { get; } = [];
    /// <summary>Crystal group condition for an axis field (1 daily … 7 annually, 8–11 time-of-day). 0, or a missing entry, groups every distinct value.</summary>
    public Dictionary<string, int> GroupKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ReportMatrixCell> Cells { get; set; } = [];
    public List<ReportTableColumn> TableColumns { get; set; } = [];
    public bool RepeatHeaders { get; set; } = true;
    public int RowHeaderWidthTwips { get; set; } = 1800;
    public int ValueColumnWidthTwips { get; set; } = 1200;
    public int RowHeightTwips { get; set; } = 360;
    private IEnumerable<string> LocalReferences => RowFields.Concat(ColumnFields).Concat(Measures.Select(m => m.Field))
        .Concat(TableColumns.Select(column => column.Field))
        .Append(CategoryField).Append(SeriesField).Append(GroupScope).Where(reference => !string.IsNullOrWhiteSpace(reference)).Distinct(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> References => Tree().SelectMany(node => node.LocalReferences).Distinct(StringComparer.OrdinalIgnoreCase);

    public string? Validate(string kind)
    {
        try { _ = Tree().ToArray(); } catch (InvalidDataException error) { return error.Message; }
        return ValidateNode(kind);
    }

    private string? ValidateNode(string kind)
    {
        foreach (var cell in Cells)
        {
            if (cell.Row < 0 || cell.Column < 0 || cell.RowSpan is < 1 or > 10000 || cell.ColumnSpan is < 1 or > 10000)
                return "Cell positions and spans must be positive and bounded.";
            if (cell.Region is not null && (cell.RegionKind is not ("Table" or "CrossTab") || cell.Region.GroupScope.Length > 0))
                return "Nested cells use Table or CrossTab regions and inherit their cell's data scope.";
            if (cell.Region?.ValidateNode(cell.RegionKind) is { } nestedError) return nestedError;
        }
        if (RowHeaderWidthTwips is < 360 or > 100000 || ValueColumnWidthTwips is < 360 or > 100000 || RowHeightTwips is < 180 or > 100000)
            return "Table dimensions are outside the supported bounds.";
        if (kind == "Table") return TableColumns.Count is < 1 or > 64 || TableColumns.Any(c => string.IsNullOrWhiteSpace(c.Field) || c.WidthTwips is < 360 or > 100000)
            ? "Choose 1 to 64 table columns with fields and widths of at least 360 twips." : null;
        if (Measures.Count == 0 || Measures.Any(m => string.IsNullOrWhiteSpace(m.Field))) return "Choose at least one measure field.";
        if (Measures.Any(m => m.Aggregate is not (ReportAggregateType.Sum or ReportAggregateType.Count or ReportAggregateType.Average or ReportAggregateType.Min or ReportAggregateType.Max or ReportAggregateType.DistinctCount or ReportAggregateType.Median)))
            return "This item supports Sum, Count, DistinctCount, Average, Median, Min and Max. Percentage summaries need a denominator.";
        if (kind == "Chart" && !EachRecord && !SummariesAsCategories && string.IsNullOrWhiteSpace(CategoryField)) return "Choose a category field.";
        if (GroupKinds.Values.Any(groupKind => groupKind is < 0 or > 11)) return "Analytical group periods must be a Crystal date or time condition.";
        if (kind == "Chart" && ChartType is not (ChartType.Bar or ChartType.HorizontalBar or ChartType.StackedBar or ChartType.StackedBar100 or ChartType.Line or ChartType.Area or ChartType.StackedArea or ChartType.Pie or ChartType.Donut)) return "This report adapter supports bar, stacked/percent bar, line, area, pie and donut charts.";
        if (kind == "Chart" && ChartType is ChartType.Pie or ChartType.Donut && !SummariesAsCategories && (Measures.Count != 1 || SeriesField.Length > 0)) return "Pie charts require one measure and no series field.";
        if (kind == "Chart" && EachRecord && SeriesField.Length > 0) return "Each-record charts cannot also aggregate a series grouping.";
        if (kind == "CrossTab" && (RowFields.Count == 0 && ColumnFields.Count == 0 || RowFields.Any(string.IsNullOrWhiteSpace) || ColumnFields.Any(string.IsNullOrWhiteSpace))) return "Choose the cross-tab row fields.";
        if (kind == "CrossTab" && (RowFields.Count > 3 || ColumnFields.Count > 3)) return "The current pivot adapter supports up to three row and column levels.";
        return null;
    }

    public ReportAnalysisDefinition Clone()
    {
        _ = Tree().ToArray();
        return CloneNode();
    }
    private ReportAnalysisDefinition CloneNode() => new()
    {
        Title = Title, ChartType = ChartType, CategoryField = CategoryField, SeriesField = SeriesField, GroupScope = GroupScope,
        RowFields = [.. RowFields], ColumnFields = [.. ColumnFields], Measures = Measures.Select(m => m.Clone()).ToList(), ShowLegend = ShowLegend, ShowTotals = ShowTotals,
        EachRecord = EachRecord, SummariesAsCategories = SummariesAsCategories, SortCategories = SortCategories, DescendingFields = [.. DescendingFields], OriginalOrderFields = [.. OriginalOrderFields],
        GroupKinds = new Dictionary<string, int>(GroupKinds, StringComparer.OrdinalIgnoreCase),
        Cells = Cells.Select(cell => cell.Copy(cell.Region?.CloneNode())).ToList(),
        TableColumns = TableColumns.Select(column => column.Clone()).ToList(), RepeatHeaders = RepeatHeaders,
        RowHeaderWidthTwips = RowHeaderWidthTwips, ValueColumnWidthTwips = ValueColumnWidthTwips, RowHeightTwips = RowHeightTwips
    };

    internal XElement ToXml()
    {
        _ = Tree().ToArray();
        return ToXmlNode();
    }
    private XElement ToXmlNode() => new("FlexKitAnalysis", new XAttribute("Title", Title), new XAttribute("ChartType", ChartType),
        new XAttribute("CategoryField", CategoryField), new XAttribute("SeriesField", SeriesField), new XAttribute("GroupScope", GroupScope),
        new XAttribute("ShowLegend", ShowLegend), new XAttribute("ShowTotals", ShowTotals),
        new XAttribute("EachRecord", EachRecord), new XAttribute("SummariesAsCategories", SummariesAsCategories), new XAttribute("SortCategories", SortCategories), new XElement("DescendingFields", DescendingFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("OriginalOrderFields", OriginalOrderFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("GroupKinds", GroupKinds.Select(pair => new XElement("Field", new XAttribute("Binding", pair.Key), new XAttribute("Kind", pair.Value)))),
        new XElement("Cells", Cells.Select(cell => new XElement("Cell", new XAttribute("Row", cell.Row), new XAttribute("Column", cell.Column),
            new XAttribute("RowSpan", cell.RowSpan), new XAttribute("ColumnSpan", cell.ColumnSpan), new XAttribute("RegionKind", cell.RegionKind),
            cell.Text is null ? null : new XElement("Text", cell.Text), cell.Region?.ToXmlNode()))),
        new XAttribute("RepeatHeaders", RepeatHeaders), new XAttribute("RowHeaderWidthTwips", RowHeaderWidthTwips),
        new XAttribute("ValueColumnWidthTwips", ValueColumnWidthTwips), new XAttribute("RowHeightTwips", RowHeightTwips),
        new XElement("TableColumns", TableColumns.Select(column => new XElement("Column", new XAttribute("Field", column.Field),
            new XAttribute("Caption", column.Caption), new XAttribute("Format", column.Format), new XAttribute("WidthTwips", column.WidthTwips)))),
        new XElement("Rows", RowFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Columns", ColumnFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Measures", Measures.Select(m => new XElement("Measure", new XAttribute("Field", m.Field), new XAttribute("Caption", m.Caption),
            new XAttribute("Aggregate", m.Aggregate), new XAttribute("Format", m.Format)))));

    internal static ReportAnalysisDefinition? Read(XElement source)
    {
        if (source.Element("FlexKitAnalysis") is not { } xml) return null;
        if (xml.DescendantsAndSelf("FlexKitAnalysis").Count() > 4096 || xml.Ancestors("FlexKitAnalysis").Count() >= 16)
            throw new InvalidDataException("Nested report regions exceed the 16-level/4096-region safety limit.");
        return new()
        {
            Title = (string?)xml.Attribute("Title") ?? "",
            ChartType = Enum.TryParse<ChartType>((string?)xml.Attribute("ChartType"), out var chart) ? chart : (ChartType)(-1),
            CategoryField = (string?)xml.Attribute("CategoryField") ?? "", SeriesField = (string?)xml.Attribute("SeriesField") ?? "",
            GroupScope = (string?)xml.Attribute("GroupScope") ?? "",
            ShowLegend = !bool.TryParse((string?)xml.Attribute("ShowLegend"), out var legend) || legend,
            ShowTotals = !bool.TryParse((string?)xml.Attribute("ShowTotals"), out var totals) || totals,
            EachRecord = bool.TryParse((string?)xml.Attribute("EachRecord"), out var eachRecord) && eachRecord,
            SummariesAsCategories = bool.TryParse((string?)xml.Attribute("SummariesAsCategories"), out var summariesAsCategories) && summariesAsCategories,
            SortCategories = bool.TryParse((string?)xml.Attribute("SortCategories"), out var sortCategories) && sortCategories,
            DescendingFields = xml.Element("DescendingFields")?.Elements("Field").Select(field => (string?)field.Attribute("Binding") ?? "").ToList() ?? [],
            OriginalOrderFields = xml.Element("OriginalOrderFields")?.Elements("Field").Select(field => (string?)field.Attribute("Binding") ?? "").Where(field => field.Length > 0).ToList() ?? [],
            GroupKinds = xml.Element("GroupKinds")?.Elements("Field").Select(field => ((string?)field.Attribute("Binding") ?? "", (int?)field.Attribute("Kind") ?? 0))
                .Where(pair => pair.Item1.Length > 0).GroupBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Item2, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase),
            Cells = xml.Element("Cells")?.Elements("Cell").Select(cell => new ReportMatrixCell
            {
                Row = (int?)cell.Attribute("Row") ?? 0, Column = (int?)cell.Attribute("Column") ?? 0,
                RowSpan = (int?)cell.Attribute("RowSpan") ?? 1, ColumnSpan = (int?)cell.Attribute("ColumnSpan") ?? 1,
                Text = (string?)cell.Element("Text"), RegionKind = (string?)cell.Attribute("RegionKind") ?? "Table", Region = Read(cell)
            }).ToList() ?? [],
            RepeatHeaders = !bool.TryParse((string?)xml.Attribute("RepeatHeaders"), out var repeat) || repeat,
            RowHeaderWidthTwips = (int?)xml.Attribute("RowHeaderWidthTwips") ?? 1800,
            ValueColumnWidthTwips = (int?)xml.Attribute("ValueColumnWidthTwips") ?? 1200,
            RowHeightTwips = (int?)xml.Attribute("RowHeightTwips") ?? 360,
            TableColumns = xml.Element("TableColumns")?.Elements("Column").Select(column => new ReportTableColumn
            {
                Field = (string?)column.Attribute("Field") ?? "", Caption = (string?)column.Attribute("Caption") ?? "",
                Format = (string?)column.Attribute("Format") ?? "", WidthTwips = (int?)column.Attribute("WidthTwips") ?? 1800
            }).ToList() ?? [],
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

public sealed record ReportAnalysisSnapshot(ReportAnalysisDefinition Definition, IReadOnlyList<Dictionary<string, object>> Rows)
{
    public ReportTabularData? Table { get; init; }
}
