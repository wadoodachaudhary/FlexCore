namespace Fx.ControlKit.Reports;

/// <summary>
/// A node in the Crystal Reports-style group tree. Each node represents one distinct
/// value of a grouping field at a given nesting level, plus all of its child groups.
///
/// <para>
/// The tree is built from the report's <see cref="ReportDefinition.Groups"/> list and
/// the rows of the executed SQL query. Each leaf (or any intermediate) node knows which
/// page in the rendered HTML contains its first row, so clicking the node navigates the
/// viewer to that page — mirroring <c>CrystalActiveXReportViewer.DisplayGroupTree = True</c>
/// from the VB6 app.
/// </para>
/// </summary>
public class FxGroupNode
{
    /// <summary>
    /// Text shown in the tree (display-friendly — empty DB values appear as "(empty)").
    /// Use <see cref="Value"/> for SQL filter comparisons.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Raw group-field value from the database (empty string for NULL/empty). This is the
    /// value to use when building drill-down WHERE clauses — <see cref="Label"/> may have
    /// been replaced with "(empty)" for display.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>The group-field's name (e.g. <c>"MyTable_GroupField"</c>).</summary>
    public string Field { get; set; } = "";

    /// <summary>Grouping depth — 0 = outermost group.</summary>
    public int Level { get; set; }

    /// <summary>
    /// 1-based index of the rendered page containing this group's first row.
    /// Used to implement click-to-navigate in the report viewer.
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>Anchor id emitted in the rendered HTML (for intra-page scrolling if needed).</summary>
    public string AnchorId { get; set; } = "";

    /// <summary>Child groups (next nesting level down).</summary>
    public List<FxGroupNode> Children { get; set; } = new();

    /// <summary>Whether the node is expanded in the tree UI. Defaults to
    /// collapsed so the report viewer's group tree opens compact — users
    /// click the disclosure toggle to drill in. Mirrors the VB6 Crystal
    /// viewer behavior where the tree showed top-level groups only on
    /// initial load.</summary>
    public bool Expanded { get; set; }
}
