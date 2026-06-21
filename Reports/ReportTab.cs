namespace Fx.ControlKit.Reports;

public class ReportTab
{
    public string Title { get; set; } = "";

    public List<DrillDownFilter> Path { get; set; } = new();

    public string SourceXmlPath { get; set; } = "";

    public List<ReportFieldFilter> FieldFilters { get; set; } = new();

    public List<string> Pages { get; set; } = new();

    public int CurrentPage { get; set; } = 1;

    public List<FxGroupNode> Tree { get; set; } = new();

    public ReportOrientation Orientation { get; set; } = ReportOrientation.Portrait;
}
