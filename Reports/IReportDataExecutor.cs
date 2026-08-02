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
}
