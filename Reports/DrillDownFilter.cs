namespace Fx.ControlKit.Reports;

/// <summary>
/// A single drill-down filter — one step of the path from the "Main Report"
/// to a specific group value. Used when the user clicks a node in the
/// report's group tree to open a Crystal-Reports-style drill-down tab.
///
/// <para>
/// Each step is a <c>(field, value)</c> pair where <c>field</c> is the alias
/// emitted by the loader for that group level (e.g.
/// <c>{tableAlias}_{columnName}</c>) and <c>value</c> is the raw DB value at
/// the clicked node. The renderer joins the steps with <c>AND</c> to widen
/// the report's <c>WHERE</c> clause for the new tab.
/// </para>
/// </summary>
public sealed record DrillDownFilter(string Field, string Value);
