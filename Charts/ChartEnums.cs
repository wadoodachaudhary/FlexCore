namespace Fx.ControlKit.Charts;

public enum ChartType
{
    // Comparison
    Bar, HorizontalBar, GroupedBar, StackedBar, StackedBar100,
    // Trend over time
    Line, MultiLine, Spline, StepLine, Area, StackedArea, Sparkline,
    // Part-to-whole
    Pie, Donut, Treemap, Funnel, Pyramid,
    // Distribution
    Histogram, BoxPlot,
    // Correlation
    Scatter, Bubble, Heatmap,
    // Ranking
    DotPlot,
    // Multivariate
    Radar,
    // Single value / KPI & Gauges
    Gauge, KpiCard, LinearGauge, ArcGauge, RadialGauge, Bullet,
    // Financial
    Candlestick, Ohlc, HighLow,
    // Range intervals
    RangeBar, RangeColumn, RangeArea,
    // Flow & Project
    Sankey, Gantt,
    // Statistical
    Waterfall, Pareto,
    // Combination
    ComboBarLine
}
