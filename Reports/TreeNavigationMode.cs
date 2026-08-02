namespace Fx.ControlKit.Reports;

/// <summary>
/// Behavior of the report viewer's group-tree when the user clicks a node.
/// Pages that host <see cref="ReportWriterControl"/> expose a small toolbar
/// control that lets the user toggle between the two modes.
/// </summary>
public enum TreeNavigationMode
{
    /// <summary>
    /// Default Crystal-Reports behavior: clicking a node opens a new tab
    /// filtered to that group's rows (re-queries the XML with the drill
    /// path appended to the WHERE clause).
    /// </summary>
    DrillDown,

    /// <summary>
    /// Navigation-only mode: clicking a node does NOT filter the report.
    /// Instead the viewer scrolls to the matching group-header row on the
    /// active tab and highlights it in red (persistent highlight — moves
    /// to the next-clicked node, or cleared explicitly).
    /// </summary>
    Group
}
