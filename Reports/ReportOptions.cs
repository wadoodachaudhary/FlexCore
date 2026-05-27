namespace Fx.ControlKit.Reports;

/// <summary>
/// Global configuration for the report-rendering pipeline. Every knob that
/// would otherwise be hardcoded to a specific report or installation lives
/// here so a single set of options drives all reports rendered through
/// <see cref="ReportWriterControl"/>.
///
/// <para>
/// Register as a singleton with the host app's DI container; inject into
/// <see cref="CrystalXmlReportLoader"/> and <see cref="ReportWriterControl"/>.
/// </para>
/// </summary>
public class ReportOptions
{
    /// <summary>
    /// SQL schema prefix for tables (e.g. <c>dbo</c>, <c>hf</c>, or empty). The loader
    /// emits <c>{Schema}.{TableName}</c> in the <c>FROM</c> and <c>JOIN</c> clauses.
    /// Set to <c>""</c> to omit the prefix entirely (use the default schema of the
    /// connection's user).
    /// </summary>
    public string SchemaPrefix { get; set; } = "dbo";

    /// <summary>
    /// Parameter names that the renderer should auto-inject from session state
    /// (via <see cref="IReportSessionContext"/>) when the report's SQL
    /// references them but the caller didn't supply a value. Comparison is
    /// case-insensitive. Empty by default — the host populates this with its
    /// own well-known session keys (e.g. a multi-tenancy id, user id, locale).
    ///
    /// <para>Example wiring in the host's <c>Program.cs</c>:</para>
    /// <code>
    /// builder.Services.AddSingleton(new ReportOptions
    /// {
    ///     SessionAutoInjectParameters = { "TenantId", "UserId" }
    /// });
    /// </code>
    /// </summary>
    public List<string> SessionAutoInjectParameters { get; set; } = new();

    /// <summary>
    /// Maximum rows a report can return before the renderer aborts with a friendly error.
    /// Prevents a badly-filtered report from locking up the UI with a million rows.
    /// 0 = unlimited.
    /// </summary>
    public int MaxRowsPerReport { get; set; } = 100_000;

    /// <summary>
    /// When true, drill-down-style reports also select <em>all</em> group-level columns
    /// (visible + hidden) in the Main tab so the group tree can show every drill target
    /// without a separate query. Default false — uses the lighter-weight separate
    /// <c>TreeSql</c> approach.
    /// </summary>
    public bool EagerGroupColumnsInMainQuery { get; set; } = false;
}
