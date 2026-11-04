using System.Globalization;
using System.Xml.Linq;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports;

public sealed class ReportAnalysisMeasure
{
    public string Field { get; set; } = "";
    public string Caption { get; set; } = "";
    public ReportAggregateType Aggregate { get; set; } = ReportAggregateType.Sum;
    public string WeightField { get; set; } = "";
    public string Format { get; set; } = "N2";
    /// <summary>Use the formula value once per category instead of aggregating repeated copies.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DoNotSummarize { get; set; }
    public ReportAnalysisMeasure Clone() => (ReportAnalysisMeasure)MemberwiseClone();
}

public sealed record ReportChartPrintAxis(double Minimum, double Maximum, int Intervals, string Format, string CategoryTitle, string ValueTitle, bool Grid)
{
    public ReportPlotFrame? Frame { get; init; }
}
public sealed record ReportPlotFrame(double Left,double Top,double Right,double Bottom)
{
    public bool IsValid => Left >= 0 && Left < Right && Right <= 1 && Top >= 0 && Top < Bottom && Bottom <= 1;
}

public sealed record ReportChartRightLegend(string CategoryTitle, string ValueTitle, bool CategoryGrid, bool ValueGrid)
{
    public bool RotateCategories { get; init; }
    public ReportBarDrawing? Drawing { get; init; }
}

// Colors and plot bounds from the saved chart drawing. A vertical wash is shared by a single stacked series.
public sealed record ReportBarDrawing(string Palette, string Background, string? GradientTop, string? GradientBottom, string? Border, ReportPlotFrame? Frame);

public sealed record ReportRowRanking(int Count, bool Bottom, bool WithTies);

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
    public string AccumulatorVariable { get; set; } = "";
    public ReportRowRanking? RowRanking { get; set; }
    public int AccumulatorCount { get; set; }
    public bool AccumulatorWithTies { get; set; }
    public string Title { get; set; } = "";
    public ChartType ChartType { get; set; } = ChartType.Bar;
    /// <summary>Render bar, stacked bar and percentage bar charts with perspective faces.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ReportChartPrintAxis? PrintAxis { get; set; }
    public bool PerspectivePie { get; set; }
    public bool ChartThreeDimensional { get; set; }
    /// <summary>Use the classic flat-bar presentation with a category legend.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ChartCategoryLegend { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportChartRightLegend? RightCategoryLegend { get; set; }
    public string CategoryField { get; set; } = "";
    /// <summary>Optional numeric category caption format, independent of the plotted values.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CategoryFormat { get; set; }
    public string SeriesField { get; set; } = "";
    public string GroupScope { get; set; } = "";
    public List<string> RowFields { get; set; } = [];
    public List<string> ColumnFields { get; set; } = [];
    public List<ReportAnalysisMeasure> Measures { get; set; } = [];
    public bool ShowLegend { get; set; } = true;
    public bool ShowTotals { get; set; } = true;
    public bool EachRecord { get; set; }
    public bool ReportGroupChart { get; set; }
    /// <summary>No category group: each measure is one bar or pie slice, aggregated over the chart's rows.</summary>
    public bool SummariesAsCategories { get; set; }
    public bool SortCategories { get; set; }
    public List<string> DescendingFields { get; set; } = [];
    public List<ReportAnalysisGroup> Groups { get; set; } = [];
    /// <summary>Axis fields whose Crystal direction is original or specified order, so categories stay in record order.</summary>
    public List<string> OriginalOrderFields { get; set; } = [];
    /// <summary>Crystal group condition for a chart axis field. On a date or date-time: 1 weekly, 2 biweekly, 3 semimonthly, 4 monthly, 5 quarterly,
    /// 6 semiannually, 7 annually, 8–11 by second, minute, hour or AM/PM; on a time: 1–3 by minute, hour or AM/PM. 0, or a missing entry, keeps each value.</summary>
    public Dictionary<string, int> GroupKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ReportMatrixCell> Cells { get; set; } = [];
    public List<ReportTableColumn> TableColumns { get; set; } = [];
    public bool RepeatHeaders { get; set; } = true;
    public ReportNativeCrossTabLayout? NativeCrossTab { get; set; }
    public List<ReportCalculatedMember> CalculatedMembers { get; set; } = [];
    public int RowHeaderWidthTwips { get; set; } = 1800;
    public int ValueColumnWidthTwips { get; set; } = 1200;
    public int RowHeightTwips { get; set; } = 360;
    private IEnumerable<string> LocalReferences => RowFields.Concat(ColumnFields).Concat(Measures.SelectMany(m => new[]{m.Field,m.WeightField}))
        .Concat(CalculatedMembers.SelectMany(member => member.Formulas).SelectMany(formula => formula.Compile().References))
        .Concat(NativeCrossTab?.Templates.SelectMany(template => template.Style.Conditions).SelectMany(condition => condition.Formula.Compile().References) ?? [])
        .Concat(Groups.SelectMany(group => group.SpecifiedGroups).SelectMany(group => CrystalFormula.Compile(group.Expression).References))
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
        if (PrintAxis is {} axis && (!double.IsFinite(axis.Minimum) || !double.IsFinite(axis.Maximum) || axis.Maximum<=axis.Minimum || axis.Intervals is <1 or >64)) return "Invalid chart axis bounds.";
        if (ChartCategoryLegend && (kind != "Chart" || ChartThreeDimensional || !PrintedCategoryBarChart.Supports(ChartType)))
            return "Category legends require a flat bar chart.";
        if (ChartThreeDimensional && (kind != "Chart" || !PrintedBarChart3D.Supports(ChartType)))
            return "3D rendering is available for bar charts.";
        if (NativeCrossTab is { } native && (native.Templates.Count > 16384 || native.Templates.Any(template =>
            template.Axis is not (207 or 211 or 216) || template.RowLevel < 0 || template.RowLevel > RowFields.Count
            || template.ColumnLevel < 0 || template.ColumnLevel > ColumnFields.Count || template.Summary < 0 || template.Summary >= Measures.Count
            || template.Width is <= 0 or > 100000 || template.Height is <= 0 or > 100000
            || template.CellWidth is < 0 or > 100000 || template.CellHeight is < 0 or > 100000
            || template.PaddingX is < 0 or > 10000 || template.PaddingY is < 0 or > 10000 || template.Style.Conditions.Count > 64)))
            return "Native cross-tab cell templates exceed their supported bounds.";
        if (RowRanking is { } rank && (kind != "CrossTab" || rank.Count <= 0 || RowFields.Count != 1 || ColumnFields.Count != 0
            || NativeCrossTab?.Appearance is null || Measures.Count != 1 || Measures[0].Aggregate != ReportAggregateType.Sum
            || Cells.Count != 0 || CalculatedMembers.Count != 0 || AccumulatorVariable.Length > 0))
            return "Ranked rows require a native row-only grid with one sum.";
        if (AccumulatorVariable.Length > 0 && (kind != "CrossTab" || RowFields.Count != 1 || ColumnFields.Count != 0
            || Measures.Count != 1 || Measures[0].Aggregate != ReportAggregateType.Sum || AccumulatorCount <= 0))
            return "A ranked accumulator requires one row group, one sum and a positive group count.";
        if (CalculatedMembers.Count > 0)
        {
            if (kind != "CrossTab" || RowFields.Count == 0 || ColumnFields.Count == 0 || Measures.Count == 0 || Cells.Count > 0)
                return "Calculated members require row and column groups, summaries and unmerged grid cells.";
            if (CalculatedMembers.Count > 128 || CalculatedMembers.Any(member => member.GroupLevel < 1
                || member.GroupLevel > (member.IsRow ? RowFields.Count : ColumnFields.Count) || member.Values.Count != Measures.Count))
                return "Calculated-member group levels or summary formulas are unavailable.";
            foreach (var expression in CalculatedMembers.SelectMany(member => member.Formulas))
            {
                try
                {
                    var formula = expression.Compile();
                    if (formula.UnknownSymbols.Count > 0 || formula.UsesPageContext || formula.UsesRecordContext || formula.UsesPersistentVariables || formula.UsesAggregates
                        || formula.References.Any(reference => !reference.StartsWith("{?", StringComparison.Ordinal)))
                        return "Calculated-member formulas require grid values, not record, page or persistent-variable context.";
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                { return "Invalid calculated-member formula: " + error.Message; }
            }
        }
        foreach (var condition in NativeCrossTab?.Templates.SelectMany(template => template.Style.Conditions) ?? [])
        {
            try
            {
                var formula = condition.Formula.Compile();
                if (!ReportCrossTabFormatting.IsSupported(condition.Property) || formula.UnknownSymbols.Count > 0 || formula.UsesPageContext
                    || formula.UsesRecordSequenceContext || (!formula.HasSelfContainedVariables() && !(NativeCrossTab?.StackFormats.Length > 0
                    && condition.Property == "CurrencySymbol" && !formula.WritesPersistentVariables && !formula.UsesSharedVariables)) || formula.UsesAggregates)
                    return "Unsupported cross-tab conditional property or evaluation context: " + condition.Property;
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            { return "Invalid cross-tab formatting condition: " + error.Message; }
        }
        if (Groups.Count > 6 || Groups.Select(group => group.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Groups.Count)
            return "Cross-tab grouping rules must be unique per axis field.";
        foreach (var group in Groups)
        {
            if (kind != "CrossTab" || !RowFields.Concat(ColumnFields).Contains(group.Field, StringComparer.OrdinalIgnoreCase)
                || group.UnspecifiedValues is < 0 or > 2 || group.SpecifiedGroups.Count > 10000)
                return "Invalid cross-tab grouping rule.";
            foreach (var specified in group.SpecifiedGroups)
            {
                try
                {
                    var formula = CrystalFormula.Compile(specified.Expression);
                    if (formula.IsEmpty || formula.UnknownSymbols.Count > 0 || formula.UsesPageContext || formula.UsesRecordContext || formula.UsesGridContext
                        || formula.UsesAggregates || formula.UsesPersistentVariables || formula.RequiresPrintPass)
                        return "Cross-tab specified groups require record-time selection formulas.";
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                { return "Invalid cross-tab group formula: " + error.Message; }
            }
        }
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
        if (Measures.Any(m => m.DoNotSummarize) && (kind != "Chart" || EachRecord)) return "Unsummarized values require an aggregated chart category.";
        if (CategoryFormat?.Length > 128) return "The chart category format is too long.";
        if (Measures.Count == 0 || Measures.Any(m => string.IsNullOrWhiteSpace(m.Field))) return "Choose at least one measure field.";
        if (Measures.Any(m => m.Aggregate == ReportAggregateType.WeightedAverage && (kind != "CrossTab" || m.WeightField.Length == 0))) return "A weighted cross-tab measure needs a weight field.";
        if (Measures.Any(m => m.Aggregate is not (ReportAggregateType.Sum or ReportAggregateType.Count or ReportAggregateType.Average or ReportAggregateType.Min or ReportAggregateType.Max or ReportAggregateType.DistinctCount or ReportAggregateType.Median or ReportAggregateType.WeightedAverage)))
            return "This item supports Sum, Count, DistinctCount, Average, Median, Min and Max. Percentage summaries need a denominator.";
        if (kind == "Chart" && !EachRecord && !SummariesAsCategories && string.IsNullOrWhiteSpace(CategoryField)) return "Choose a category field.";
        if (GroupKinds.Values.Any(groupKind => groupKind is < 0 or > 11)) return "Analytical group periods must be a Crystal date or time condition.";
        if (GroupKinds.Any(pair => pair.Value != 0 && Measures.Any(measure => measure.Field.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))))
            return "A field grouped by a date or time period cannot also be summarized.";
        if (kind == "Chart" && SummariesAsCategories && (EachRecord || CategoryField.Length > 0 || SeriesField.Length > 0)) return "A summary chart draws its measures without a category, series or each-record field.";
        if (kind == "Chart" && ChartType is not (ChartType.Bar or ChartType.StackedBar or ChartType.StackedBar100 or ChartType.Line or ChartType.Pie or ChartType.Donut or ChartType.Surface or ChartType.Ohlc or ChartType.HorizontalBar or ChartType.Area or ChartType.Radar or ChartType.Funnel or ChartType.Gauge or ChartType.Scatter or ChartType.Bubble)) return "Unsupported report chart type.";
        if (kind == "Chart" && ChartType is ChartType.Scatter or ChartType.Bubble
            && (SummariesAsCategories || Measures.Count != (ChartType == ChartType.Bubble ? 3 : 2)))
            return "Choose X and Y measures for a scatter plot, plus a size measure for a bubble plot.";
        if (kind == "Chart" && ChartType is ChartType.Pie or ChartType.Donut && !SummariesAsCategories && (Measures.Count != 1 || SeriesField.Length > 0)) return "Pie charts require one measure and no series field.";
        if (kind == "Chart" && EachRecord && SeriesField.Length > 0) return "Each-record charts cannot also aggregate a series grouping.";
        if (kind == "CrossTab" && ((RowFields.Count == 0 && NativeCrossTab is not { ColumnSumFormat.Length: > 0 }) || RowFields.Any(string.IsNullOrWhiteSpace) || ColumnFields.Any(string.IsNullOrWhiteSpace))) return "Choose the cross-tab row fields.";
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
        RowRanking = RowRanking, AccumulatorVariable = AccumulatorVariable, AccumulatorCount = AccumulatorCount, AccumulatorWithTies = AccumulatorWithTies,
        Title = Title, PrintAxis = PrintAxis, PerspectivePie = PerspectivePie, ChartType = ChartType, ChartThreeDimensional = ChartThreeDimensional, ChartCategoryLegend = ChartCategoryLegend, RightCategoryLegend = RightCategoryLegend, CategoryField = CategoryField, CategoryFormat = CategoryFormat, SeriesField = SeriesField, GroupScope = GroupScope,
        RowFields = [.. RowFields], ColumnFields = [.. ColumnFields], Measures = Measures.Select(m => m.Clone()).ToList(), ShowLegend = ShowLegend, ShowTotals = ShowTotals,
        EachRecord = EachRecord, ReportGroupChart = ReportGroupChart, SummariesAsCategories = SummariesAsCategories, SortCategories = SortCategories, DescendingFields = [.. DescendingFields], OriginalOrderFields = [.. OriginalOrderFields],
        GroupKinds = new Dictionary<string, int>(GroupKinds, StringComparer.OrdinalIgnoreCase),
        Groups = Groups.Select(group => group.Clone()).ToList(),
        Cells = Cells.Select(cell => cell.Copy(cell.Region?.CloneNode())).ToList(),
        TableColumns = TableColumns.Select(column => column.Clone()).ToList(), RepeatHeaders = RepeatHeaders,
        NativeCrossTab = NativeCrossTab, CalculatedMembers = CalculatedMembers.Select(member => member with { Values = member.Values.ToArray() }).ToList(),
        RowHeaderWidthTwips = RowHeaderWidthTwips, ValueColumnWidthTwips = ValueColumnWidthTwips, RowHeightTwips = RowHeightTwips
    };

    internal XElement ToXml()
    {
        _ = Tree().ToArray();
        return ToXmlNode();
    }
    private XElement ToXmlNode() => new("FlexKitAnalysis", new XAttribute("Title", Title), new XAttribute("ChartType", ChartType),
        PerspectivePie ? new XAttribute("PerspectivePie", true) : null,
        ChartThreeDimensional ? new XAttribute("ChartThreeDimensional", true) : null,
        ChartCategoryLegend ? new XAttribute("ChartCategoryLegend", true) : null,
        RightCategoryLegend is {} key ? new XElement("RightCategoryLegend", new XAttribute("CategoryTitle", key.CategoryTitle),
            new XAttribute("ValueTitle", key.ValueTitle), new XAttribute("CategoryGrid", key.CategoryGrid), new XAttribute("ValueGrid", key.ValueGrid),
            key.RotateCategories ? new XAttribute("RotateCategories", true) : null,
            key.Drawing is {} paint ? new XElement("BarDrawing", new XAttribute("Palette", paint.Palette), new XAttribute("Background", paint.Background),
                paint.GradientTop is null ? null : new XAttribute("TopColor", paint.GradientTop),
                paint.GradientBottom is null ? null : new XAttribute("BottomColor", paint.GradientBottom),
                paint.Border is null ? null : new XAttribute("Border", paint.Border),
                paint.Frame is {} box ? new XElement("Frame", new XAttribute("Left", box.Left), new XAttribute("Top", box.Top),
                    new XAttribute("Right", box.Right), new XAttribute("Bottom", box.Bottom)) : null) : null) : null,
        RowRanking is { } rank ? new XElement("RowRanking", new XAttribute("Count", rank.Count),
            new XAttribute("Bottom", rank.Bottom), new XAttribute("WithTies", rank.WithTies)) : null,
        AccumulatorVariable.Length == 0 ? null : new XElement("RankedAccumulator", new XAttribute("Variable", AccumulatorVariable), new XAttribute("Count", AccumulatorCount), new XAttribute("WithTies", AccumulatorWithTies)),
        new XAttribute("CategoryField", CategoryField), string.IsNullOrEmpty(CategoryFormat) ? null : new XAttribute("CategoryFormat", CategoryFormat), new XAttribute("SeriesField", SeriesField), new XAttribute("GroupScope", GroupScope),
        new XAttribute("ShowLegend", ShowLegend), new XAttribute("ShowTotals", ShowTotals),
        new XAttribute("EachRecord", EachRecord), ReportGroupChart ? new XAttribute("ReportGroupChart", true) : null, SummariesAsCategories ? new XAttribute("SummariesAsCategories", true) : null, new XAttribute("SortCategories", SortCategories), new XElement("DescendingFields", DescendingFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        OriginalOrderFields.Count == 0 ? null : new XElement("OriginalOrderFields", OriginalOrderFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        GroupKinds.Count == 0 ? null : new XElement("GroupKinds", GroupKinds.Select(pair => new XElement("Field", new XAttribute("Binding", pair.Key), new XAttribute("Kind", pair.Value)))),
        new XElement("Cells", Cells.Select(cell => new XElement("Cell", new XAttribute("Row", cell.Row), new XAttribute("Column", cell.Column),
            new XAttribute("RowSpan", cell.RowSpan), new XAttribute("ColumnSpan", cell.ColumnSpan), new XAttribute("RegionKind", cell.RegionKind),
            cell.Text is null ? null : new XElement("Text", cell.Text), cell.Region?.ToXmlNode()))),
        new XElement("Groups", Groups.Select(group => group.ToXml())),
        NativeCrossTab?.ToXml(), CalculatedMembers.Count == 0 ? null : new XElement("CalculatedMembers", CalculatedMembers.Select(member => member.ToXml())),
        new XAttribute("RepeatHeaders", RepeatHeaders), new XAttribute("RowHeaderWidthTwips", RowHeaderWidthTwips),
        new XAttribute("ValueColumnWidthTwips", ValueColumnWidthTwips), new XAttribute("RowHeightTwips", RowHeightTwips),
        new XElement("TableColumns", TableColumns.Select(column => new XElement("Column", new XAttribute("Field", column.Field),
            new XAttribute("Caption", column.Caption), new XAttribute("Format", column.Format), new XAttribute("WidthTwips", column.WidthTwips)))),
        PrintAxis is not {} axis ? null : new XElement("PrintAxis",new XAttribute("Min",axis.Minimum),new XAttribute("Max",axis.Maximum),
            new XAttribute("Intervals",axis.Intervals),new XAttribute("Format",axis.Format),new XAttribute("CategoryTitle",axis.CategoryTitle),
            new XAttribute("ValueTitle",axis.ValueTitle),new XAttribute("Grid",axis.Grid),
            axis.Frame is {} frame ? new XElement("Frame",new XAttribute("Left",frame.Left),new XAttribute("Top",frame.Top),
                new XAttribute("Right",frame.Right),new XAttribute("Bottom",frame.Bottom)) : null),
        new XElement("Rows", RowFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Columns", ColumnFields.Select(field => new XElement("Field", new XAttribute("Binding", field)))),
        new XElement("Measures", Measures.Select(m => new XElement("Measure", new XAttribute("Field", m.Field), new XAttribute("Caption", m.Caption),
            m.WeightField.Length > 0 ? new XAttribute("WeightField", m.WeightField) : null, new XAttribute("Aggregate", m.Aggregate), new XAttribute("Format", m.Format), m.DoNotSummarize ? new XAttribute("DoNotSummarize", true) : null))));

    internal static ReportAnalysisDefinition? Read(XElement source)
    {
        if (source.Element("FlexKitAnalysis") is not { } xml) return null;
        if (xml.DescendantsAndSelf("FlexKitAnalysis").Count() > 4096 || xml.Ancestors("FlexKitAnalysis").Count() >= 16)
            throw new InvalidDataException("Nested report regions exceed the 16-level/4096-region safety limit.");
        return new()
        {
            Title = (string?)xml.Attribute("Title") ?? "",
            ChartType = Enum.TryParse<ChartType>((string?)xml.Attribute("ChartType"), out var chart) ? chart : (ChartType)(-1),
            PerspectivePie = (bool?)xml.Attribute("PerspectivePie") ?? false,
            PrintAxis = xml.Element("PrintAxis") is {} axis ? new((double)axis.Attribute("Min")!, (double)axis.Attribute("Max")!,
                (int)axis.Attribute("Intervals")!, (string?)axis.Attribute("Format") ?? "", (string?)axis.Attribute("CategoryTitle") ?? "",
                (string?)axis.Attribute("ValueTitle") ?? "", (bool?)axis.Attribute("Grid") ?? true)
                { Frame = axis.Element("Frame") is {} fr ? new((double)fr.Attribute("Left")!, (double)fr.Attribute("Top")!,
                      (double)fr.Attribute("Right")!, (double)fr.Attribute("Bottom")!) : null } : null,
            ChartThreeDimensional = (bool?)xml.Attribute("ChartThreeDimensional") ?? false,
            ChartCategoryLegend = (bool?)xml.Attribute("ChartCategoryLegend") ?? false,
            RightCategoryLegend = xml.Element("RightCategoryLegend") is {} key ? new((string?)key.Attribute("CategoryTitle") ?? "",
                (string?)key.Attribute("ValueTitle") ?? "", (bool?)key.Attribute("CategoryGrid") ?? false, (bool?)key.Attribute("ValueGrid") ?? true)
            {
                RotateCategories = (bool?)key.Attribute("RotateCategories") ?? false,
                Drawing = key.Element("BarDrawing") is {} paint ? new((string?)paint.Attribute("Palette") ?? "", (string?)paint.Attribute("Background") ?? "#ffffff",
                    (string?)paint.Attribute("TopColor"), (string?)paint.Attribute("BottomColor"), (string?)paint.Attribute("Border"),
                    paint.Element("Frame") is {} box ? new((double)box.Attribute("Left")!, (double)box.Attribute("Top")!,
                        (double)box.Attribute("Right")!, (double)box.Attribute("Bottom")!) : null) : null
            } : null,
            RowRanking = xml.Element("RowRanking") is { } rank ? new((int)rank.Attribute("Count")!,
                (bool?)rank.Attribute("Bottom") ?? false, (bool?)rank.Attribute("WithTies") ?? false) : null,
            AccumulatorVariable = (string?)xml.Element("RankedAccumulator")?.Attribute("Variable") ?? "",
            AccumulatorCount = (int?)xml.Element("RankedAccumulator")?.Attribute("Count") ?? 0,
            AccumulatorWithTies = (bool?)xml.Element("RankedAccumulator")?.Attribute("WithTies") ?? false,
            CategoryField = (string?)xml.Attribute("CategoryField") ?? "", CategoryFormat = (string?)xml.Attribute("CategoryFormat"), SeriesField = (string?)xml.Attribute("SeriesField") ?? "",
            GroupScope = (string?)xml.Attribute("GroupScope") ?? "",
            ShowLegend = !bool.TryParse((string?)xml.Attribute("ShowLegend"), out var legend) || legend,
            ShowTotals = !bool.TryParse((string?)xml.Attribute("ShowTotals"), out var totals) || totals,
            ReportGroupChart = (bool?)xml.Attribute("ReportGroupChart") ?? false,
            EachRecord = bool.TryParse((string?)xml.Attribute("EachRecord"), out var eachRecord) && eachRecord,
            SummariesAsCategories = bool.TryParse((string?)xml.Attribute("SummariesAsCategories"), out var summariesAsCategories) && summariesAsCategories,
            SortCategories = bool.TryParse((string?)xml.Attribute("SortCategories"), out var sortCategories) && sortCategories,
            DescendingFields = xml.Element("DescendingFields")?.Elements("Field").Select(field => (string?)field.Attribute("Binding") ?? "").ToList() ?? [],
            Groups = xml.Element("Groups")?.Elements("Group").Select(ReportAnalysisGroup.Read).ToList() ?? [],
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
            NativeCrossTab = ReportNativeCrossTabLayout.Read(xml.Element("NativeCrossTab")),
            CalculatedMembers = xml.Element("CalculatedMembers")?.Elements("Member").Select(ReportCalculatedMember.Read).ToList() ?? [],
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
                WeightField = (string?)m.Attribute("WeightField") ?? "", Aggregate = Enum.TryParse<ReportAggregateType>((string?)m.Attribute("Aggregate"), out var operation) ? operation : (ReportAggregateType)(-1),
                Format = (string?)m.Attribute("Format") ?? "N2", DoNotSummarize = (bool?)m.Attribute("DoNotSummarize") ?? false
            }).ToList() ?? []
        };
    }
}

public sealed record ReportAnalysisSnapshot(ReportAnalysisDefinition Definition, IReadOnlyList<Dictionary<string, object>> Rows)
{
    public ReportTabularData? Table { get; init; }
    public DateTime PrintTime { get; init; } = DateTime.Now;
}
