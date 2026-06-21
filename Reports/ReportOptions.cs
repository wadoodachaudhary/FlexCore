namespace Fx.ControlKit.Reports;

public class ReportOptions
{
    public string SchemaPrefix { get; set; } = "dbo";

    public List<string> SessionAutoInjectParameters { get; set; } = new();

    public int MaxRowsPerReport { get; set; } = 100_000;

    public bool EagerGroupColumnsInMainQuery { get; set; } = false;
}
