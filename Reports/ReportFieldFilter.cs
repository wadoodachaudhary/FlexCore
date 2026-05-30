namespace Fx.ControlKit.Reports;

/// <summary>
/// A direct table-field filter used when opening Crystal subreports from a clicked row.
/// </summary>
public sealed record ReportFieldFilter(string Table, string Field, string Value);
