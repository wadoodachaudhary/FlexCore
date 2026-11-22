namespace Fx.ControlKit.Reports;

public sealed partial record ReportPageEstimateWeights
{
    // Fitted 2026-11-19 on 1,993 full runs: sample reports at original size, with 100 times the groups and with 100 times the rows (1,095 runs), and 898 runs of nineteen business reports with their standard parameters and with every parameter value selected.
    // Generated from the fit; change the fit, not this file.
    private static readonly ReportPageEstimateWeights Fitted = new()
    {
        Growth = new Dictionary<string, double>
        {
            ["crosstab"] = 1.0,
            ["growing"] = 1.0,
            ["plain"] = 1.0,
            ["subreport"] = 1.0,
            ["suppress"] = 1.0,
        },
        ConditionalBreakRate = 0.0,
        Range = new Dictionary<string, double>
        {
            ["0|agree|complex"] = 0.3882,
            ["0|agree|plain"] = 0.005,
            ["0|differ|complex"] = 0.4462,
            ["0|differ|plain"] = 0.0723,
            ["1-9|agree|complex"] = 0.0226,
            ["1-9|agree|plain"] = 0.005,
            ["1-9|differ|complex"] = 0.0586,
            ["1-9|differ|plain"] = 0.5306,
            ["10-49|agree|complex"] = 0.0143,
            ["10-49|agree|plain"] = 0.005,
            ["10-49|differ|complex"] = 0.0194,
            ["10-49|differ|plain"] = 0.0488,
            ["100-149|agree|complex"] = 0.0086,
            ["100-149|agree|plain"] = 0.005,
            ["100-149|differ|complex"] = 0.005,
            ["150+|agree|complex"] = 0.005,
            ["150+|agree|plain"] = 0.005,
            ["150+|differ|complex"] = 0.005,
            ["50-99|agree|complex"] = 0.0114,
            ["50-99|agree|plain"] = 0.005,
            ["50-99|differ|complex"] = 0.005,
        }
    };
}
