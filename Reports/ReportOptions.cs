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
    /// <summary>Optional host policy for authored custom SQL. Throws before a designer preview or viewer can execute it.</summary>
    public Action<string>? ValidateCustomSql { get; set; }

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

    /// <summary>
    /// Which failures of an inline subreport's query still let a positioned report print. When this returns true
    /// for the exception the subreport data executor threw, the subreport object prints "[Subreport error: ...]"
    /// in place of its records, the run lists it (<see cref="ReportLayoutResult.SubreportErrors"/>) and the same
    /// query is not sent again in that run. Null (the default) accepts nothing: the failure ends the report, as any
    /// other report error does.
    ///
    /// <para>
    /// <see cref="ReportWriterControl"/> passes it to every positioned run it shows. A host that creates
    /// <see cref="ReportLayoutSession"/> itself decides per run whether to pass it. Accept only failures a retry
    /// cannot change (an object the database does not have, for example); a timeout, deadlock or lost connection
    /// should still fail the report.
    /// </para>
    /// </summary>
    public Func<Exception, bool>? TolerateSubreportQueryError { get; set; }
}
