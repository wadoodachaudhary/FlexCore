namespace Fx.ControlKit.Reports;

public interface IReportSessionContext
{
    object? Get(string parameterName);
}
