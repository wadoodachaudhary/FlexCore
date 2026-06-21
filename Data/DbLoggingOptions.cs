namespace Fx.ControlKit.Data;

public sealed class DbLoggingOptions
{
    public bool LogSqlText { get; set; } = true;

    public bool LogParameters { get; set; }

    public int SlowQueryThresholdMs { get; set; } = 1000;
}
