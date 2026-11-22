using System.Data;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Abstraction for the actual SQL execution. <see cref="ReportWriterControl"/>
/// uses this instead of binding to a specific database wrapper, so the same
/// control can be hosted by an app using any data layer (DbWrapperSqlServer,
/// EF Core, Dapper, etc.).
///
/// <para>
/// The host registers a concrete implementation in its DI container; the
/// control resolves it via <c>@inject</c>.
/// </para>
/// </summary>
public interface IReportDataExecutor
{
    /// <summary>
    /// Executes <paramref name="sql"/> with the supplied named parameters and
    /// returns the result set as a <see cref="DataTable"/>.
    /// </summary>
    /// <param name="sql">A parameterized SQL query.</param>
    /// <param name="parameters">
    /// Map of parameter name → value. Names should NOT include a leading '@'
    /// (the implementation prepends one if its database needs it).
    /// </param>
    DataTable Execute(string sql, IDictionary<string, object>? parameters);

    /// <summary>
    /// True when every text column the executor's databases hold compares case-insensitively. A record selection then compares
    /// such a column with printable-ASCII values directly, in the column's own collation; otherwise it keeps the explicit
    /// case-insensitive comparison, which finds the same rows under any collation.
    /// </summary>
    bool TextColumnsCompareCaseInsensitively => false;

    /// <summary>
    /// True when <paramref name="column"/>, a column of a table or view in the connected database, compares case-insensitively as
    /// <see cref="TextColumnsCompareCaseInsensitively"/> describes. A record selection compares only such a column directly.
    /// </summary>
    bool TextColumnComparesCaseInsensitively(ReportTextColumn column) => TextColumnsCompareCaseInsensitively;
}

/// <summary>A column a record selection compares: the table or view as the query names it (with its schema when it names one)
/// and the column's name.</summary>
public sealed record ReportTextColumn(string? Schema, string Table, string Column);

/// <summary>Optional structured execution for hosts that supply report-specific data fixtures.</summary>
public interface IReportDefinitionDataExecutor : IReportDataExecutor
{
    DataTable Execute(ReportDefinition definition, IDictionary<string, object>? parameters);
}

/// <summary>Optional non-blocking, cancellable query execution. Sorting and report summaries still
/// consume a complete record snapshot, unlike an unordered grid row window.</summary>
public interface IAsyncReportDataExecutor : IReportDataExecutor
{
    Task<DataTable> ExecuteAsync(string sql, IDictionary<string, object>? parameters, CancellationToken cancellationToken);
}

/// <summary>What the data executor declares about its databases when a report definition prepares its SQL.</summary>
/// <param name="CaseInsensitiveTextColumns">Every text column compares case-insensitively (see <see cref="IReportDataExecutor.TextColumnsCompareCaseInsensitively"/>).</param>
public sealed record ReportSqlExecutionOptions(bool CaseInsensitiveTextColumns)
{
    /// <summary>Which columns compare case-insensitively (see <see cref="IReportDataExecutor.TextColumnComparesCaseInsensitively"/>).
    /// Null: every column of a table or view does when <see cref="CaseInsensitiveTextColumns"/> is true.</summary>
    public Func<ReportTextColumn, bool>? CaseInsensitiveColumn { get; init; }

    /// <summary>The query is sent with only the parameters its SQL names (<see cref="ReportSqlParameters"/>); a structured
    /// executor receives them all.</summary>
    public bool SendsOnlyReadParameters { get; init; }

    /// <summary>The options a data executor declares.</summary>
    public static ReportSqlExecutionOptions For(IReportDataExecutor executor) =>
        new(executor.TextColumnsCompareCaseInsensitively)
        {
            CaseInsensitiveColumn = executor.TextColumnComparesCaseInsensitively,
            SendsOnlyReadParameters = executor is not IReportDefinitionDataExecutor
        };
}
