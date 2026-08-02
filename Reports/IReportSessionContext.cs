namespace Fx.ControlKit.Reports;

/// <summary>
/// Supplies session-scoped values that the report renderer auto-injects when
/// the executing SQL references them but no caller value was provided.
///
/// <para>
/// The host implements this against whatever its session model exposes
/// (tenant id, user id, locale, etc.). The control consults
/// <see cref="ReportOptions.SessionAutoInjectParameters"/> to know which
/// keys to ask for; this interface just turns a key into a value.
/// </para>
/// </summary>
public interface IReportSessionContext
{
    /// <summary>
    /// Returns the current session value for the given parameter name, or
    /// <c>null</c> if the host doesn't recognize it (or has nothing to bind).
    /// </summary>
    object? Get(string parameterName);
}
