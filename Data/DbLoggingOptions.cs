namespace Fx.ControlKit.Data;

/// <summary>
/// Controls what gets logged for database operations.
/// </summary>
public sealed class DbLoggingOptions
{
    /// <summary>Log the SQL text in warning/error messages.</summary>
    public bool LogSqlText { get; set; } = true;

    /// <summary>Log parameter names/values (caution: may expose sensitive data).</summary>
    public bool LogParameters { get; set; }

    /// <summary>Threshold in milliseconds above which a query is logged as a slow-query warning. Zero disables.</summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;
}
