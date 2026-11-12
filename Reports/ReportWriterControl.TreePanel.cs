namespace Fx.ControlKit.Reports;

// The group tree sits on a slider panel beside the pages, as in Crystal's viewer: dragging the bar, or the arrow keys
// on it, sizes the tree within these bounds. Crystal's viewer keeps that width for its own life and saves nothing, so
// the width lives with this viewer: a refresh, a drill-down tab or Hide/Show Tree keep it; a new viewer starts at the
// default.
public partial class ReportWriterControl
{
    private const double DefaultGroupTreeWidth = 250;
    private const double MinGroupTreeWidth = 120;
    private const string MaxGroupTreeShare = "60%";
    private const string MinPagePaneWidth = "200px";

    private double _groupTreeWidth = DefaultGroupTreeWidth;

    private bool GroupTreeShown => ShowGroupTree && CanShowGroupTree;

    // A hidden tree collapses its pane and the bar; the page pane is not rebuilt.
    private string GroupTreeSplitCss => GroupTreeShown ? "fx-rpt-tree-split" : "fx-rpt-tree-split fx-rpt-tree-hidden";
}
