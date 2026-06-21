using System.Data;

namespace Fx.ControlKit.Reports;

public interface IReportDataExecutor
{
    DataTable Execute(string sql, IDictionary<string, object>? parameters);
}
