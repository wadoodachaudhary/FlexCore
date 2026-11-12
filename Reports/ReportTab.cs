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
    public ReportDesignerPage? PositionedPage { get; set; }
    /// <summary>Owned native execution snapshot used for group drill-down without rerunning the main query.</summary>
    public ReportLayoutSession? NativeSession { get; set; }
    internal string NativeGroupId { get; set; } = "";

    public List<string> RuntimeDiagnostics { get; set; } = new();

    /// <summary>Drill-down path for this tab — empty for the main report.</summary>
    public List<DrillDownFilter> Path { get; set; } = new();

    /// <summary>Source XML for a subreport tab. Empty for main/drill-down tabs.</summary>
    public string SourceXmlPath { get; set; } = "";

    /// <summary>Direct table-field filters applied to a subreport tab.</summary>
    public List<ReportFieldFilter> FieldFilters { get; set; } = new();

    /// <summary>Rendered HTML pages for this tab.</summary>
    public List<string> Pages { get; set; } = new();
    public bool IsPaginationComplete { get; set; } = true;
    public List<ReportPageSnapshot> PageSnapshots { get; set; } = [];
    public ReportTextSearch? Search { get; set; }
    /// <summary>The page of the match the user is on; zero when the search has no current match.</summary>
    public int SearchMatchPage { get; set; }
    /// <summary>The current match's index among its page's matches (<see cref="ReportTextSearch.MatchesOn"/>); -1 when none.</summary>
    public int SearchMatchIndex { get; set; } = -1;
    /// <summary>The page whose highlighted rendering <see cref="SearchHighlightHtml"/> holds; null when that page has no matches.</summary>
    public int SearchHighlightPage { get; set; }
    public string? SearchHighlightHtml { get; set; }
    /// <summary>For a page rendered from its snapshot: its objects that carry the marks of <see cref="SearchHighlightHtml"/>.</summary>
    public ReportPageHighlight? SearchHighlight { get; set; }
    /// <summary>Data from this tab's execution snapshot; exporting never reruns its query.</summary>
    public Func<Fx.ControlKit.Grid.GridExportTable>? CreateDataExport { get; set; }

    /// <summary>Currently-viewed page index (1-based) within this tab.</summary>
    public int CurrentPage { get; set; } = 1;

    /// <summary>Group tree (hierarchical) for this tab's data.</summary>
    public List<FxGroupNode> Tree { get; set; } = new();

    /// <summary>Orientation for this tab's pages (some drill-downs may differ).</summary>
    public ReportOrientation Orientation { get; set; } = ReportOrientation.Portrait;

    /// <summary>Print's progress or why it stopped, shown over the status it was set over while that status is unchanged.</summary>
    internal string? PrintNote { get; set; }
    internal string? PrintNoteOver { get; set; }
    /// <summary>The number of the latest Print of this tab (see ReportWriterControl's print).</summary>
    internal int PrintNumber { get; set; }
}
