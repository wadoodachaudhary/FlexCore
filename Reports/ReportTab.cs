namespace Fx.ControlKit.Reports;

/// <summary>
/// One "tab" in the report viewer — either the main report or a drill-down view.
/// Mirrors Crystal Reports' tabbed drill-down: clicking a node in the group tree
/// opens a new tab filtered to that group's rows with more-detailed columns.
/// </summary>
public class ReportTab
{
    /// <summary>Title shown on the tab (e.g. "Main Report", "ANDAV01C01", "400 Drywall").</summary>
    public string Title { get; set; } = "";

    /// <summary>Drill-down path for this tab — empty for the main report.</summary>
    public List<DrillDownFilter> Path { get; set; } = new();

    /// <summary>Rendered HTML pages for this tab.</summary>
    public List<string> Pages { get; set; } = new();

    /// <summary>Currently-viewed page index (1-based) within this tab.</summary>
    public int CurrentPage { get; set; } = 1;

    /// <summary>Group tree (hierarchical) for this tab's data.</summary>
    public List<HfGroupNode> Tree { get; set; } = new();

    /// <summary>Orientation for this tab's pages (some drill-downs may differ).</summary>
    public ReportOrientation Orientation { get; set; } = ReportOrientation.Portrait;
}
